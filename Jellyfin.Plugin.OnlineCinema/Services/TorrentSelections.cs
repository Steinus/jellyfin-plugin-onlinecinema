using System.Collections.Concurrent;
using Jellyfin.Plugin.OnlineCinema.Models;

namespace Jellyfin.Plugin.OnlineCinema.Services;

public sealed class TorrentSelections
{
    private readonly ConcurrentDictionary<string, (TorrentRelease Release, DateTimeOffset Expires)> _entries = new();
    public TorrentResult Register(TorrentRelease release)
    {
        var key = Guid.NewGuid().ToString("N");
        _entries[key] = (release, DateTimeOffset.UtcNow.AddMinutes(30));
        if (_entries.Count > 5000)
        {
            foreach (var pair in _entries.Where(x => x.Value.Expires < DateTimeOffset.UtcNow).Take(1000))
                _entries.TryRemove(pair.Key, out _);
        }
        return new TorrentResult(key, release.Title, release.Indexer, release.Size, release.Seeders, release.MagnetUri.StartsWith("magnet:?xt=urn:btih:", StringComparison.OrdinalIgnoreCase));
    }
    public TorrentRelease Get(string key)
    {
        if (!_entries.TryGetValue(key, out var entry) || entry.Expires < DateTimeOffset.UtcNow)
            throw new KeyNotFoundException("Результат поиска устарел. Выполните поиск повторно.");
        return entry.Release;
    }
}
