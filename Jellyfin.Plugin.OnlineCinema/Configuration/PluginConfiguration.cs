using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.OnlineCinema.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public string TmdbApiKey { get; set; } = string.Empty;
    public string Language { get; set; } = "ru-RU";
    public string ProwlarrUrl { get; set; } = string.Empty;
    public string ProwlarrApiKey { get; set; } = string.Empty;
    public string TorrServerUrl { get; set; } = string.Empty;
    public string TorrServerUsername { get; set; } = string.Empty;
    public string TorrServerPassword { get; set; } = string.Empty;
    public bool PreferLocalLibrary { get; set; } = true;
    public bool ShowGlobalSearchResults { get; set; } = true;
}
