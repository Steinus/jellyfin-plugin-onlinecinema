using System.Text.Json;
using Jellyfin.Plugin.OnlineCinema.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.OnlineCinema.Api;

[ApiController]
[Authorize]
[Route("OnlineCinema/JacRed")]
public sealed class JacRedController : ControllerBase
{
    private readonly JacRedService _jacred;
    private readonly ProwlarrService _prowlarr;
    public JacRedController(JacRedService jacred, ProwlarrService prowlarr)
    {
        _jacred = jacred;
        _prowlarr = prowlarr;
    }

    [HttpGet("status")]
    public IActionResult Status() => Ok(new { provider = Plugin.Instance?.Configuration.TorrentSearchProvider ?? "jackett", jackett = _jacred.GetStatus() });

    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string title, [FromQuery] string original = "",
        [FromQuery] string year = "", [FromQuery] bool series = false, CancellationToken cancellationToken = default)
    {
        try { return Ok(await ((Plugin.Instance?.Configuration.TorrentSearchProvider == "prowlarr") ? _prowlarr.SearchAsync(title, original, year, series, cancellationToken) : _jacred.SearchAsync(title, original, year, series, cancellationToken)).ConfigureAwait(false)); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (HttpRequestException ex) { return StatusCode(502, new { error = ex.Message }); }
        catch (TaskCanceledException) { return StatusCode(504, new { error = "Тайм-аут JacRed. Проверьте сетевой доступ с NAS." }); }
        catch (JsonException) { return StatusCode(502, new { error = "JacRed вернул не JSON." }); }
    }
}
