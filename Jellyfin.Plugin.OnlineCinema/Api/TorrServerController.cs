using Jellyfin.Plugin.OnlineCinema.Models;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;
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
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<TorrServerController> _logger;
    public TorrServerController(
        TorrServerService server,
        NativePlaybackRegistry native,
        IMediaEncoder mediaEncoder,
        ILogger<TorrServerController> logger)
    {
        _server = server;
        _native = native;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
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

    [Authorize]
    [HttpPost("select")]
    public async Task<IActionResult> Select([FromBody] PlaySelection input, CancellationToken token)
    {
        try
        {
            var selected = await _server.SelectAsync(input.Session, input.FileIndex, token);
            var title = Path.GetFileNameWithoutExtension(selected.FileName);
            var configuration = Plugin.Instance!.Configuration;
            var wantsDirect = string.Equals(configuration.StreamDeliveryMode,
                "direct", StringComparison.OrdinalIgnoreCase);
            // Never expose a configured TorrServer basic-auth credential in
            // a native playback URL. Fall back to the authenticated proxy.
            var direct = wantsDirect && string.IsNullOrWhiteSpace(configuration.TorrServerUsername);
            var playbackPath = direct
                ? _server.GetDirectPlaybackUrl(input.Session, input.FileIndex)
                : selected.Url;
            var id = _native.Register(title, playbackPath, selected.FileName, selected.Url);
            var probed = false;
            var streams = 0;
            if (Plugin.Instance?.Configuration.ProbeMediaStreams == true)
            {
                try
                {
                    var url = direct
                        ? playbackPath
                        : Request.Scheme + "://" + Request.Host +
                            Request.PathBase.ToString().TrimEnd('/') + "/" + selected.Url.TrimStart('/');
                    var source = new MediaSourceInfo
                    {
                        Id = id.ToString("N"),
                        Path = url,
                        Container = Path.GetExtension(selected.FileName).TrimStart('.').ToLowerInvariant(),
                        Protocol = MediaProtocol.Http,
                        IsRemote = true,
                        SupportsProbing = true
                    };
                    using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    probeTimeout.CancelAfter(TimeSpan.FromSeconds(18));
                    var info = await _mediaEncoder.GetMediaInfo(new MediaInfoRequest
                    {
                        MediaSource = source,
                        MediaType = DlnaProfileType.Video,
                        ExtractChapters = false
                    }, probeTimeout.Token).ConfigureAwait(false);
                    if (info?.MediaStreams is { Count: > 0 })
                    {
                        _native.EnrichMediaInfo(id, info);
                        probed = true;
                        streams = info.MediaStreams.Count;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Online Cinema: FFprobe could not inspect selected video {File}", selected.FileName);
                }
            }
            return Ok(new { itemId = id.ToString("N"), fileName = selected.FileName,
                mode = direct ? "direct-torrserver" : "jellyfin-proxy",
                modeNotice = wantsDirect && !direct
                    ? "TorrServer защищён паролем — используем прокси Jellyfin."
                    : direct
                        ? "Плеер получает поток непосредственно от TorrServer."
                        : "Плеер получает поток через Jellyfin.",
                probed, streamCount = streams, transcoding = false });
        }
        catch (Exception e) when (e is ArgumentException or KeyNotFoundException or InvalidOperationException)
        { return BadRequest(new { error = e.Message }); }
        catch (Exception e) when (e is HttpRequestException or InvalidDataException or TimeoutException)
        { return StatusCode(502, new { error = e.Message }); }
    }

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
