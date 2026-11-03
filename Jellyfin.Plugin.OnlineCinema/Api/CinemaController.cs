using Jellyfin.Plugin.OnlineCinema.Models;
using Jellyfin.Plugin.OnlineCinema.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.OnlineCinema.Api;

[ApiController]
[Authorize]
[Route("OnlineCinema")]
public sealed class CinemaController : ControllerBase
{
    private readonly CatalogService _catalog;
    private readonly NativePlaybackRegistry _native;
    public CinemaController(CatalogService catalog, NativePlaybackRegistry native)
    {
        _catalog = catalog;
        _native = native;
    }

    [HttpGet("status")]
    public object Status() => new { configured = _catalog.Configured, version = "0.1.19", catalog = "TMDB" };

    [HttpGet("discover")]
    public async Task<ActionResult<CatalogResult>> Discover(
        [FromQuery] string type = "movie", [FromQuery] string category = "popular",
        [FromQuery] int page = 1, CancellationToken cancellationToken = default)
    {
        try { return Ok(await _catalog.DiscoverAsync(type, category, page, cancellationToken)); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (HttpRequestException ex) { return StatusCode(502, new { error = ex.Message }); }
    }

    [HttpGet("search")]
    public async Task<ActionResult<CatalogResult>> Search(
        [FromQuery] string query = "", [FromQuery] string type = "multi",
        [FromQuery] int page = 1, CancellationToken cancellationToken = default)
    {
        try { return Ok(await _catalog.SearchAsync(query, type, page, cancellationToken)); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (HttpRequestException ex) { return StatusCode(502, new { error = ex.Message }); }
    }

    // GET alias to the established catalog API controller. Some embedded
    // clients cannot POST to the temporary Native/open route reliably.
    [HttpGet("open/{type}/{id:int}")]
    public async Task<IActionResult> OpenNative(
        string type, int id, [FromQuery] string? title, [FromQuery] string? original,
        [FromQuery] string? year, [FromQuery] string? poster, [FromQuery] string? backdrop,
        CancellationToken cancellationToken)
    {
        if (type is not ("movie" or "tv") || id < 1)
            return BadRequest(new { error = "Неверный идентификатор TMDB." });
        try
        {
            CatalogDetails details;
            try
            {
                details = await _catalog.DetailsAsync(type, id, cancellationToken).ConfigureAwait(false);
            }
            catch (KeyNotFoundException) when (!string.IsNullOrWhiteSpace(title))
            {
                // The title came from a successfully loaded TMDB catalog.
                // Preserve access to its Jellyfin detail page if a later
                // TMDB /details request races catalog data or returns 404.
                var item = new CatalogItem(id, type, title[..Math.Min(title.Length, 180)],
                    (original ?? title)[..Math.Min((original ?? title).Length, 180)],
                    year ?? "", "", poster ?? "", backdrop ?? "", 0, null);
                details = new CatalogDetails(item, [], [], 0);
            }
            if (!string.IsNullOrWhiteSpace(details.Item.LocalItemId))
                return Ok(new { localItemId = details.Item.LocalItemId });
            var nativeId = _native.RegisterCatalog(details);
            return Ok(new { itemId = nativeId.ToString("N"), title = details.Item.Title });
        }
        catch (ArgumentException e) { return BadRequest(new { error = e.Message }); }
        catch (InvalidOperationException e) { return BadRequest(new { error = e.Message }); }
        catch (KeyNotFoundException e) { return NotFound(new { error = e.Message }); }
        catch (HttpRequestException e) { return StatusCode(502, new { error = e.Message }); }
    }

    [HttpGet("details/{type}/{id:int}")]
    public async Task<IActionResult> Details(string type, int id, CancellationToken cancellationToken)
    {
        try { return Ok(await _catalog.DetailsAsync(type, id, cancellationToken)); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (HttpRequestException ex) { return StatusCode(502, new { error = ex.Message }); }
    }

    [HttpGet("seasons/{id:int}/{season:int}")]
    public async Task<IActionResult> Season(int id, int season, CancellationToken cancellationToken)
    {
        try { return Ok(await _catalog.SeasonAsync(id, season, cancellationToken)); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (HttpRequestException ex) { return StatusCode(502, new { error = ex.Message }); }
    }
}
