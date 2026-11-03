using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.OnlineCinema.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OnlineCinema.Services;

/// <summary>
/// Produces actual STRM files inside an existing Jellyfin library. Playback
/// runs through Jellyfin's normal STRM/FFmpeg pipeline, never a virtual media
/// item or a custom HTML5 player.
/// </summary>
public sealed class StrmLibraryService : IDisposable
{
    private readonly ILibraryManager _library;
    private readonly ILibraryMonitor _monitor;
    private readonly ISessionManager _sessions;
    private readonly NativePlaybackRegistry _catalog;
    private readonly TorrServerService _torr;
    private readonly ILogger<StrmLibraryService> _logger;
    private readonly ConcurrentDictionary<Guid, string> _selectedPaths = new();
    private readonly ConcurrentDictionary<string, byte> _temporaryPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _writes = new(1, 1);
    private static readonly Regex SeasonEpisode = new(
        @"(?i)(?:^|[^a-z0-9])S(?<season>\d{1,2})[\s._-]*E(?<episode>\d{1,3})(?:[^a-z0-9]|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AlternateEpisode = new(
        @"(?i)(?:^|[^a-z0-9])(?<season>\d{1,2})x(?<episode>\d{1,3})(?:[^a-z0-9]|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private const int MaxFileNameLength = 115;

    public StrmLibraryService(
        ILibraryManager library,
        ILibraryMonitor monitor,
        ISessionManager sessions,
        NativePlaybackRegistry catalog,
        TorrServerService torr,
        ILogger<StrmLibraryService> logger)
    {
        _library = library;
        _monitor = monitor;
        _sessions = sessions;
        _catalog = catalog;
        _torr = torr;
        _logger = logger;
        _sessions.PlaybackStopped += OnPlaybackStopped;
    }

    public void Dispose()
    {
        _sessions.PlaybackStopped -= OnPlaybackStopped;
        _writes.Dispose();
    }

    private static string CleanName(string? value)
    {
        var text = (value ?? "").Normalize(NormalizationForm.FormC).Trim();
        var invalid = Path.GetInvalidFileNameChars()
            .Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        var cleaned = new string(text.Select(ch =>
            invalid.Contains(ch) || char.IsControl(ch) ? ' ' : ch).ToArray());
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim(' ', '.');
        // ext4/Asustor file names are limited by UTF-8 bytes, not by .NET
        // UTF-16 character count. Russian titles can use two bytes per char.
        var utf8 = new StringBuilder();
        var bytes = 0;
        foreach (var rune in cleaned.EnumerateRunes())
        {
            if (utf8.Length >= MaxFileNameLength || bytes + rune.Utf8SequenceLength > 145)
                break;
            utf8.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }
        cleaned = utf8.ToString().Trim(' ', '.');
        return string.IsNullOrWhiteSpace(cleaned) ? "Без названия" : cleaned;
    }

    private static string MovieTitle(CatalogItem item)
    {
        var year = string.IsNullOrWhiteSpace(item.Year) ? "" : " (" + CleanName(item.Year) + ")";
        return CleanName(item.Title) + year;
    }

    private string ValidatedRoot(bool isSeries)
    {
        var configured = isSeries
            ? Plugin.Instance?.Configuration.StrmSeriesDirectory
            : Plugin.Instance?.Configuration.StrmMoviesDirectory;
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException(
                "Путь STRM для " + (isSeries ? "сериалов" : "фильмов") +
                " не указан в настройках Online Cinema.");
        if (!Path.IsPathFullyQualified(configured))
            throw new InvalidOperationException(
                "Путь должен быть абсолютным путём сервера Jellyfin, например /sharefolder/Video2/test/mov.");

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured.Trim()));
        if (full == Path.GetPathRoot(full))
            throw new InvalidOperationException(
                "Нельзя сохранять STRM в корневой каталог файловой системы.");
        // A path visible in the NAS and a Jellyfin library location can be
        // represented by different aliases/symlinks. Requiring a textual
        // prefix of GetVirtualFolders() rejected valid Asustor paths with 400
        // even before the plugin attempted to create a file. Library indexing
        // is validated separately, after creation.
        if (!Directory.Exists(full))
            throw new InvalidOperationException(
                "Папка STRM недоступна процессу Jellyfin: " + full +
                ". Проверь путь внутри окружения Jellyfin и доступ к каталогу.");
        return full;
    }

    private bool IsInsideJellyfinLibrary(string fullPath)
    {
        try
        {
            return _library.GetVirtualFolders().Any(v => v.Locations.Any(location =>
            {
                if (string.IsNullOrWhiteSpace(location)) return false;
                var registered = Path.TrimEndingDirectorySeparator(Path.GetFullPath(location));
                return fullPath.Equals(registered, StringComparison.OrdinalIgnoreCase) ||
                    fullPath.StartsWith(registered + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Online Cinema: unable to compare STRM path with Jellyfin library locations");
            return false;
        }
    }

    private object InspectFolder(bool isSeries)
    {
        string full;
        try { full = ValidatedRoot(isSeries); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return new { configured = false, exists = false, writable = false,
                inLibrary = false, message = ex.Message };
        }

        var probePath = Path.Combine(full, ".onlinecinema-write-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var probe = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                probe.WriteByte(0x31);
            return new { configured = true, exists = true, writable = true,
                inLibrary = IsInsideJellyfinLibrary(full),
                message = "Путь доступен, тестовый файл создан и удалён. " +
                    "Если папка ещё не включена в медиатеку Jellyfin, добавь её как источник библиотеки." };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new { configured = true, exists = true, writable = false,
                inLibrary = IsInsideJellyfinLibrary(full),
                message = "Jellyfin не может записать файл в папку: " + ex.Message };
        }
        finally
        {
            try { if (File.Exists(probePath)) File.Delete(probePath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { _logger.LogWarning(ex, "Online Cinema: could not remove STRM write probe"); }
        }
    }

    public object Diagnose()
        => new
        {
            movies = InspectFolder(false),
            series = InspectFolder(true),
            torrServerAuthConfigured = !string.IsNullOrWhiteSpace(
                Plugin.Instance?.Configuration.TorrServerUsername),
            note = "STRM создаётся по абсолютному пути, видимому самому серверу Jellyfin. " +
                "Для индексации созданную папку также необходимо добавить в медиатеку."
        };

    public bool IsManagedLibraryItem(BaseItem? item)
    {
        var path = item?.Path;
        if (string.IsNullOrWhiteSpace(path) ||
            !Regex.IsMatch(path, @"\[tmdbid-\d+\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return false;

        foreach (var configured in new[]
        {
            Plugin.Instance?.Configuration.StrmMoviesDirectory,
            Plugin.Instance?.Configuration.StrmSeriesDirectory
        })
        {
            if (string.IsNullOrWhiteSpace(configured) || !Path.IsPathFullyQualified(configured))
                continue;
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));
            var candidate = Path.GetFullPath(path);
            if (candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private string Folder(CatalogItem item)
    {
        var root = ValidatedRoot(item.Type == "tv");
        var title = MovieTitle(item);
        return Path.Combine(root, title + " [tmdbid-" +
            item.Id.ToString(CultureInfo.InvariantCulture) + "]");
    }

    private static (int Season, int Episode) EpisodeNumbers(string videoName)
    {
        var match = SeasonEpisode.Match(videoName);
        if (!match.Success) match = AlternateEpisode.Match(videoName);
        if (!match.Success)
            throw new InvalidOperationException(
                "Не найден номер серии S01E01 в имени видеофайла. " +
                "Для корректного именования сериала выбери файл с обозначением сезона и серии.");
        return (int.Parse(match.Groups["season"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["episode"].Value, CultureInfo.InvariantCulture));
    }

    private string Destination(CatalogItem item, string videoFileName)
    {
        var title = MovieTitle(item);
        var folder = Folder(item);
        if (item.Type != "tv")
            return Path.Combine(folder, title + ".strm");

        var (season, episode) = EpisodeNumbers(videoFileName);
        var seasonFolder = Path.Combine(folder, $"Season {season:00}");
        // Stable filename independent of the release group, so the same
        // episode can be found later from a different catalog session.
        return Path.Combine(seasonFolder,
            CleanName(item.Title) + $" - S{season:00}E{episode:00}.strm");
    }

    private static void EnsureBelowRoot(string fullFile, string root)
    {
        if (!fullFile.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Небезопасный путь создания STRM.");
    }


    /// <summary>
    /// The native Jellyfin player needs a database item ID, NOT a 90-second
    /// library refresh. Create the actual Movie/Episode entry immediately
    /// after writing its STRM. Jellyfin reads the TorrServer URL as the
    /// shortcut path and can transcode it like a normal, indexed STRM.
    /// </summary>
    private Guid? RegisterImmediately(CatalogItem metadata, string filePath,
        string videoFileName, string remoteUrl, string root, bool migratedLegacy)
    {
        var present = _library.FindByPath(filePath, false);
        if (present is not null)
        {
            if (migratedLegacy && present is Video scanned)
            {
                // Upgrade the registered media source too; changing only the
                // on-disk .strm would leave the old URL in Jellyfin's DB.
                try
                {
                    scanned.IsShortcut = true;
                    scanned.ShortcutPath = remoteUrl;
                    scanned.Container = Path.GetExtension(videoFileName).TrimStart('.').ToLowerInvariant();
                    _library.CreateItem(scanned, scanned.GetParent());
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Online Cinema: legacy STRM file migrated; cached movie source will refresh on next scan");
                }
            }
            return present.Id;
        }

        if (!IsInsideJellyfinLibrary(root))
        {
            _logger.LogWarning(
                "Online Cinema: cannot instantly register STRM outside a Jellyfin library: {Root}", root);
            return null;
        }

        // A user may have registered /share/Video2 but configured the same
        // directory through /sharefolder/Video2. Do not create items with
        // unrelated parents: fall back to the standard library scan.
        var libraryFolder = _library.FindByPath(root, true) as Folder;
        if (libraryFolder is null)
        {
            _logger.LogWarning(
                "Online Cinema: Jellyfin library root was not resolved as a Folder: {Root}", root);
            return null;
        }

        try
        {
            var now = DateTime.UtcNow;
            var extension = Path.GetExtension(videoFileName).TrimStart('.').ToLowerInvariant();
            var sharedName = MovieTitle(metadata);
            if (metadata.Type == "movie")
            {
                var movie = new Movie
                {
                    Id = _library.GetNewItemId(filePath, typeof(Movie)),
                    ParentId = libraryFolder.Id,
                    Path = filePath,
                    Name = metadata.Title,
                    OriginalTitle = metadata.OriginalTitle,
                    ProductionYear = int.TryParse(metadata.Year, out var year) ? year : null,
                    IsShortcut = true,
                    ShortcutPath = remoteUrl,
                    VideoType = VideoType.VideoFile,
                    Container = extension,
                    DateCreated = now,
                    DateModified = now,
                    ProviderIds = new Dictionary<string, string>
                    {
                        ["Tmdb"] = metadata.Id.ToString(CultureInfo.InvariantCulture)
                    }
                };
                _library.CreateItem(movie, libraryFolder);
                _logger.LogInformation("Online Cinema: registered STRM Movie instantly {Id} {Path}",
                    movie.Id, filePath);
                return movie.Id;
            }

            var (seasonNumber, episodeNumber) = EpisodeNumbers(videoFileName);
            var seriesPath = Path.GetDirectoryName(Path.GetDirectoryName(filePath)!)!;
            var seasonPath = Path.GetDirectoryName(filePath)!;

            var series = _library.FindByPath(seriesPath, true) as Series;
            if (series is null)
            {
                series = new Series
                {
                    Id = _library.GetNewItemId(seriesPath, typeof(Series)),
                    ParentId = libraryFolder.Id,
                    Path = seriesPath,
                    Name = metadata.Title,
                    OriginalTitle = metadata.OriginalTitle,
                    ProductionYear = int.TryParse(metadata.Year, out var sy) ? sy : null,
                    DateCreated = now,
                    DateModified = now,
                    ProviderIds = new Dictionary<string, string>
                    {
                        ["Tmdb"] = metadata.Id.ToString(CultureInfo.InvariantCulture)
                    }
                };
                _library.CreateItem(series, libraryFolder);
            }

            var season = _library.FindByPath(seasonPath, true) as Season;
            if (season is null)
            {
                season = new Season
                {
                    Id = _library.GetNewItemId(seasonPath, typeof(Season)),
                    ParentId = series.Id,
                    Path = seasonPath,
                    Name = "Season " + seasonNumber.ToString("00", CultureInfo.InvariantCulture),
                    IndexNumber = seasonNumber,
                    SeriesId = series.Id,
                    SeriesName = metadata.Title,
                    DateCreated = now,
                    DateModified = now
                };
                _library.CreateItem(season, series);
            }

            var episode = new Episode
            {
                Id = _library.GetNewItemId(filePath, typeof(Episode)),
                ParentId = season.Id,
                Path = filePath,
                Name = "Серия " + episodeNumber.ToString(CultureInfo.InvariantCulture),
                IndexNumber = episodeNumber,
                ParentIndexNumber = seasonNumber,
                SeriesId = series.Id,
                SeasonId = season.Id,
                SeriesName = metadata.Title,
                IsShortcut = true,
                ShortcutPath = remoteUrl,
                VideoType = VideoType.VideoFile,
                Container = extension,
                DateCreated = now,
                DateModified = now
            };
            _library.CreateItem(episode, season);
            _logger.LogInformation("Online Cinema: registered STRM Episode instantly {Id} {Path}",
                episode.Id, filePath);
            return episode.Id;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException
            or UnauthorizedAccessException or ArgumentException)
        {
            // File creation has already succeeded; never throw away the STRM
            // just because the instant indexing optimization is unavailable.
            _logger.LogWarning(ex,
                "Online Cinema: fast STRM registration unavailable, regular Jellyfin scan will follow");
            return null;
        }
    }

    private StrmEntry Describe(string path)
    {
        var item = _library.FindByPath(path, false);
        return new StrmEntry(Path.GetFileNameWithoutExtension(path),
            item?.Id is Guid id && id != Guid.Empty ? id.ToString("N") : null);
    }

    public object Status(Guid nativeId)
    {
        var entry = _catalog.GetCatalog(nativeId);
        if (entry is null) throw new KeyNotFoundException("Онлайн-карточка истекла. Открой её заново.");
        string folder;
        try { folder = Folder(entry.Details.Item); }
        catch (InvalidOperationException ex)
        {
            return new { configured = false, message = ex.Message, hasStrm = false, entries = Array.Empty<StrmEntry>() };
        }
        if (!Directory.Exists(folder))
            return new { configured = true, message = "", hasStrm = false,
                inLibrary = IsInsideJellyfinLibrary(ValidatedRoot(entry.Details.Item.Type == "tv")),
                entries = Array.Empty<StrmEntry>() };
        // Restrict discovery to our deterministic TMDB folder and never traverse
        // files from unrelated movies/series in the user's Jellyfin library.
        var files = Directory.EnumerateFiles(folder, "*.strm", SearchOption.AllDirectories)
            .Take(250)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        return new
        {
            configured = true,
            message = "",
            inLibrary = IsInsideJellyfinLibrary(ValidatedRoot(entry.Details.Item.Type == "tv")),
            hasStrm = files.Length > 0,
            entries = files.Select(Describe).ToArray()
        };
    }

    public async Task<object> CreateAsync(
        Guid nativeId, string sessionId, int fileIndex, CancellationToken token)
    {
        var entry = _catalog.GetCatalog(nativeId) ??
            throw new KeyNotFoundException("Карточка устарела. Вернись в онлайн-каталог.");

        var metadata = entry.Details.Item;
        if (metadata.Type is not ("movie" or "tv"))
            throw new ArgumentException("Поддерживаются фильмы и сериалы.");

        var root = ValidatedRoot(metadata.Type == "tv");
        if (!string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.TorrServerUsername))
            throw new InvalidOperationException(
                "Для STRM нужна прямая ссылка на TorrServer без Basic Auth. " +
                "Настрой доступ с сервера Jellyfin без пароля (например, через доверенный LAN-прокси). " +
                "Пароль нельзя записывать в STRM.");

        var file = await _torr.SelectAsync(sessionId, fileIndex, token).ConfigureAwait(false);
        var fullPath = Destination(metadata, file.FileName);
        EnsureBelowRoot(fullPath, root);

        // Keep the torrent in TorrServer's DB. Without this, saved STRM
        // files may stop working after TorrServer's idle cleanup/restart.
        await _torr.PersistAsync(sessionId, token).ConfigureAwait(false);
        var url = _torr.GetDirectPlaybackUrl(sessionId, fileIndex);
        var migratedLegacy = false;
        await _writes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var parent = Path.GetDirectoryName(fullPath)!;
            Directory.CreateDirectory(parent);
            if (File.Exists(fullPath))
            {
                var content = (await File.ReadAllTextAsync(fullPath, token).ConfigureAwait(false)).Trim();
                // Only migrate the exact URL generated by older versions of
                // this plugin for the same torrent hash/file index.
                // Never silently replace a user-created STRM or another release.
                if (!content.Equals(url, StringComparison.Ordinal))
                {
                    var oldLink = _torr.GetLegacyPlaybackUrl(sessionId, fileIndex);
                    if (!content.Equals(oldLink, StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            "STRM уже существует и указывает на другую раздачу. " +
                            "Используй кнопку STRM либо удали старую запись вручную.");
                    await File.WriteAllTextAsync(fullPath, url + "\n",
                        new UTF8Encoding(false), token).ConfigureAwait(false);
                    migratedLegacy = true;
                    _logger.LogInformation("Online Cinema: migrated old /play STRM link to /stream for {Path}", fullPath);
                }
            }
            else
            {
                await File.WriteAllTextAsync(fullPath, url + "\n", new UTF8Encoding(false), token).ConfigureAwait(false);
                if (Plugin.Instance?.Configuration.KeepStrmAfterPlayback == false)
                    _temporaryPaths.TryAdd(fullPath, 0);
                _logger.LogInformation("Online Cinema: STRM created {File}", fullPath);
            }
            _selectedPaths[nativeId] = fullPath;
        }
        finally { _writes.Release(); }

        // Notify Jellyfin's native library scanner rather than mocking Items
        // or PlaybackInfo. FindByPath will resolve a genuine library item.
        // Register before notifying the file-system watcher. This avoids
        // waiting for its debounce interval, metadata scan, and refresh queue.
        var immediateId = RegisterImmediately(metadata, fullPath, file.FileName, url, root, migratedLegacy);
        try { _monitor.ReportFileSystemChanged(fullPath); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Online Cinema: STRM created but the library monitor could not be notified");
        }
        var imported = Describe(fullPath);
        var resolvedId = immediateId?.ToString("N") ?? imported.ItemId;
        var inLibrary = IsInsideJellyfinLibrary(root);
        return new { created = true, fileName = imported.Name,
            itemId = resolvedId, waitingForScan = resolvedId is null,
            registeredImmediately = immediateId is not null,
            inLibrary,
            message = immediateId is not null
                ? "STRM зарегистрирован в Jellyfin сразу, без сканирования медиатеки."
                : inLibrary
                    ? "STRM создан. Быстрая регистрация не удалась, ожидание сканирования."
                    : "STRM создан, но путь не совпадает с зарегистрированными медиатеками." };
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs args)
    {
        if (Plugin.Instance?.Configuration.KeepStrmAfterPlayback != false) return;
        var path = args.Item?.Path;
        if (string.IsNullOrEmpty(path) || !_temporaryPaths.ContainsKey(path)) return;
        _ = DeleteAfterPlaybackAsync(path, args.Item!.Id);
    }

    private async Task DeleteAfterPlaybackAsync(string path, Guid itemId)
    {
        // Allow FFmpeg and mobile clients time to close their file handles;
        // don't remove the file while another session is playing it.
        await Task.Delay(TimeSpan.FromSeconds(35)).ConfigureAwait(false);
        for (var i = 0; i < 12; i++)
        {
            if (!_sessions.Sessions.Any(s => s.NowPlayingItem?.Id == itemId)) break;
            await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        if (_sessions.Sessions.Any(s => s.NowPlayingItem?.Id == itemId)) return;
        if (!_temporaryPaths.TryRemove(path, out _)) return;
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                // Remove only empty TMDB folders beneath the configured STRM
                // library root. Never delete another file or a library root.
                RemoveEmptyParents(path);
                _monitor.ReportFileSystemChanged(path);
                _logger.LogInformation("Online Cinema: temporary STRM removed on playback stop {File}", path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Online Cinema: could not remove temporary STRM {File}", path);
        }
    }

    private static void RemoveEmptyParents(string filePath)
    {
        var folders = new[]
        {
            Plugin.Instance?.Configuration.StrmMoviesDirectory,
            Plugin.Instance?.Configuration.StrmSeriesDirectory
        }.Where(x => !string.IsNullOrWhiteSpace(x) && Path.IsPathFullyQualified(x))
            .Select(x => Path.TrimEndingDirectorySeparator(Path.GetFullPath(x!)))
            .ToArray();
        var parent = Path.GetDirectoryName(filePath);
        while (!string.IsNullOrEmpty(parent))
        {
            var root = folders.FirstOrDefault(x =>
                parent.StartsWith(x + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            if (root is null || !Directory.Exists(parent) ||
                Directory.EnumerateFileSystemEntries(parent).Any()) break;
            Directory.Delete(parent);
            parent = Path.GetDirectoryName(parent);
        }
    }

    public sealed record StrmEntry(string Name, string? ItemId);
}
