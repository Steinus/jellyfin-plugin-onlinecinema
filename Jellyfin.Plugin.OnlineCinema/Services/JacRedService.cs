using System.Text.Json;
using Jellyfin.Plugin.OnlineCinema.Models;

namespace Jellyfin.Plugin.OnlineCinema.Services;

/// <summary>Read-only release search via JacRed's documented Jackett-compatible JSON endpoint.</summary>
public sealed class JacRedService
{
    private readonly IHttpClientFactory _clients;
    public JacRedService(IHttpClientFactory clients) => _clients = clients;
    public bool Configured => !string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.JacRedUrl);

    public async Task<IReadOnlyList<TorrentResult>> SearchAsync(string title, string original, string year, bool series, CancellationToken token)
    {
        var config = Plugin.Instance?.Configuration ?? throw new InvalidOperationException("Plugin not ready");
        if (string.IsNullOrWhiteSpace(config.JacRedUrl))
            throw new InvalidOperationException("Укажи адрес JacRed в настройках плагина.");
        if (!Uri.TryCreate(config.JacRedUrl.Trim(), UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(baseUri.UserInfo)
            || !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment))
            throw new ArgumentException("JacRed URL должен быть http(s)-адресом сервера без параметров.");

        if (string.IsNullOrWhiteSpace(title) || title.Length > 160)
            throw new ArgumentException("Некорректное название фильма.");

        var parameters = new Dictionary<string, string>
        {
            ["Query"] = title.Trim(),
            ["title"] = title.Trim(),
            ["title_original"] = original?.Trim() ?? "",
            ["year"] = year?.Trim() ?? "",
            ["is_serial"] = series ? "2" : "1"
        };
        if (!string.IsNullOrWhiteSpace(config.JacRedApiKey))
            parameters["apikey"] = config.JacRedApiKey.Trim();

        var query = string.Join("&", parameters.Where(x => x.Value.Length > 0)
            .Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value)));
        var endpoint = new Uri(baseUri.ToString().TrimEnd('/') + "/api/v2.0/indexers/all/results?" + query);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        using var response = await _clients.CreateClient("OnlineCinemaJacRed").SendAsync(request, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("JacRed вернул HTTP " + (int)response.StatusCode + ". Проверьте адрес, API-ключ и доступ с NAS.");

        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        var found = document.RootElement.Prop("Results").Elements();
        return found.Take(80).Select(x => new TorrentResult(
            x.Str("MagnetUri").Length > 0 ? "magnet" : "release",
            x.Str("Title"), x.Str("Tracker"), x.Long("Size"), x.Int("Seeders")))
            .Where(x => !string.IsNullOrWhiteSpace(x.Title)).ToArray();
    }
}
