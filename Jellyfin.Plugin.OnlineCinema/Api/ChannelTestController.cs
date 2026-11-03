using Jellyfin.Plugin.OnlineCinema.Channels;
using Jellyfin.Plugin.OnlineCinema.Services;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OnlineCinema.Api;

/// <summary>
/// Proof of concept: one selected TorrServer stream -> probed channel item ->
/// regular Jellyfin PlaybackInfo + FFmpeg/HLS. No synthetic PlaybackInfo filter.
/// </summary>
[ApiController]
[Authorize]
[Route("OnlineCinema/ChannelTest")]
public sealed class ChannelTestController : ControllerBase
{
    private readonly TorrServerService _torr;
    private readonly NativePlaybackRegistry _native;
    private readonly ChannelPlaybackState _state;
    private readonly IChannelManager _channels;
    private readonly IMediaSourceManager _mediaSources;
    private readonly ILibraryManager _library;
    private readonly IMediaEncoder _encoder;
    private readonly ILogger<ChannelTestController> _log;

    public ChannelTestController(
        TorrServerService torr, NativePlaybackRegistry native,
        ChannelPlaybackState state, IChannelManager channels,
        IMediaSourceManager mediaSources, ILibraryManager library,
        IMediaEncoder encoder, ILogger<ChannelTestController> log)
    {
        _torr = torr;
        _native = native;
        _state = state;
        _channels = channels;
        _mediaSources = mediaSources;
        _library = library;
        _encoder = encoder;
        _log = log;
    }

    public sealed record ChannelTestSelection(Guid NativeId, string Session, int FileIndex);

    [HttpPost("select")]
    public async Task<IActionResult> Select([FromBody] ChannelTestSelection input, CancellationToken token)
    {
        try
        {
            var catalog = _native.GetCatalog(input.NativeId)
                ?? throw new KeyNotFoundException("Карточка истекла. Открой фильм повторно.");
            // Validate the actual video index before exposing a media source.
            var selected = await _torr.SelectAsync(input.Session, input.FileIndex, token)
                .ConfigureAwait(false);
            var url = _torr.GetDirectPlaybackUrl(input.Session, input.FileIndex);
            var container = Path.GetExtension(selected.FileName).TrimStart('.').ToLowerInvariant();

            // This is a server-side probe using Jellyfin's own FFprobe,
            // not browser codecs and not the broken hand-built virtual PlaybackInfo.
            var probeSource = new MediaSourceInfo
            {
                Name = selected.FileName,
                Path = url,
                Protocol = MediaProtocol.Http,
                Type = MediaSourceType.Default,
                Container = container,
                IsRemote = true,
                SupportsDirectPlay = false,
                SupportsDirectStream = false,
                SupportsTranscoding = true,
                SupportsProbing = true
            };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(35));
            var media = await _encoder.GetMediaInfo(
                new MediaInfoRequest
                {
                    MediaSource = probeSource,
                    MediaType = DlnaProfileType.Video,
                    ExtractChapters = false
                }, timeout.Token).ConfigureAwait(false);

            var tracks = media.MediaStreams?.ToArray() ?? [];
            if (!tracks.Any(s => s.Type == MediaStreamType.Video))
                throw new InvalidDataException("FFprobe не обнаружил видеодорожку в потоке TorrServer.");
            if (!tracks.Any(s => s.Type == MediaStreamType.Audio))
                throw new InvalidDataException("FFprobe не обнаружил аудиодорожки. Выбери фильм с аудио для теста.");

            var audioDefault = tracks.FirstOrDefault(s => s.Type == MediaStreamType.Audio && s.IsDefault)
                ?? tracks.First(s => s.Type == MediaStreamType.Audio);
            probeSource.MediaStreams = tracks;
            probeSource.RunTimeTicks = media.RunTimeTicks;
            probeSource.Bitrate = media.Bitrate;
            probeSource.Size = media.Size;
            probeSource.DefaultAudioStreamIndex = audioDefault.Index;
            probeSource.SupportsProbing = false;

            var active = _state.Set(catalog.Details.Item.Title, selected.FileName, probeSource);

            // Force ChannelManager to register just this in-memory channel item.
            // This is NOT the normal filesystem scan and writes no STRM file.
            var all = await _channels.GetChannelsInternalAsync(new ChannelQuery())
                .ConfigureAwait(false);
            var channel = all.Items.FirstOrDefault(c => c.Name == ChannelPlaybackTestChannel.ChannelName)
                ?? throw new InvalidOperationException("Тестовый канал не зарегистрирован. Перезапусти Jellyfin после установки плагина.");
            var query = new InternalItemsQuery
            {
                Parent = channel,
                ChannelIds = [channel.Id],
                EnableTotalRecordCount = true
            };
            var result = await _channels.GetChannelItemsInternal(
                query, new Progress<double>(), token).ConfigureAwait(false);
            var item = result.Items.FirstOrDefault(i => i.ExternalId == active.ExternalId)
                ?? throw new InvalidOperationException("Jellyfin ChannelManager не вернул созданный элемент.");

            // A channel media item must have ONE canonical static media source.
            // In 0.1.26 this source was duplicated by IRequiresMediaInfoCallback.
            // Jellyfin then produced different playback flags for the two copies.
            var resolved = await _mediaSources.GetPlaybackMediaSources(
                item, null!, false, false, token).ConfigureAwait(false);
            if (resolved.Count != 1)
                throw new InvalidOperationException(
                    $"Jellyfin предоставил {resolved.Count} источников вместо одного. " +
                    "Воспроизведение остановлено до запуска плеера.");
            var canonical = resolved[0];
            if (!canonical.MediaStreams.Any(s => s.Type == MediaStreamType.Video))
                throw new InvalidOperationException(
                    "Jellyfin потерял видеоинформацию FFprobe в Channel MediaSource.");
            if (!canonical.MediaStreams.Any(s => s.Type == MediaStreamType.Audio))
                throw new InvalidOperationException(
                    "Jellyfin потерял аудиодорожки FFprobe в Channel MediaSource.");
            if (canonical.SupportsDirectPlay || canonical.SupportsDirectStream ||
                !canonical.SupportsTranscoding)
                throw new InvalidOperationException(
                    "Channel MediaSource имеет неверные параметры: DirectPlay/DirectStream должен быть выключен, а транскодирование включено.");

            _state.MarkRegistered(active.ExternalId, item.Id);

            _log.LogInformation(
                "Online Cinema channel test ready: item {ItemId}, container {Container}, video {Video}, audio {Audio}, subtitle {Subtitle}",
                item.Id, container,
                tracks.Count(s => s.Type == MediaStreamType.Video),
                tracks.Count(s => s.Type == MediaStreamType.Audio),
                tracks.Count(s => s.Type == MediaStreamType.Subtitle));
            return Ok(new
            {
                itemId = item.Id.ToString("N"),
                fileName = selected.FileName,
                mediaSourceCount = resolved.Count,
                playbackSourceId = canonical.Id,
                audioTracks = tracks.Count(s => s.Type == MediaStreamType.Audio),
                subtitleTracks = tracks.Count(s => s.Type == MediaStreamType.Subtitle),
                transcodingAllowed = true,
                directPlayDisabled = true,
                mediaStreams = tracks.Select(s => new
                {
                    index = s.Index,
                    type = s.Type.ToString(),
                    codec = s.Codec,
                    language = s.Language,
                    title = s.Title
                })
            });
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return StatusCode(504, new { error = "FFprobe не дождался данных TorrServer за 35 секунд. Проверь доступность раздачи." });
        }
        catch (KeyNotFoundException e) { return NotFound(new { error = e.Message }); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            _log.LogWarning(e, "Online Cinema channel setup failed");
            return BadRequest(new { error = e.Message });
        }
        catch (Exception e) when (e is HttpRequestException or InvalidDataException or TimeoutException)
        {
            _log.LogWarning(e, "Online Cinema channel probe failed");
            return StatusCode(502, new { error = e.Message });
        }
    }

    // Read-only diagnostics, via Jellyfin's authenticated ApiClient.
    // No torrent hash, magnet, authentication data or direct stream URLs are returned.
    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken token)
    {
        var active = _state.Current;
        var itemId = _state.LastItemId;
        if (active is null || itemId is null) return Ok(new { ready = false });
        var item = _library.GetItemById(itemId.Value);
        if (item is null)
            return Ok(new { ready = false, reason = "Channel item отсутствует в Jellyfin" });
        try
        {
            var sources = await _mediaSources.GetPlaybackMediaSources(
                item, null!, false, false, token).ConfigureAwait(false);
            return Ok(new
            {
                ready = true,
                itemId,
                sourceCount = sources.Count,
                sources = sources.Select(source => new
                {
                    sourceId = source.Id,
                    protocol = source.Protocol.ToString(),
                    container = source.Container,
                    videoTracks = source.MediaStreams.Count(s => s.Type == MediaStreamType.Video),
                    audioTracks = source.MediaStreams.Count(s => s.Type == MediaStreamType.Audio),
                    subtitleTracks = source.MediaStreams.Count(s => s.Type == MediaStreamType.Subtitle),
                    supportsDirectPlay = source.SupportsDirectPlay,
                    supportsDirectStream = source.SupportsDirectStream,
                    supportsTranscoding = source.SupportsTranscoding
                })
            });
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or ArgumentException)
        {
            _log.LogWarning(e, "Online Cinema diagnostic source retrieval failed");
            return StatusCode(500, new { error = e.Message });
        }
    }
}
