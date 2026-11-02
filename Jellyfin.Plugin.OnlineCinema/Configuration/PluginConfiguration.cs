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
    public bool PreferLocalLibrary { get; set; } = true;
    public bool ShowGlobalSearchResults { get; set; } = true;
}
