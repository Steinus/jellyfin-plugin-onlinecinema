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
    // Jellyfin persists channel-list caches between restarts. A numeric-only
    // DataVersion would restart at "1" and collide with older cached lists.
    // Give each plugin lifetime its own namespace, without clearing any caches.
    private readonly string _startupCacheKey = Guid.NewGuid().ToString("N");
    private ChannelPlaybackEntry? _active;
    private Guid? _lastItemId;
    // A new playback must NOT delete a channel item which is still in use
    // by FFmpeg or a client. Keep a bounded recent list rather than one slot.
    private readonly List<ChannelPlaybackEntry> _recent = [];
    private const int MaxRecent = 24;
    private static readonly TimeSpan Retention = TimeSpan.FromHours(4);
    private long _version;

    public string Version => _startupCacheKey + "-" +
        Interlocked.Read(ref _version).ToString(CultureInfo.InvariantCulture);

    public ChannelPlaybackEntry Set(string title, string fileName, MediaSourceInfo source)
    {
        lock (_sync)
        {
            var entry = new ChannelPlaybackEntry(Guid.NewGuid().ToString("N"),
                title, fileName, source, DateTime.UtcNow);
            _active = entry;
            _lastItemId = null;
            _recent.Insert(0, entry);
            var cutoff = DateTime.UtcNow - Retention;
            _recent.RemoveAll(e => e.CreatedUtc < cutoff);
            if (_recent.Count > MaxRecent)
                _recent.RemoveRange(MaxRecent, _recent.Count - MaxRecent);
            Interlocked.Increment(ref _version);
            return entry;
        }
    }

    public Guid? LastItemId { get { lock (_sync) return _lastItemId; } }

    public void MarkRegistered(string externalId, Guid itemId)
    {
        lock (_sync)
        {
            if (string.Equals(_active?.ExternalId, externalId, StringComparison.Ordinal))
                _lastItemId = itemId;
        }
    }

    public ChannelPlaybackEntry? Current
    {
        get { lock (_sync) return _active; }
    }

    public IReadOnlyList<ChannelPlaybackEntry> Recent
    {
        get { lock (_sync) return _recent.ToArray(); }
    }

    public ChannelPlaybackEntry? Find(string externalId)
    {
        lock (_sync)
            return _recent.FirstOrDefault(e =>
                string.Equals(e.ExternalId, externalId, StringComparison.Ordinal));
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

public sealed class ChannelPlaybackTestChannel : IChannel, IHasCacheKey
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
        var items = _state.Recent.Select(entry => new ChannelItemInfo
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
        }).ToArray();
        return Task.FromResult(new ChannelItemResult { Items = items, TotalRecordCount = items.Length });
    }

    public Task<DynamicImageResponse> GetChannelImage(
        ImageType type, CancellationToken cancellationToken)
        => Task.FromResult(new DynamicImageResponse { HasImage = false });

    public IEnumerable<ImageType> GetSupportedChannelImages() => [];
}
