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
    public TorrServerController(TorrServerService server, NativePlaybackRegistry native)
    {
        _server = server;
        _native = native;
    }

    [Authorize]
    [HttpGet("diagnose")]
    public async Task<IActionResult> Diagnose(CancellationToken token) => Ok(await _server.DiagnoseAsync(token));

    [Authorize]
    [HttpGet("check/{itemId:guid}")]
    public async Task<IActionResult> Check(Guid itemId, CancellationToken token)
    {
        var item = _native.Get(itemId);
        if (item is null)
            return NotFound(new { error = "Временный видеоэлемент не найден. Выберите раздачу повторно." });

        // Direct-play items still retain a private Jellyfin proxy ticket for
        // diagnostics. Never attempt to parse a full TorrServer URL as ticket.
        var parts = item.ProxyPath.Trim('/').Split('/');
        if (parts.Length < 4 || parts[0] != "OnlineCinema" ||
            parts[1] != "TorrServer" || parts[2] != "stream" ||
            parts[3].Length != 64 || !parts[3].All(Uri.IsHexDigit))
            return BadRequest(new { error = "У выбранного элемента нет корректного потока TorrServer." });

        return Ok(await _server.CheckPlaybackAsync(parts[3], token).ConfigureAwait(false));
    }

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
    [HttpPost("files")]
    public Task<IActionResult> FilesPost([FromBody] TorrentSessionRequest request, CancellationToken token)
        => FilesImpl(request.Session, token);

    [Authorize]
    [HttpGet("files/{session}")]
    public Task<IActionResult> Files(string session, CancellationToken token)
        => FilesImpl(session, token);

    private async Task<IActionResult> FilesImpl(string session, CancellationToken token)
    {
        try { return Ok(await _server.PollAsync(session, token).ConfigureAwait(false)); }
        catch (KeyNotFoundException e) { return Conflict(new { error = e.Message }); }
        catch (Exception e) when (e is HttpRequestException or InvalidDataException or TimeoutException)
        { return StatusCode(502, new { error = e.Message }); }
    }

    // Old Jellyfin Web clients may still have the virtual playback JavaScript
    // cached. Refuse to create an unplayable virtual item: the client must use
    // /OnlineCinema/Strm/create and then the actual library item ID.
    [Authorize]
    [HttpPost("select")]
    public IActionResult Select([FromBody] PlaySelection input)
        => Conflict(new
        {
            error = "Старый способ воспроизведения отключён. Обнови интерфейс " +
                    "Online Cinema до версии 0.1.22 и выбери раздачу заново: " +
                    "сначала создаётся STRM, затем запускается Jellyfin."
        });

    // Browser video tags cannot set custom Jellyfin auth headers.
    // The stream URL uses an unguessable, expiring bearer ticket instead.
    [AllowAnonymous]
    [HttpGet("stream/{ticket}")]
    [HttpHead("stream/{ticket}")]
    [HttpGet("stream/{ticket}/video.{extension}")]
    [HttpHead("stream/{ticket}/video.{extension}")]
    public Task Stream(string ticket, string? extension, CancellationToken token)
        => _server.ProxyAsync(ticket, Request, Response, token);
}
