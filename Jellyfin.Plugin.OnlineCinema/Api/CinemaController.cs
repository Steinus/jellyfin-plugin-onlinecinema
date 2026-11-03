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
    public CinemaController(CatalogService catalog) => _catalog = catalog;

    [HttpGet("status")]
    public object Status() => new { configured = _catalog.Configured, version = "0.1.13", catalog = "TMDB" };

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
