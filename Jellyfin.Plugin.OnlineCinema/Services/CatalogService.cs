using System.Net;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.OnlineCinema.Models;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OnlineCinema.Services;

public sealed class CatalogService
{
    private readonly IHttpClientFactory _clients;
    private readonly ILibraryManager _library;
    private readonly ILogger<CatalogService> _logger;

    public CatalogService(IHttpClientFactory clients, ILibraryManager library, ILogger<CatalogService> logger)
    {
        _clients = clients;
        _library = library;
        _logger = logger;
    }

    private static string ApiKey => Plugin.Instance?.Configuration.TmdbApiKey?.Trim() ?? string.Empty;
    private static string Language => Plugin.Instance?.Configuration.Language?.Trim() is { Length: > 0 } v ? v : "ru-RU";

    public bool Configured => !string.IsNullOrWhiteSpace(ApiKey);

    public async Task<CatalogResult> DiscoverAsync(string type, string category, int page, CancellationToken token)
    {
        type = NormalizeType(type);
        category = category is "top_rated" or "now_playing" or "on_the_air" or "trending" ? category : "popular";
        if (category == "trending")
        {
            using var trending = await FetchAsync($"/3/trending/{type}/week", new Dictionary<string,string>
            {
                ["page"] = Math.Clamp(page, 1, 500).ToString(System.Globalization.CultureInfo.InvariantCulture)
            }, token).ConfigureAwait(false);
            return ParseList(trending.RootElement, type);
        }
        if (category == "now_playing" && type == "tv") category = "on_the_air";
        if (category == "on_the_air" && type == "movie") category = "now_playing";
        using var response = await FetchAsync($"/3/{type}/{category}", new Dictionary<string,string>
        {
            ["page"] = Math.Clamp(page, 1, 500).ToString(System.Globalization.CultureInfo.InvariantCulture)
        }, token).ConfigureAwait(false);
        return ParseList(response.RootElement, type);
    }

    public async Task<CatalogResult> SearchAsync(string query, string type, int page, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 160) throw new ArgumentException("Введите поисковый запрос длиной до 160 символов.");
        type = type is "movie" or "tv" ? type : "multi";
        using var response = await FetchAsync($"/3/search/{type}", new Dictionary<string,string>
        {
            ["query"] = query.Trim(), ["include_adult"] = "false",
            ["page"] = Math.Clamp(page, 1, 500).ToString(System.Globalization.CultureInfo.InvariantCulture)
        }, token).ConfigureAwait(false);
        return ParseList(response.RootElement, type);
    }

    public async Task<CatalogDetails> DetailsAsync(string type, int id, CancellationToken token)
    {
        type = NormalizeType(type);
        if (id < 1) throw new ArgumentException("Некорректный ID.");
        using var response = await FetchAsync($"/3/{type}/{id}", null, token).ConfigureAwait(false);
        var root = response.RootElement;
        var item = ParseItem(root, type);
        if (item is null) throw new KeyNotFoundException("Фильм или сериал не найден.");
        var genres = root.Prop("genres").Elements().Select(g => g.Str("name")).Where(x => x.Length > 0).ToArray();
        var seasons = root.Prop("seasons").Elements().Select(s => new SeasonInfo(
            s.Int("season_number"), s.Str("name"), s.Int("episode_count"),
            Image(s.Str("poster_path")), s.Str("air_date"))).ToArray();
        var runtime = type == "movie" ? root.Int("runtime") : root.Prop("episode_run_time").Elements().Select(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out var n) ? n : 0).FirstOrDefault();
        return new CatalogDetails(item, genres, seasons, runtime);
    }

    public async Task<SeasonResult> SeasonAsync(int seriesId, int season, CancellationToken token)
    {
        if (seriesId < 1 || season < 0 || season > 200) throw new ArgumentException("Некорректный сезон.");
        using var response = await FetchAsync($"/3/tv/{seriesId}/season/{season}", null, token).ConfigureAwait(false);
        var eps = response.RootElement.Prop("episodes").Elements()
            .Select(e => new EpisodeInfo(e.Int("episode_number"), e.Str("name"), e.Str("overview"), Image(e.Str("still_path")), e.Str("air_date")))
            .ToArray();
        return new SeasonResult(season, eps);
    }

    private async Task<JsonDocument> FetchAsync(string path, Dictionary<string,string>? extra, CancellationToken token)
    {
        if (!Configured) throw new InvalidOperationException("Укажите TMDB API Key в настройках Online Cinema.");
        var query = new Dictionary<string,string>(extra ?? new()) { ["api_key"] = ApiKey, ["language"] = Language };
        var url = "https://api.themoviedb.org" + path + "?" + string.Join("&",
            query.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _clients.CreateClient("OnlineCinemaMetadata").SendAsync(request, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new KeyNotFoundException("TMDB: объект не найден.");
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("TMDB request failed with HTTP {Status}", (int)response.StatusCode);
            throw new HttpRequestException($"Ошибка TMDB: HTTP {(int)response.StatusCode}. Проверьте API ключ.");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
    }

    private CatalogResult ParseList(JsonElement root, string type)
    {
        var items = root.Prop("results").Elements()
            .Select(x => ParseItem(x, type == "multi" ? x.Str("media_type") : type))
            .OfType<CatalogItem>()
            .GroupBy(x => (x.Type, x.Id)).Select(x => x.First()).ToArray();
        return new CatalogResult(items, root.Int("page"), root.Int("total_pages"));
    }

    private CatalogItem? ParseItem(JsonElement root, string type)
    {
        if (type is not ("movie" or "tv")) return null;
        var id = root.Int("id");
        if (id <= 0) return null;
        var title = root.Str(type == "movie" ? "title" : "name");
        var originalTitle = root.Str(type == "movie" ? "original_title" : "original_name");
        var date = root.Str(type == "movie" ? "release_date" : "first_air_date");
        string? local = null;
        try
        {
            var query = new InternalItemsQuery
            {
                HasAnyProviderId = new Dictionary<string,string> { ["Tmdb"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                IncludeItemTypes = [type == "movie" ? BaseItemKind.Movie : BaseItemKind.Series],
                Recursive = true,
                Limit = 1
            };
            BaseItem? first = _library.GetItemsResult(query).Items.FirstOrDefault();
            local = first?.Id.ToString("D");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cannot check local library for TMDB {Id}", id);
        }
        return new CatalogItem(id, type, string.IsNullOrWhiteSpace(title) ? originalTitle : title,
            originalTitle, date.Length >= 4 ? date[..4] : string.Empty, root.Str("overview"),
            Image(root.Str("poster_path")), ImageBackdrop(root.Str("backdrop_path")),
            root.Double("vote_average"), local);
    }

    private static string Image(string? path) => string.IsNullOrEmpty(path) || !path.StartsWith("/", StringComparison.Ordinal) ? "" : "https://image.tmdb.org/t/p/w500" + path;
    private static string ImageBackdrop(string? path) => string.IsNullOrEmpty(path) || !path.StartsWith("/", StringComparison.Ordinal) ? "" : "https://image.tmdb.org/t/p/w1280" + path;
    private static string NormalizeType(string type) => type == "tv" ? "tv" : "movie";
}
