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
    public JacRedController(JacRedService jacred) => _jacred = jacred;

    [HttpGet("status")]
    public IActionResult Status() => Ok(new { configured = _jacred.Configured });

    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string title, [FromQuery] string original = "",
        [FromQuery] string year = "", [FromQuery] bool series = false, CancellationToken cancellationToken = default)
    {
        try { return Ok(await _jacred.SearchAsync(title, original, year, series, cancellationToken).ConfigureAwait(false)); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (HttpRequestException ex) { return StatusCode(502, new { error = ex.Message }); }
        catch (JsonException) { return StatusCode(502, new { error = "JacRed вернул не JSON." }); }
    }
}
