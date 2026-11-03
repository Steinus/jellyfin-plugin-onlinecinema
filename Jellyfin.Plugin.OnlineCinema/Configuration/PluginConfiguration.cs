using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.OnlineCinema.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public string TmdbApiKey { get; set; } = string.Empty;
    public string Language { get; set; } = "ru-RU";
    // "jackett" searches JacRed's Jackett-compatible API; "prowlarr" uses Prowlarr's REST API.
    // Retain "jackett" as default for users upgrading from 0.1.6.
    public string TorrentSearchProvider { get; set; } = "jackett";
    public string JacRedUrl { get; set; } = string.Empty;
    public string JacRedApiKey { get; set; } = string.Empty;
    public string ProwlarrUrl { get; set; } = string.Empty;
    public string ProwlarrApiKey { get; set; } = string.Empty;
    public string TorrServerUrl { get; set; } = string.Empty;
    public string TorrServerUsername { get; set; } = string.Empty;
    public string TorrServerPassword { get; set; } = string.Empty;
    // proxy: Jellyfin forwards byte-range requests to TorrServer (legacy).
    // direct: the native Jellyfin player opens http(s)://TorrServer/play/hash/file
    // itself, like a direct remote STRM link. Requires client LAN connectivity.
    // With TorrServer basic auth, direct mode falls back to proxy so credentials
    // are never disclosed in the playback URL.
    public string StreamDeliveryMode { get; set; } = "proxy";
    // strm registers a real Jellyfin library entry so the stock playback
    // pipeline can select compatible codecs, subtitles and transcode.
    public string PlaybackBackend { get; set; } = "strm";
    public string StrmMoviesDirectory { get; set; } = string.Empty;
    public string StrmSeriesDirectory { get; set; } = string.Empty;
    public bool KeepStrmAfterPlayback { get; set; } = true;
    // Behaviour of the custom Play action on an online catalog item.
    // auto = search and play automatically; previous = replay selected media, otherwise auto;
    // manual = open the torrent list and let the user choose.
    public string AutoPlayMode { get; set; } = "auto";
    public string AutoPlayQuality { get; set; } = "1080p";
    public string AutoPlaySort { get; set; } = "quality";
    public int AutoPlayMaxAttempts { get; set; } = 6;
    public int AutoPlayMinSeeders { get; set; } = 0;
    public bool Exclude4K { get; set; } = false;
    public bool Exclude720p { get; set; } = false;
    public bool ExcludeHdr { get; set; } = false;
    public bool ExcludeDolbyVision { get; set; } = false;
    public bool ProbeMediaStreams { get; set; } = true;
    public bool PreferLocalLibrary { get; set; } = true;
    public bool ShowGlobalSearchResults { get; set; } = true;
}
