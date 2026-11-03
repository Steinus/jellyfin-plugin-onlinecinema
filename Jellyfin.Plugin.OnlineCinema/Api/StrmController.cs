using Jellyfin.Plugin.OnlineCinema.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.OnlineCinema.Api;

[ApiController]
[Authorize]
[Route("OnlineCinema/Strm")]
public sealed class StrmController : ControllerBase
{
    private readonly StrmLibraryService _library;

    public StrmController(StrmLibraryService library) => _library = library;

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
        catch (KeyNotFoundException e) { return NotFound(new { error = e.Message }); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        { return BadRequest(new { error = e.Message }); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return StatusCode(500, new { error = "Не удалось создать STRM: " + e.Message }); }
        catch (Exception e) when (e is HttpRequestException or InvalidDataException or TimeoutException)
        { return StatusCode(502, new { error = "TorrServer: " + e.Message }); }
    }

    public sealed record StrmCreateRequest(Guid NativeId, string Session, int FileIndex);
}
