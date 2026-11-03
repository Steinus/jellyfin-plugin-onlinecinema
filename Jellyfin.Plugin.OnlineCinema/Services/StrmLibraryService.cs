using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.OnlineCinema.Models;
using MediaBrowser.Controller.Entities;
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
        if (string.IsNullOrWhiteSpace(configured) || !Path.IsPathFullyQualified(configured))
            throw new InvalidOperationException(isSeries
                ? "Укажи полный путь папки STRM для сериалов в настройках Online Cinema."
                : "Укажи полный путь папки STRM для фильмов в настройках Online Cinema.");

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));
        var allowed = _library.GetVirtualFolders().Any(v => v.Locations.Any(location =>
        {
            var registered = Path.TrimEndingDirectorySeparator(Path.GetFullPath(location));
            return full.Equals(registered, StringComparison.OrdinalIgnoreCase) ||
                   full.StartsWith(registered + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }));
        if (!allowed)
            throw new InvalidOperationException(
                "Папка STRM не находится внутри подключённой медиатеки Jellyfin. " +
                "Сначала добавь её в Jellyfin как библиотеку " +
                (isSeries ? "«Сериалы»." : "«Фильмы»."));
        return full;
    }

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
            return new { configured = true, message = "", hasStrm = false, entries = Array.Empty<StrmEntry>() };
        // Restrict discovery to our deterministic TMDB folder and never traverse
        // files from unrelated movies/series in the user's Jellyfin library.
        var files = Directory.EnumerateFiles(folder, "*.strm", SearchOption.AllDirectories)
            .Take(250)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        return new
        {
            configured = true,
            message = "",
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
        await _writes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var parent = Path.GetDirectoryName(fullPath)!;
            Directory.CreateDirectory(parent);
            if (File.Exists(fullPath))
            {
                var content = (await File.ReadAllTextAsync(fullPath, token).ConfigureAwait(false)).Trim();
                // Never overwrite an existing file that may belong to the user.
                if (!content.Equals(url, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "STRM для этого фильма/эпизода уже существует и указывает на другой поток. " +
                        "Запусти его кнопкой STRM или удали вручную, если хочешь заменить.");
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
        _monitor.ReportFileSystemChanged(fullPath);
        var imported = Describe(fullPath);
        return new { created = true, fileName = imported.Name,
            itemId = imported.ItemId, waitingForScan = imported.ItemId is null };
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
