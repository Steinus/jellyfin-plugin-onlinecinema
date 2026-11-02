using Jellyfin.Plugin.OnlineCinema.Models;
using Jellyfin.Plugin.OnlineCinema.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.OnlineCinema.Api;

[ApiController]
[Route("OnlineCinema/TorrServer")]
public sealed class TorrServerController : ControllerBase
{
    private readonly TorrServerService _server;
    private readonly NativePlaybackRegistry _native;
    public TorrServerController(TorrServerService server, NativePlaybackRegistry native) { _server = server; _native = native; }

    [Authorize]
    [HttpGet("diagnose")]
    public async Task<IActionResult> Diagnose(CancellationToken token) => Ok(await _server.DiagnoseAsync(token));

    [Authorize]
    [HttpPost("prepare")]
    public async Task<IActionResult> Prepare([FromBody] PrepareSelection input, CancellationToken token)
    {
        try { return Ok(await _server.PrepareAsync(input.Key, token)); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or KeyNotFoundException)
        { return BadRequest(new { error = e.Message }); }
        catch (Exception e) when (e is HttpRequestException or InvalidDataException or TimeoutException)
        { return StatusCode(502, new { error = e.Message }); }
    }

    [Authorize]
    [HttpGet("files/{session}")]
    public async Task<IActionResult> Files(string session, CancellationToken token)
    {
        try { return Ok(await _server.PollAsync(session, token)); }
        catch (KeyNotFoundException e) { return NotFound(new { error = e.Message }); }
        catch (Exception e) when (e is HttpRequestException or InvalidDataException or TimeoutException) { return StatusCode(502, new { error = e.Message }); }
    }

    [Authorize]
    [HttpPost("select")]
    public async Task<IActionResult> Select([FromBody] PlaySelection input, CancellationToken token)
    {
        try
        {
            var selected = await _server.SelectAsync(input.Session, input.FileIndex, token);
            var title = Path.GetFileNameWithoutExtension(selected.FileName);
            var id = _native.Register(title, selected.Url, selected.FileName);
            return Ok(new { itemId = id.ToString("N"), fileName = selected.FileName, mode = "jellyfin-native" });
        }
        catch (Exception e) when (e is ArgumentException or KeyNotFoundException)
        { return BadRequest(new { error = e.Message }); }
        catch (HttpRequestException e) { return StatusCode(502, new { error = e.Message }); }
    }

    // Browser video tags cannot set custom Jellyfin auth headers.
    // The stream URL uses an unguessable, expiring bearer ticket instead.
    [AllowAnonymous]
    [HttpGet("stream/{ticket}")]
    public Task Stream(string ticket, CancellationToken token)
        => _server.ProxyAsync(ticket, Request, Response, token);
}
