using System.Collections.Concurrent;
using Jellyfin.Plugin.OnlineCinema.Models;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Entities;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.OnlineCinema.Services;

/// <summary>
/// Temporary virtual items that let Jellyfin Web invoke its own video player
/// through the standard Items and PlaybackInfo APIs. No media is added to the
/// user's physical Jellyfin library.
/// </summary>
public sealed class NativePlaybackRegistry
{
    private readonly ConcurrentDictionary<Guid, NativeItem> _items = new();
    private readonly ConcurrentDictionary<Guid, NativeCatalogEntry> _catalog = new();
    private readonly ConcurrentDictionary<Guid, MediaInfo> _probeResults = new();
    private readonly IServerApplicationHost _host;

    public NativePlaybackRegistry(IServerApplicationHost host) => _host = host;

    public Guid Register(string title, string streamPath, string fileName)
    {
        var id = Guid.NewGuid();
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        _items[id] = new NativeItem(id, title, streamPath, ext, DateTimeOffset.UtcNow.AddHours(2));
        Prune();
        return id;
    }

    public void EnrichMediaInfo(Guid id, MediaInfo info)
    {
        if (_items.ContainsKey(id))
            _probeResults[id] = info;
    }

    public NativeItem? Get(Guid id)
    {
        if (!_items.TryGetValue(id, out var entry)) return null;
        if (entry.Expires > DateTimeOffset.UtcNow) return entry;
        _items.TryRemove(id, out _);
        return null;
    }

    public Guid RegisterCatalog(CatalogDetails details)
    {
        var id = Guid.NewGuid();
        _catalog[id] = new NativeCatalogEntry(id, details, DateTimeOffset.UtcNow.AddHours(3));
        Prune();
        return id;
    }

    public NativeCatalogEntry? GetCatalog(Guid id)
    {
        if (!_catalog.TryGetValue(id, out var entry)) return null;
        if (entry.Expires > DateTimeOffset.UtcNow) return entry;
        _catalog.TryRemove(id, out _);
        return null;
    }

    public BaseItemDto BuildCatalogItem(NativeCatalogEntry entry)
    {
        var data = entry.Details;
        var metadata = data.Item;
        var isMovie = metadata.Type == "movie";
        int? year = int.TryParse(metadata.Year, out var parsed) ? parsed : null;
        var dto = new BaseItemDto
        {
            Id = entry.Id,
            ServerId = _host.SystemId,
            Name = metadata.Title,
            OriginalTitle = metadata.OriginalTitle,
            Overview = metadata.Overview,
            Type = isMovie ? BaseItemKind.Movie : BaseItemKind.Series,
            MediaType = isMovie ? MediaType.Video : default,
            IsFolder = !isMovie,
            ProductionYear = year,
            CommunityRating = (float)metadata.Rating,
            Genres = data.Genres.ToArray(),
            RunTimeTicks = data.Runtime > 0 ? data.Runtime * TimeSpan.TicksPerMinute : null,
            PrimaryImageAspectRatio = 2d / 3d,
            ProviderIds = new Dictionary<string, string>
            {
                ["Tmdb"] = metadata.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
            },
            UserData = new UserItemDataDto
            {
                Key = "onlinecinema:tmdb:" + metadata.Type + ":" + metadata.Id,
                PlaybackPositionTicks = 0,
                PlayCount = 0,
                Played = false,
                IsFavorite = false
            }
        };
        if (!string.IsNullOrEmpty(metadata.PosterUrl))
            dto.ImageTags = new Dictionary<ImageType, string> { [ImageType.Primary] = "onlinecinema" };
        if (!string.IsNullOrEmpty(metadata.BackdropUrl))
            dto.BackdropImageTags = ["onlinecinema"];
        return dto;
    }

    public string? GetCatalogImage(Guid id, ImageType imageType)
    {
        var record = GetCatalog(id);
        if (record is null) return null;
        return imageType switch
        {
            ImageType.Primary => record.Details.Item.PosterUrl,
            ImageType.Backdrop => record.Details.Item.BackdropUrl,
            _ => null
        };
    }

    private void Prune()
    {
        if (_items.Count < 500 && _catalog.Count < 500) return;
        foreach (var entry in _items.Where(pair => pair.Value.Expires <= DateTimeOffset.UtcNow))
        {
            _items.TryRemove(entry.Key, out _);
            _probeResults.TryRemove(entry.Key, out _);
        }
        foreach (var entry in _catalog.Where(pair => pair.Value.Expires <= DateTimeOffset.UtcNow))
            _catalog.TryRemove(entry.Key, out _);
    }

    private MediaSourceInfo Source(NativeItem entry, HttpRequest request)
    {
        _probeResults.TryGetValue(entry.Id, out var probed);
        var path = request.PathBase.ToString().TrimEnd('/') + "/" + entry.StreamPath.TrimStart('/');
        var absolute = request.Scheme + "://" + request.Host + path;
        return new MediaSourceInfo
        {
            Id = entry.Id.ToString("N"),
            Name = entry.Title,
            Path = absolute,
            Protocol = MediaProtocol.Http,
            Type = MediaSourceType.Default,
            Container = entry.Container,
            MediaStreams = probed?.MediaStreams ?? [],
            DefaultAudioStreamIndex = probed?.DefaultAudioStreamIndex,
            DefaultSubtitleStreamIndex = probed?.DefaultSubtitleStreamIndex,
            RunTimeTicks = probed?.RunTimeTicks,
            Bitrate = probed?.Bitrate,
            Size = probed?.Size,
            // The URL is hosted by Jellyfin itself. Marking it remote
            // disables native direct playback in clients whose appHost does
            // not allow RemoteVideo (including some Android integrations).
            IsRemote = false,
            SupportsDirectPlay = true,
            SupportsDirectStream = false,
            SupportsTranscoding = false,
            // Keep disabled until Jellyfin's virtual-item HLS/FFmpeg routes
            // can resolve dynamic IDs. FFprobe metadata alone is insufficient.
            SupportsProbing = false,
            RequiresOpening = false,
            RequiresClosing = false
        };
    }

    public BaseItemDto BuildItem(NativeItem entry, HttpRequest request)
        => new()
        {
            Id = entry.Id,
            ServerId = _host.SystemId,
            Name = entry.Title,
            Type = BaseItemKind.Movie,
            MediaType = MediaType.Video,
            IsFolder = false,
            Path = Source(entry, request).Path,
            MediaSources = [Source(entry, request)],
            UserData = new UserItemDataDto
            {
                Key = "onlinecinema:" + entry.Id.ToString("N"),
                PlaybackPositionTicks = 0,
                PlayCount = 0,
                Played = false,
                IsFavorite = false
            }
        };

    public PlaybackInfoResponse BuildPlayback(NativeItem entry, HttpRequest request)
        => new()
        {
            MediaSources = [Source(entry, request)],
            PlaySessionId = Guid.NewGuid().ToString("N")
        };

    public string StreamRedirect(NativeItem entry, HttpRequest request)
        => request.PathBase.ToString().TrimEnd('/') + "/" + entry.StreamPath.TrimStart('/');
}

public sealed record NativeItem(Guid Id, string Title, string StreamPath, string Container, DateTimeOffset Expires);

public sealed record NativeCatalogEntry(Guid Id, CatalogDetails Details, DateTimeOffset Expires);
