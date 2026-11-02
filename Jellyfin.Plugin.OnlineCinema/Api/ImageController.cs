using Jellyfin.Plugin.OnlineCinema.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.OnlineCinema.Api;

[ApiController]
[Route("OnlineCinema/images")]
public sealed class ImageController : ControllerBase
{
    private readonly TmdbImageService _images;

    public ImageController(TmdbImageService images) => _images = images;

    // Public, non-sensitive TMDB artwork; HTML image tags cannot send Jellyfin auth headers.
    // The path is strictly constrained, so this is NOT an arbitrary HTTP proxy.
    [AllowAnonymous]
    [HttpGet("{size}/{fileName}")]
    public async Task<IActionResult> Image(
        [FromRoute] string size, [FromRoute] string fileName,
        CancellationToken cancellationToken)
    {
        if (!TmdbImageService.IsValid(size, fileName))
            return BadRequest("Invalid image path.");

        try
        {
            var image = await _images.GetAsync(size, fileName, cancellationToken).ConfigureAwait(false);
            Response.Headers.CacheControl = "public, max-age=86400";
            return File(image.Bytes, image.ContentType);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or TaskCanceledException)
        {
            // Never bypass certificate validation. If the NAS also cannot access TMDB,
            // return an identifiable 502 so the network / DNS / trust chain can be fixed.
            Response.Headers.CacheControl = "no-store";
            return StatusCode(502, "TMDB image could not be loaded by the Jellyfin server. Check server connectivity and HTTPS certificates.");
        }
    }
}
