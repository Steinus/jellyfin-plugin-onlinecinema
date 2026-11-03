using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.OnlineCinema.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OnlineCinema.Services;

/// <summary>
/// The UI calls Jellyfin; only the Jellyfin server contacts JacRed.
/// Requests are sent to the documented Jackett JSON API, not the Torznab XML API.
/// </summary>
public sealed class JacRedService
{
    private readonly IHttpClientFactory _clients;
    private readonly ILogger<JacRedService> _logger;
    private readonly TorrentSelections _selections;
    private int _attempts;
    private int _lastHttpStatus;
    private int _lastResultCount;
    private string _lastError = string.Empty;
    private DateTimeOffset? _lastAttempt;

    public JacRedService(IHttpClientFactory clients, ILogger<JacRedService> logger, TorrentSelections selections)
    {
        _clients = clients;
        _logger = logger;
        _selections = selections;
    }

    public bool Configured => !string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.JacRedUrl);

    public object GetStatus()
    {
        var configuredUrl = Plugin.Instance?.Configuration.JacRedUrl ?? string.Empty;
        string? host = null;
        if (Uri.TryCreate(configuredUrl, UriKind.Absolute, out var u))
            host = u.Host;
        return new
        {
            configured = Configured,
            host,
            attempts = _attempts,
            lastHttpStatus = _lastHttpStatus == 0 ? (int?)null : _lastHttpStatus,
            lastResultCount = _lastResultCount,
            lastAttempt = _lastAttempt,
            lastError = _lastError,
            userAgent = "Jell-Stein",
            note = "Outbound requests originate from Jellyfin and use User-Agent Jell-Stein; direct browser navigation to this status endpoint returns 401 without the Jellyfin authorization header."
        };
    }

    public static Uri ResolveEndpoint(string configuredUrl)
    {
        if (!Uri.TryCreate(configuredUrl.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Укажите адрес JacRed как http(s)://host[:port] без параметров и логина.");

        var path = uri.AbsolutePath.TrimEnd('/');
        // Users often copy the working Torznab URL from TorrServer or Prowlarr.
        // JacRed JSON search lives at the root (or at the same reverse-proxy prefix).
        var suffixes = new[]
        {
            "/api/v2.0/indexers/all/results",
            "/api/v1/search",
            "/torznab/api",
            "/torznab"
        };
        foreach (var suffix in suffixes)
        {
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^suffix.Length];
                break;
            }
        }

        var root = uri.GetLeftPart(UriPartial.Authority) + path.TrimEnd('/');
        return new Uri(root + "/api/v2.0/indexers/all/results", UriKind.Absolute);
    }

    public async Task<IReadOnlyList<TorrentResult>> SearchAsync(
        string title, string original, string year, bool series, CancellationToken token)
    {
        var config = Plugin.Instance?.Configuration ?? throw new InvalidOperationException("Плагин ещё не готов.");
        if (!Configured)
            throw new InvalidOperationException("JacRed не настроен. Сначала укажите JacRed URL в настройках Online Cinema.");

        if (string.IsNullOrWhiteSpace(title) || title.Length > 160)
            throw new ArgumentException("Некорректное название фильма или сериала.");

        var endpoint = ResolveEndpoint(config.JacRedUrl);
        // Use the same Jackett query shape as the working Lampa request.
        // Some JacRed/nginx installations apply client filtering to requests
        // without a User-Agent (HttpClient doesn't send one by default).
        var parameters = new Dictionary<string, string>
        {
            ["apikey"] = string.Empty,
            ["Query"] = title.Trim(),
            ["title"] = title.Trim(),
            ["title_original"] = original?.Trim() ?? string.Empty,
            ["year"] = year?.Trim() ?? string.Empty,
            ["is_serial"] = series ? "2" : "1",
            ["Category[]"] = series ? "5000" : "2000"
        };
        var query = string.Join("&", parameters.Where(p => p.Key == "apikey" || !string.IsNullOrWhiteSpace(p.Value))
            .Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        var builder = new UriBuilder(endpoint) { Query = query };
        using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
        request.Headers.UserAgent.ParseAdd("Jell-Stein");
        if (!string.IsNullOrWhiteSpace(config.JacRedApiKey))
        {
            // Avoid writing nonempty API keys into nginx access logs.
            parameters["apikey"] = string.Empty;
            request.Headers.Add("X-Api-Key", config.JacRedApiKey.Trim());
        }

        Interlocked.Increment(ref _attempts);
        _lastAttempt = DateTimeOffset.UtcNow;
        _lastError = string.Empty;
        var watch = Stopwatch.StartNew();
        _logger.LogInformation("Online Cinema: JacRed search to {Host} for {Title} ({Year})",
            endpoint.Authority, title, year);

        try
        {
            using var response = await _clients.CreateClient("OnlineCinemaJacRed")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            _lastHttpStatus = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                var reason = response.StatusCode switch
                {
                    HttpStatusCode.Forbidden => "HTTP 403: доступ запрещён. Проверьте фильтр IP в JacRed/nginx и адрес NAS.",
                    HttpStatusCode.Unauthorized => "HTTP 401: неверный API-ключ или нужна авторизация.",
                    HttpStatusCode.NotFound => "HTTP 404: путь API не найден. Укажите базовый адрес JacRed без /torznab.",
                    _ => "HTTP " + (int)response.StatusCode + " от JacRed."
                };
                throw new HttpRequestException(reason, null, response.StatusCode);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
            var results = json.RootElement.Prop("Results");
            if (results.ValueKind != JsonValueKind.Array)
                results = json.RootElement.Prop("results");
            if (results.ValueKind != JsonValueKind.Array)
                throw new JsonException("JacRed JSON не содержит массива Results.");

            var torrents = results.EnumerateArray().Take(100).Select(x => _selections.Register(new TorrentRelease(
                x.Str("Title"), x.Str("Tracker"), x.Long("Size"), x.Int("Seeders"),
                x.Str("MagnetUri"), x.Str("Link"), "jackett")))
                .Where(x => !string.IsNullOrWhiteSpace(x.Title)).ToArray();
            _lastResultCount = torrents.Length;
            _logger.LogInformation("Online Cinema: JacRed returned {Count} releases in {Ms} ms",
                torrents.Length, watch.ElapsedMilliseconds);
            return torrents;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            _lastError = ex is TaskCanceledException ? "Тайм-аут соединения JacRed." : ex.Message;
            _logger.LogWarning(ex, "Online Cinema: JacRed search failed on {Host} after {Ms} ms",
                endpoint.Authority, watch.ElapsedMilliseconds);
            throw;
        }
    }
}
