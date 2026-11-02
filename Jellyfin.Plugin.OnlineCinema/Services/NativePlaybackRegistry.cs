using System.Collections.Concurrent;
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

    public NativeItem? Get(Guid id)
    {
        if (!_items.TryGetValue(id, out var entry)) return null;
        if (entry.Expires > DateTimeOffset.UtcNow) return entry;
        _items.TryRemove(id, out _);
        return null;
    }

    private void Prune()
    {
        if (_items.Count < 500) return;
        foreach (var entry in _items.Where(pair => pair.Value.Expires <= DateTimeOffset.UtcNow))
            _items.TryRemove(entry.Key, out _);
    }

    private MediaSourceInfo Source(NativeItem entry, HttpRequest request)
    {
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
            IsRemote = true,
            SupportsDirectPlay = true,
            SupportsDirectStream = false,
            SupportsTranscoding = false,
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
