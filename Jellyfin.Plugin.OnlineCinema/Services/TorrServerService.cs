using Microsoft.AspNetCore.Http;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.OnlineCinema.Models;

namespace Jellyfin.Plugin.OnlineCinema.Services;

public sealed class TorrServerService
{
    private readonly IHttpClientFactory _clients;
    private readonly TorrentSelections _selections;
    private readonly ILogger<TorrServerService> _logger;
    private sealed class TorrentSession
    {
        public required string Hash { get; set; }
        public required string MagnetUri { get; init; }
        public required string Title { get; init; }
        public DateTimeOffset Expires { get; init; } = DateTimeOffset.UtcNow.AddHours(2);
        public TorrentFile[] LastKnownFiles { get; set; } = [];
        public DateTimeOffset LastRecovery { get; set; } = DateTimeOffset.MinValue;
        public SemaphoreSlim RecoveryGate { get; } = new(1, 1);
    }
    private readonly ConcurrentDictionary<string, TorrentSession> _sessions = new();
    private readonly ConcurrentDictionary<string, (string Hash, int Index, string ContentType, DateTimeOffset Expires)> _tickets = new();

    public TorrServerService(
        IHttpClientFactory clients, TorrentSelections selections, ILogger<TorrServerService> logger)
    {
        _clients = clients;
        _selections = selections;
        _logger = logger;
    }

    private static Uri ServerUrl()
    {
        var text = Plugin.Instance?.Configuration.TorrServerUrl?.Trim() ?? "";
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsolutePath is not ("" or "/") || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Укажите базовый http(s) адрес TorrServer с портом в настройках.");
        return uri;
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(ServerUrl(), path));
        var config = Plugin.Instance!.Configuration;
        if (!string.IsNullOrWhiteSpace(config.TorrServerUsername))
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(config.TorrServerUsername + ":" + config.TorrServerPassword)));
        request.Headers.UserAgent.ParseAdd("Jell-Stein");
        return request;
    }

    private async Task<JsonDocument> CommandAsync(object body, CancellationToken token)
    {
        using var request = Request(HttpMethod.Post, "torrents");
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try
        {
            response = await _clients.CreateClient("OnlineCinemaTorrServer")
                .SendAsync(request, token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            var url = ServerUrl();
            throw new HttpRequestException(
                $"Нет соединения с TorrServer ({url.Host}:{url.Port}). Проверьте IP-адрес, порт, HTTP/HTTPS и доступ с NAS. {ex.GetBaseException().Message}",
                ex);
        }
        catch (TaskCanceledException ex) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("TorrServer не ответил за установленное время. Проверьте IP, порт и работу сервера.", ex);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var hint = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => " Проверьте имя пользователя и пароль TorrServer.",
                    HttpStatusCode.Forbidden => " Проверьте ограничения доступа на TorrServer или прокси.",
                    HttpStatusCode.NotFound => " По указанному адресу не найден API /torrents — возможно, указан порт NAS, а не TorrServer.",
                    HttpStatusCode.BadRequest => " TorrServer отклонил данные раздачи; попробуйте другую magnet-ссылку.",
                    _ => " Проверьте TorrServer и его журнал."
                };
                throw new HttpRequestException(
                    $"TorrServer вернул HTTP {(int)response.StatusCode} на POST /torrents." + hint,
                    null, response.StatusCode);
            }
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                return await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                var contentType = response.Content.Headers.ContentType?.MediaType ?? "не указан";
                throw new InvalidDataException(
                    $"POST /torrents вернул не JSON (Content-Type: {contentType}). Проверьте, что URL ведёт именно к TorrServer, а не к веб-странице NAS/nginx.",
                    ex);
            }
        }
    }

    public async Task<object> DiagnoseAsync(CancellationToken token)
    {
        Uri? target = null;
        try
        {
            target = ServerUrl();
            using var answer = await CommandAsync(new { action = "list" }, token).ConfigureAwait(false);
            if (answer.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("TorrServer вернул JSON, но не список раздач. Проверьте API и порт.");
            return new
            {
                connected = true,
                host = target.Host,
                port = target.Port,
                count = answer.RootElement.GetArrayLength(),
                message = "Соединение установлено, API /torrents отвечает."
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or InvalidDataException or TimeoutException or JsonException)
        {
            return new
            {
                connected = false,
                host = target?.Host ?? "",
                port = target?.Port ?? 0,
                error = ex.Message,
                message = "Убедитесь, что указан адрес TorrServer с правильным портом."
            };
        }
    }

    private static TorrentFile[] ParseFiles(JsonElement state)
        => state.Prop("file_stats").Elements()
            .Select(x => new TorrentFile(x.Int("id"), x.Str("path"), x.Long("length")))
            .Where(x => x.Id > 0 && !string.IsNullOrWhiteSpace(x.Path)).ToArray();

    private static PreparedTorrent BuildState(string sessionId, TorrentSession entry, JsonElement state)
    {
        var fresh = ParseFiles(state);
        if (fresh.Length > 0)
            entry.LastKnownFiles = fresh;

        // TorrServer answers add immediately, while it may take tens of
        // seconds to discover peers and download the torrent's file index.
        return new PreparedTorrent(sessionId, entry.Title, entry.LastKnownFiles,
            entry.LastKnownFiles.Length > 0,
            state.Str("stat_string"), state.Int("connected_seeders"), state.Int("total_peers"));
    }

    public async Task<PreparedTorrent> PrepareAsync(string selection, CancellationToken token)
    {
        var release = _selections.Get(selection);
        if (!release.MagnetUri.StartsWith("magnet:?xt=urn:btih:", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Для этой раздачи нет magnet-ссылки.");

        using var response = await CommandAsync(new
        {
            action = "add", link = release.MagnetUri, title = release.Title, save_to_db = false
        }, token).ConfigureAwait(false);

        var hash = response.RootElement.Str("hash");
        if (hash.Length != 40 || !hash.All(Uri.IsHexDigit))
            throw new InvalidDataException("TorrServer не вернул корректный hash.");

        var sessionId = Guid.NewGuid().ToString("N");
        var session = new TorrentSession
        {
            Hash = hash, Title = release.Title, MagnetUri = release.MagnetUri
        };
        _sessions[sessionId] = session;
        return BuildState(sessionId, session, response.RootElement);
    }

    private TorrentSession GetSession(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var entry)
            && entry.Expires > DateTimeOffset.UtcNow)
            return entry;

        _sessions.TryRemove(sessionId, out _);
        throw new KeyNotFoundException(
            "Сеанс раздачи отсутствует или истёк. Выбери раздачу повторно.");
    }

    private async Task<JsonDocument> RefreshFromTorrServerAsync(
        TorrentSession entry, CancellationToken token)
    {
        try
        {
            return await CommandAsync(new { action = "get", hash = entry.Hash }, token)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // TorrServer disconnects idle torrents automatically. Its /get
            // API then returns 404 even though add worked moments earlier.
            // Re-add the same magnet instead of propagating an opaque 404.
            await entry.RecoveryGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                _logger.LogInformation("Online Cinema: TorrServer forgot torrent {Hash}; restoring magnet session",
                    entry.Hash);
                using var resurrected = await CommandAsync(new
                {
                    action = "add",
                    link = entry.MagnetUri,
                    title = entry.Title,
                    save_to_db = false
                }, token).ConfigureAwait(false);

                var returnedHash = resurrected.RootElement.Str("hash");
                if (returnedHash.Length != 40 || !returnedHash.All(Uri.IsHexDigit))
                    throw new InvalidDataException("TorrServer вернул неверный hash после восстановления торрента.");

                entry.Hash = returnedHash;
                entry.LastRecovery = DateTimeOffset.UtcNow;
                // 'add' itself returns a status document. Do not immediately
                // issue another 'get': the torrent may still be initializing.
                return JsonDocument.Parse(resurrected.RootElement.GetRawText());
            }
            finally
            {
                entry.RecoveryGate.Release();
            }
        }
    }

    public async Task<PreparedTorrent> PollAsync(string sessionId, CancellationToken token)
    {
        var session = GetSession(sessionId);
        using var response = await RefreshFromTorrServerAsync(session, token)
            .ConfigureAwait(false);
        return BuildState(sessionId, session, response.RootElement);
    }

    public async Task<PlayInfo> SelectAsync(string sessionId, int fileIndex, CancellationToken token)
    {
        var status = await PollAsync(sessionId, token).ConfigureAwait(false);
        if (!status.Ready)
            throw new InvalidOperationException(
                "TorrServer ещё не получил метаданные раздачи. Дождись списка файлов и повтори выбор.");

        var file = status.Files.FirstOrDefault(x => x.Id == fileIndex);
        if (file is null)
            throw new ArgumentException("Выбранный файл больше не найден в раздаче.");
        var ext = Path.GetExtension(file.Path).ToLowerInvariant();
        if (ext is not (".mp4" or ".m4v" or ".mkv" or ".webm" or ".avi" or ".ts"))
            throw new ArgumentException("Выбранный файл не является поддерживаемым видео.");
        var ticket = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var record = GetSession(sessionId);
        var contentType = ext switch
        {
            ".mp4" or ".m4v" => "video/mp4",
            ".mkv" => "video/x-matroska",
            ".avi" => "video/x-msvideo",
            ".webm" => "video/webm",
            ".ts" => "video/mp2t",
            _ => "application/octet-stream"
        };
        _tickets[ticket] = (record.Hash, fileIndex, contentType, DateTimeOffset.UtcNow.AddHours(2));

        // Android's player relies on the file extension and Content-Type when
        // resolving a streamed media source. Keep the real extension in URL.
        return new PlayInfo("OnlineCinema/TorrServer/stream/" + ticket + "/video" + ext,
            Path.GetFileName(file.Path), contentType);
    }

    /// <summary>
    /// Checks whether TorrServer can actually deliver the first bytes of the
    /// selected file. This is deliberately distinct from /torrents action=list
    /// and does not disclose magnet, hash, or playback-ticket values.
    /// </summary>
    public async Task<object> CheckPlaybackAsync(string ticket, CancellationToken token)
    {
        if (!_tickets.TryGetValue(ticket, out var record) ||
            record.Expires <= DateTimeOffset.UtcNow)
            return new
            {
                ok = false,
                status = 404,
                receivedBytes = 0,
                message = "Временная ссылка видео устарела. Выберите раздачу заново."
            };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(18));
        try
        {
            using var request = Request(HttpMethod.Get, "play/" + record.Hash + "/" + record.Index);
            request.Headers.Range = new RangeHeaderValue(0, 1023);
            using var response = await _clients.CreateClient("OnlineCinemaTorrServerStream")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            var status = (int)response.StatusCode;
            var receivedBytes = 0;
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token)
                    .ConfigureAwait(false);
                var probe = new byte[1024];
                receivedBytes = await stream.ReadAsync(probe, timeout.Token).ConfigureAwait(false);
            }
            var ok = response.IsSuccessStatusCode && receivedBytes > 0;
            var message = !response.IsSuccessStatusCode
                ? "TorrServer не отдал видеофайл. Проверьте активность торрента и журнал TorrServer."
                : receivedBytes == 0
                    ? "TorrServer ответил, но первые байты видео ещё недоступны."
                    : "TorrServer отдаёт данные. Если штатный плеер показывает Source error, проверьте выбранный видеокодек, прямое воспроизведение и транскодирование.";
            _logger.LogInformation(
                "Online Cinema playback check: TorrServer status={Status}, bytes={Bytes}, fileIndex={Index}",
                status, receivedBytes, record.Index);
            return new
            {
                ok,
                status,
                receivedBytes,
                contentType = response.Content.Headers.ContentType?.MediaType ?? record.ContentType,
                rangeSupported = response.StatusCode == HttpStatusCode.PartialContent,
                message
            };
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new
            {
                ok = false, status = 504, receivedBytes = 0,
                message = "TorrServer не отдал начальные байты видео за 18 секунд."
            };
        }
        catch (HttpRequestException error)
        {
            _logger.LogWarning(error, "Online Cinema: stream check failed for fileIndex={FileIndex}", record.Index);
            return new
            {
                ok = false, status = 502, receivedBytes = 0,
                message = "Ошибка соединения с TorrServer во время запроса видео: " + error.GetBaseException().Message
            };
        }
        catch (IOException error)
        {
            _logger.LogWarning(error, "Online Cinema: stream read failed for fileIndex={FileIndex}", record.Index);
            return new
            {
                ok = false, status = 502, receivedBytes = 0,
                message = "Ошибка чтения данных видео: " + error.Message
            };
        }
    }

    /// <summary>
    /// Store the selected torrent persistently so saved STRM links survive
    /// TorrServer idle cleanup or restart.
    /// </summary>
    public async Task PersistAsync(string sessionId, CancellationToken token)
    {
        var session = GetSession(sessionId);
        using var stored = await CommandAsync(new
        {
            action = "add",
            link = session.MagnetUri,
            title = session.Title,
            save_to_db = true
        }, token).ConfigureAwait(false);
        var hash = stored.RootElement.Str("hash");
        if (hash.Length != 40 || !hash.All(Uri.IsHexDigit))
            throw new InvalidDataException("TorrServer не сохранил раздачу в базе.");
        session.Hash = hash;
    }

    /// <summary>
    /// Direct network URL intended for Jellyfin clients with access to the
    /// configured TorrServer host. This does not embed credentials in a URL.
    /// Only call for a file that SelectAsync has already validated.
    /// </summary>
    public string GetDirectPlaybackUrl(string sessionId, int fileIndex)
    {
        var session = GetSession(sessionId);
        var file = session.LastKnownFiles.FirstOrDefault(x => x.Id == fileIndex);
        if (file is null)
            throw new ArgumentException("TorrServer не вернул имя выбранного видеофайла.");

        // Match the stream URL format of the user's working
        // torrserver-strm-for-jellyfin plugin. The real filename (and its
        // extension) is important for client media/container detection.
        var name = Path.GetFileName(file.Path.Replace('\\', '/'));
        var resource = "stream/" + Uri.EscapeDataString(name) +
            "?link=" + Uri.EscapeDataString(session.Hash) +
            "&index=" + fileIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            "&play";
        return new Uri(ServerUrl(), resource).AbsoluteUri;
    }

    // Used only to migrate files created by the older Online Cinema /play
    // implementation to the compatible /stream link. Never use for new files.
    public string GetLegacyPlaybackUrl(string sessionId, int fileIndex)
    {
        var session = GetSession(sessionId);
        return new Uri(ServerUrl(), "play/" + session.Hash + "/" +
            fileIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)).AbsoluteUri;
    }

    public async Task ProxyAsync(string ticket, HttpRequest incoming, HttpResponse outgoing, CancellationToken token)
    {
        if (!_tickets.TryGetValue(ticket, out var record) || record.Expires < DateTimeOffset.UtcNow)
        {
            outgoing.StatusCode = 404; return;
        }
        // TorrServer exposes GET /play/{hash}/{id}, including RFC 7233 ranges.
        // It does not expose a separate HEAD handler, so implement HEAD via
        // a 0-0 byte-range GET without sending any response body downstream.
        var isHead = HttpMethods.IsHead(incoming.Method);
        using var request = Request(HttpMethod.Get, "play/" + record.Hash + "/" + record.Index);
        if (incoming.Headers.TryGetValue("Range", out var range))
            request.Headers.TryAddWithoutValidation("Range", range.ToString());
        else if (isHead)
            request.Headers.TryAddWithoutValidation("Range", "bytes=0-0");

        using var response = await _clients.CreateClient("OnlineCinemaTorrServerStream")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            _logger.LogWarning("Online Cinema: TorrServer /play returned HTTP {Status} for file #{FileIndex}",
                (int)response.StatusCode, record.Index);

        outgoing.StatusCode = (int)response.StatusCode;
        foreach (var header in new[] { "Content-Range", "Accept-Ranges", "Content-Length" })
        {
            if (response.Content.Headers.TryGetValues(header, out var values))
                outgoing.Headers[header] = values.ToArray();
            else if (response.Headers.TryGetValues(header, out values))
                outgoing.Headers[header] = values.ToArray();
        }
        outgoing.ContentType = record.ContentType;
        outgoing.Headers.CacheControl = "private, no-store";
        if (isHead) return;

        await using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await body.CopyToAsync(outgoing.Body, token).ConfigureAwait(false);
    }
}
