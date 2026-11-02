using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.OnlineCinema.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OnlineCinema.Services;

public sealed class ProwlarrService
{
    private readonly IHttpClientFactory _clients;
    private readonly ILogger<ProwlarrService> _logger;
    public ProwlarrService(IHttpClientFactory clients, ILogger<ProwlarrService> logger)
    {
        _clients = clients;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TorrentResult>> SearchAsync(string title, string original, string year, bool series, CancellationToken token)
    {
        var config = Plugin.Instance?.Configuration ?? throw new InvalidOperationException("Плагин не готов.");
        if (string.IsNullOrWhiteSpace(config.ProwlarrUrl) || string.IsNullOrWhiteSpace(config.ProwlarrApiKey))
            throw new InvalidOperationException("Укажите URL и API-ключ Prowlarr в настройках.");

        if (!Uri.TryCreate(config.ProwlarrUrl.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Неверный адрес Prowlarr.");

        if (string.IsNullOrWhiteSpace(title) || title.Length > 160)
            throw new ArgumentException("Укажите название фильма или сериала.");

        var root = uri.ToString().TrimEnd('/');
        if (root.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
            root = root[..^7];
        var query = Uri.EscapeDataString(title.Trim());
        var url = root + "/api/v1/search?query=" + query + "&type=search&limit=100";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Api-Key", config.ProwlarrApiKey.Trim());
        request.Headers.UserAgent.ParseAdd("Jell-Stein");
        _logger.LogInformation("Online Cinema searching Prowlarr for {Title}", title);
        using var response = await _clients.CreateClient("OnlineCinemaProwlarr")
            .SendAsync(request, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("Prowlarr HTTP " + (int)response.StatusCode +
                (response.StatusCode == HttpStatusCode.Unauthorized ? ": проверь API-ключ." : "."));
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        var results = json.RootElement;
        if (results.ValueKind != JsonValueKind.Array)
            results = results.Prop("results");
        if (results.ValueKind != JsonValueKind.Array)
            throw new JsonException("Prowlarr вернул неожиданный формат ответа.");

        return results.EnumerateArray().Take(100).Select(x => new TorrentResult(
            x.Str("magnetUrl").Length > 0 ? "magnet" : "release",
            x.Str("title"), x.Str("indexer"), x.Long("size"), x.Int("seeders")))
            .Where(x => !string.IsNullOrWhiteSpace(x.Title)).ToArray();
    }
}
