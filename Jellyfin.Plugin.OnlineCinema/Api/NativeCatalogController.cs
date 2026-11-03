using System.Collections.Concurrent;
using Jellyfin.Plugin.OnlineCinema.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.OnlineCinema.Api;

/// <summary>
/// Creates temporary native Jellyfin detail pages backed by TMDB metadata.
/// No media is imported into a user's ordinary library.
/// </summary>
[ApiController]
[Authorize]
[Route("OnlineCinema/Native")]
public sealed class NativeCatalogController : ControllerBase
{
    private readonly CatalogService _catalog;
    private readonly NativePlaybackRegistry _registry;
    private static readonly ConcurrentDictionary<Guid, (Guid VideoId, DateTimeOffset Expires)> LastPlayed = new();

    public NativeCatalogController(CatalogService catalog, NativePlaybackRegistry registry)
    {
        _catalog = catalog;
        _registry = registry;
    }

    [HttpGet("options")]
    public IActionResult Options()
    {
        Response.Headers.CacheControl = "no-store, max-age=0";
        var c = Plugin.Instance?.Configuration;
        return Ok(new
        {
            autoPlayMode = c?.AutoPlayMode ?? "auto",
            autoPlayQuality = c?.AutoPlayQuality ?? "1080p",
            autoPlaySort = c?.AutoPlaySort ?? "quality",
            autoPlayMaxAttempts = Math.Clamp(c?.AutoPlayMaxAttempts ?? 3, 1, 5),
            autoPlayMinSeeders = Math.Max(0, c?.AutoPlayMinSeeders ?? 0),
            exclude4K = c?.Exclude4K ?? false,
            exclude720p = c?.Exclude720p ?? false,
            excludeHdr = c?.ExcludeHdr ?? false,
            excludeDolbyVision = c?.ExcludeDolbyVision ?? false,
            probeMediaStreams = c?.ProbeMediaStreams ?? true,
            playbackBackend = "strm",
            keepStrmAfterPlayback = c?.KeepStrmAfterPlayback ?? true
        });
    }

    [HttpPost("open/{type}/{tmdbId:int}")]
    public async Task<IActionResult> Open(string type, int tmdbId, CancellationToken token)
    {
        if (type is not ("movie" or "tv") || tmdbId <= 0)
            return BadRequest(new { error = "Некорректный тип или TMDB ID." });

        try
        {
            var details = await _catalog.DetailsAsync(type, tmdbId, token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(details.Item.LocalItemId))
                return Ok(new { localItemId = details.Item.LocalItemId });
            var nativeId = _registry.RegisterCatalog(details);
            return Ok(new { itemId = nativeId.ToString("N"), title = details.Item.Title });
        }
        catch (KeyNotFoundException e) { return NotFound(new { error = e.Message }); }
        catch (HttpRequestException e) { return StatusCode(502, new { error = e.Message }); }
        catch (InvalidOperationException e) { return BadRequest(new { error = e.Message }); }
    }

    [HttpGet("item/{nativeId:guid}")]
    public IActionResult Item(Guid nativeId)
    {
        var record = _registry.GetCatalog(nativeId);
        if (record is null)
            return NotFound(new { error = "Онлайн-карточка устарела. Откройте её повторно из каталога." });

        Guid? selectedId = null;
        if (LastPlayed.TryGetValue(nativeId, out var playback))
        {
            if (playback.Expires > DateTimeOffset.UtcNow && _registry.Get(playback.VideoId) is not null)
                selectedId = playback.VideoId;
            else
                LastPlayed.TryRemove(nativeId, out _);
        }
        return Ok(new
        {
            nativeId,
            type = record.Details.Item.Type,
            tmdbId = record.Details.Item.Id,
            title = record.Details.Item.Title,
            originalTitle = record.Details.Item.OriginalTitle,
            year = record.Details.Item.Year,
            selectedVideoId = selectedId?.ToString("N")
        });
    }

    [HttpPost("item/{nativeId:guid}/selected")]
    public IActionResult Selected(Guid nativeId, [FromBody] SelectedVideoRequest input)
    {
        if (_registry.GetCatalog(nativeId) is null)
            return NotFound(new { error = "Онлайн-карточка устарела." });
        if (_registry.Get(input.VideoId) is null)
            return BadRequest(new { error = "Выбранное видео не найдено." });
        LastPlayed[nativeId] = (input.VideoId, DateTimeOffset.UtcNow.AddHours(2));
        return Ok(new { selectedVideoId = input.VideoId.ToString("N") });
    }

    public sealed record SelectedVideoRequest(Guid VideoId);
}
