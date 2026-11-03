using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OnlineCinema.Services;

/// <summary>
/// Reads TMDB public images server-side and caches them. The browser never
/// connects to image.tmdb.org, solving client-side CDN certificate problems.
/// TLS certificate verification remains enabled on the Jellyfin server.
/// </summary>
public sealed class TmdbImageService
{
    private const int MaxImageBytes = 5 * 1024 * 1024;
    private static readonly MemoryCache Cache = new(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });
    private static readonly Regex FilePattern = new(
        @"^[A-Za-z0-9_-]{10,80}\.(jpg|jpeg|png|webp)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(200));

    private readonly IHttpClientFactory _clients;
    private readonly ILogger<TmdbImageService> _logger;

    public TmdbImageService(IHttpClientFactory clients, ILogger<TmdbImageService> logger)
    {
        _clients = clients;
        _logger = logger;
    }

    public static bool IsValid(string size, string fileName)
        => size is "w300" or "w342" or "w500" or "w1280"
            && !string.IsNullOrWhiteSpace(fileName)
            && FilePattern.IsMatch(fileName);

    public async Task<TmdbImage> GetAsync(string size, string fileName, CancellationToken cancellationToken)
    {
        if (!IsValid(size, fileName))
            throw new ArgumentException("Invalid TMDB image path.");

        var cacheKey = size + "/" + fileName;
        if (Cache.TryGetValue<TmdbImage>(cacheKey, out var existing) && existing is not null)
            return existing;

        // Fixed external host and validated file path; no user-controlled URL proxy.
        var url = "https://image.tmdb.org/t/p/" + cacheKey;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _clients.CreateClient("OnlineCinemaArtwork")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new KeyNotFoundException("TMDB image not found.");

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Image CDN returned HTTP {(int)response.StatusCode}.");

        var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? string.Empty;
        if (mediaType is not ("image/jpeg" or "image/png" or "image/webp"))
            throw new InvalidDataException("TMDB returned an unexpected image content type.");

        if (response.Content.Headers.ContentLength is > MaxImageBytes)
            throw new InvalidDataException("TMDB image exceeds size limit.");

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[32 * 1024];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + count > MaxImageBytes)
                throw new InvalidDataException("TMDB image exceeds size limit.");
            await memory.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }

        var image = new TmdbImage(memory.ToArray(), mediaType);
        if (image.Bytes.Length == 0)
            throw new InvalidDataException("TMDB returned an empty image.");

        using var entry = Cache.CreateEntry(cacheKey);
        entry.SetSize(image.Bytes.Length);
        entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24);
        entry.SlidingExpiration = TimeSpan.FromHours(6);
        entry.Value = image;
        _logger.LogDebug("Cached TMDB image {Size}/{File}", size, fileName);
        return image;
    }
}

public sealed record TmdbImage(byte[] Bytes, string ContentType);
