using Microsoft.AspNetCore.Http;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.OnlineCinema.Models;

namespace Jellyfin.Plugin.OnlineCinema.Services;

public sealed class TorrServerService
{
    private readonly IHttpClientFactory _clients;
    private readonly TorrentSelections _selections;
    private readonly ConcurrentDictionary<string, (string Hash, DateTimeOffset Expires)> _sessions = new();
    private readonly ConcurrentDictionary<string, (string Hash, int Index, DateTimeOffset Expires)> _tickets = new();

    public TorrServerService(IHttpClientFactory clients, TorrentSelections selections)
    {
        _clients = clients;
        _selections = selections;
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
        using var response = await _clients.CreateClient("OnlineCinemaTorrServer").SendAsync(request, token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("TorrServer HTTP " + (int)response.StatusCode + ". Проверьте адрес и авторизацию.");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        return await JsonDocument.ParseAsync(stream, cancellationToken: token);
    }

    private static PreparedTorrent Parse(JsonElement state, string session)
    {
        var files = state.Prop("file_stats").Elements()
            .Select(x => new TorrentFile(x.Int("id"), x.Str("path"), x.Long("length"))).ToArray();
        return new PreparedTorrent(session, state.Str("title"), files, files.Length > 0);
    }

    public async Task<PreparedTorrent> PrepareAsync(string selection, CancellationToken token)
    {
        var release = _selections.Get(selection);
        if (!release.MagnetUri.StartsWith("magnet:?xt=urn:btih:", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Для этой раздачи нет magnet-ссылки.");
        using var state = await CommandAsync(new { action = "add", link = release.MagnetUri, title = release.Title, save_to_db = false }, token);
        var hash = state.RootElement.Str("hash");
        if (hash.Length != 40 || !hash.All(Uri.IsHexDigit))
            throw new InvalidDataException("TorrServer не вернул корректный hash.");
        var session = Guid.NewGuid().ToString("N");
        _sessions[session] = (hash, DateTimeOffset.UtcNow.AddHours(2));
        return Parse(state.RootElement, session);
    }

    public async Task<PreparedTorrent> PollAsync(string session, CancellationToken token)
    {
        if (!_sessions.TryGetValue(session, out var record) || record.Expires < DateTimeOffset.UtcNow)
            throw new KeyNotFoundException("Сеанс истёк. Выберите раздачу повторно.");
        using var state = await CommandAsync(new { action = "get", hash = record.Hash }, token);
        return Parse(state.RootElement, session);
    }

    public async Task<PlayInfo> SelectAsync(string session, int fileIndex, CancellationToken token)
    {
        var status = await PollAsync(session, token);
        var file = status.Files.FirstOrDefault(x => x.Id == fileIndex);
        if (file is null) throw new ArgumentException("Файл не найден в раздаче.");
        var ext = Path.GetExtension(file.Path).ToLowerInvariant();
        if (ext is not (".mp4" or ".m4v" or ".mkv" or ".webm" or ".avi" or ".ts"))
            throw new ArgumentException("Выбранный файл не является поддерживаемым видео.");
        var ticket = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var record = _sessions[session];
        _tickets[ticket] = (record.Hash, fileIndex, DateTimeOffset.UtcNow.AddHours(2));
        return new PlayInfo("OnlineCinema/TorrServer/stream/" + ticket, Path.GetFileName(file.Path),
            ext is ".mp4" or ".m4v" ? "video/mp4" : ext == ".webm" ? "video/webm" : "application/octet-stream");
    }

    public async Task ProxyAsync(string ticket, HttpRequest incoming, HttpResponse outgoing, CancellationToken token)
    {
        if (!_tickets.TryGetValue(ticket, out var record) || record.Expires < DateTimeOffset.UtcNow)
        {
            outgoing.StatusCode = 404; return;
        }
        using var request = Request(HttpMethod.Get, "play/" + record.Hash + "/" + record.Index);
        if (incoming.Headers.TryGetValue("Range", out var range))
            request.Headers.TryAddWithoutValidation("Range", range.ToString());
        using var response = await _clients.CreateClient("OnlineCinemaTorrServerStream")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        outgoing.StatusCode = (int)response.StatusCode;
        foreach (var header in new[] { "Content-Range", "Accept-Ranges", "Content-Length" })
        {
            if (response.Content.Headers.TryGetValues(header, out var values))
                outgoing.Headers[header] = values.ToArray();
            else if (response.Headers.TryGetValues(header, out values))
                outgoing.Headers[header] = values.ToArray();
        }
        outgoing.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        await using var body = await response.Content.ReadAsStreamAsync(token);
        await body.CopyToAsync(outgoing.Body, token);
    }
}
