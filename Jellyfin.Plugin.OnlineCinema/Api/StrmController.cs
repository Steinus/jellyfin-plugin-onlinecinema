using Jellyfin.Plugin.OnlineCinema.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OnlineCinema.Api;

[ApiController]
[Authorize]
[Route("OnlineCinema/Strm")]
public sealed class StrmController : ControllerBase
{
    private readonly StrmLibraryService _library;
    private readonly ILogger<StrmController> _logger;

    public StrmController(StrmLibraryService library, ILogger<StrmController> logger)
    {
        _library = library;
        _logger = logger;
    }

    [HttpGet("diagnose")]
    public IActionResult Diagnose() => Ok(_library.Diagnose());

    [HttpGet("status/{nativeId:guid}")]
    public IActionResult Status(Guid nativeId)
    {
        try { return Ok(_library.Status(nativeId)); }
        catch (KeyNotFoundException e) { return NotFound(new { error = e.Message }); }
        catch (IOException e) { return StatusCode(500, new { error = e.Message }); }
    }

    [HttpPost("create")]
    public async Task<IActionResult> Create([FromBody] StrmCreateRequest request, CancellationToken token)
    {
        try
        {
            return Ok(await _library.CreateAsync(request.NativeId, request.Session,
                request.FileIndex, token).ConfigureAwait(false));
        }
        catch (KeyNotFoundException e)
        {
            _logger.LogWarning(e, "Online Cinema STRM: missing session or catalog item");
            return NotFound(new { code = "session-not-found", error = e.Message });
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            _logger.LogWarning(e, "Online Cinema STRM: refused to create file");
            return BadRequest(new { code = "configuration-or-video", error = e.Message });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Online Cinema STRM: file write failed");
            return StatusCode(500, new { code = "file-write-failed",
                error = "Jellyfin не удалось записать STRM: " + e.Message });
        }
        catch (Exception e) when (e is HttpRequestException or InvalidDataException or TimeoutException)
        {
            _logger.LogWarning(e, "Online Cinema STRM: TorrServer request failed");
            return StatusCode(502, new { code = "torrserver-failed", error = "TorrServer: " + e.Message });
        }
    }

    public sealed record StrmCreateRequest(Guid NativeId, string Session, int FileIndex);
}
