namespace Jellyfin.Plugin.OnlineCinema.Models;

public sealed record CatalogItem(
    int Id, string Type, string Title, string OriginalTitle, string Year,
    string Overview, string PosterUrl, string BackdropUrl, double Rating,
    string? LocalItemId);

public sealed record CatalogResult(IReadOnlyList<CatalogItem> Items, int Page, int TotalPages);

public sealed record SeasonInfo(int Number, string Name, int Episodes, string PosterUrl, string AirDate);

public sealed record CatalogDetails(CatalogItem Item, IReadOnlyList<string> Genres, IReadOnlyList<SeasonInfo> Seasons, int Runtime);

public sealed record EpisodeInfo(int Number, string Name, string Overview, string StillUrl, string AirDate);

public sealed record SeasonResult(int Season, IReadOnlyList<EpisodeInfo> Episodes);

// Key is an opaque, short-lived server-side selection ID; no magnet URLs
// or tracker download URLs are included in the client-facing response.
public sealed record TorrentResult(string Key, string Title, string Indexer, long Size, int Seeders, bool Playable);

public sealed record TorrentRelease(
    string Title, string Indexer, long Size, int Seeders,
    string MagnetUri, string DownloadUrl, string Source);


public sealed record PreparedTorrent(string Session, string Title, IReadOnlyList<TorrentFile> Files, bool Ready);

public sealed record TorrentFile(int Id, string Path, long Length);

public sealed record PlaySelection(string Session, int FileIndex);
public sealed record PlayInfo(string Url, string FileName, string ContentType);
public sealed record PrepareSelection(string Key);
