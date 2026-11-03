using System.Globalization;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.OnlineCinema.Channels;

/// <summary>
/// One short-lived remote video backed by a real Jellyfin channel item.
/// No STRM file and no filesystem library scanner are involved.
/// </summary>
public sealed class ChannelPlaybackState
{
    private readonly object _sync = new();
    private ChannelPlaybackEntry? _active;
    private long _version;

    public string Version => Interlocked.Read(ref _version).ToString(CultureInfo.InvariantCulture);

    public ChannelPlaybackEntry Set(string title, string fileName, MediaSourceInfo source)
    {
        lock (_sync)
        {
            var entry = new ChannelPlaybackEntry(Guid.NewGuid().ToString("N"),
                title, fileName, source, DateTime.UtcNow);
            _active = entry;
            Interlocked.Increment(ref _version);
            return entry;
        }
    }

    public ChannelPlaybackEntry? Current
    {
        get { lock (_sync) return _active; }
    }

    public ChannelPlaybackEntry? Find(string externalId)
    {
        lock (_sync)
            return string.Equals(_active?.ExternalId, externalId, StringComparison.Ordinal)
                ? _active : null;
    }
}

public sealed record ChannelPlaybackEntry(
    string ExternalId, string Title, string FileName, MediaSourceInfo ProbedSource, DateTime CreatedUtc)
{
    // Never hand a mutable shared source to a Jellyfin playback request.
    public MediaSourceInfo CreateMediaSource() => new()
    {
        Name = FileName,
        Path = ProbedSource.Path,
        Protocol = MediaProtocol.Http,
        Type = MediaSourceType.Default,
        Container = ProbedSource.Container,
        RunTimeTicks = ProbedSource.RunTimeTicks,
        Bitrate = ProbedSource.Bitrate,
        Size = ProbedSource.Size,
        MediaStreams = ProbedSource.MediaStreams.ToArray(),
        DefaultAudioStreamIndex = ProbedSource.DefaultAudioStreamIndex,
        DefaultSubtitleStreamIndex = null,
        IsRemote = true,
        SupportsDirectPlay = false,
        SupportsDirectStream = false,
        SupportsTranscoding = true,
        SupportsProbing = false,
        RequiresOpening = false,
        RequiresClosing = false
    };
}

public sealed class ChannelPlaybackTestChannel : IChannel, IHasCacheKey, IRequiresMediaInfoCallback
{
    public const string ChannelName = "Online Cinema — тест воспроизведения";
    private readonly ChannelPlaybackState _state;
    public ChannelPlaybackTestChannel(ChannelPlaybackState state) => _state = state;

    public string Name => ChannelName;
    public string Description => "Диагностика HTTP-транскодирования без STRM и сканирования библиотеки.";
    public string DataVersion => _state.Version;
    public string HomePageUrl => string.Empty;
    public ChannelParentalRating ParentalRating => ChannelParentalRating.UsPG;
    public string? GetCacheKey(string? userId) => _state.Version;
    public bool IsEnabledFor(string userId) => true;

    public InternalChannelFeatures GetChannelFeatures() => new()
    {
        MediaTypes = [ChannelMediaType.Video],
        ContentTypes = [ChannelMediaContentType.Movie]
    };

    public Task<ChannelItemResult> GetChannelItems(
        InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        var entry = _state.Current;
        var items = entry is null
            ? Array.Empty<ChannelItemInfo>()
            : new[]
            {
                new ChannelItemInfo
                {
                    Id = entry.ExternalId,
                    Name = entry.Title,
                    DateModified = entry.CreatedUtc,
                    Type = ChannelItemType.Media,
                    MediaType = ChannelMediaType.Video,
                    ContentType = ChannelMediaContentType.Movie,
                    IsLiveStream = false,
                    MediaSources = [entry.CreateMediaSource()],
                    RunTimeTicks = entry.ProbedSource.RunTimeTicks
                }
            };
        return Task.FromResult(new ChannelItemResult { Items = items, TotalRecordCount = items.Length });
    }

    public Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(
        string id, CancellationToken cancellationToken)
    {
        var entry = _state.Find(id);
        IEnumerable<MediaSourceInfo> sources = entry is null
            ? [] : [entry.CreateMediaSource()];
        return Task.FromResult(sources);
    }

    public Task<DynamicImageResponse> GetChannelImage(
        ImageType type, CancellationToken cancellationToken)
        => Task.FromResult(new DynamicImageResponse { HasImage = false });

    public IEnumerable<ImageType> GetSupportedChannelImages() => [];
}
