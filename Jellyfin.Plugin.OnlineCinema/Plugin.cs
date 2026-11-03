using Jellyfin.Plugin.OnlineCinema.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.OnlineCinema;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Online Cinema";

    public override Guid Id => Guid.Parse("e1c3807b-47d2-4dd8-a40d-e5e57990a621");

    public override string Description => "Browse TMDB movies and series, with separate administration settings.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        // EnableInMainMenu is the ADMINISTRATION sidebar, not the user home screen.
        // Register exactly one administrative page here. The viewer page is injected
        // into Jellyfin Web separately via the File Transformation plugin.
        yield return new PluginPageInfo
        {
            Name = "OnlineCinema",
            DisplayName = "Онлайн-кинотеатр (настройки)",
            EnableInMainMenu = true,
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
        };
    }
}
