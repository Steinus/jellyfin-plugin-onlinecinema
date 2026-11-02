using Jellyfin.Plugin.OnlineCinema.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.OnlineCinema;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer) { Instance = this; }

    public static Plugin? Instance { get; private set; }
    public override string Name => "Online Cinema";
    public override Guid Id => Guid.Parse("e1c3807b-47d2-4dd8-a40d-e5e57990a621");
    public override string Description => "TMDB movie and TV catalog with online discovery features for Jellyfin.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = "OnlineCinema",
            DisplayName = "Настройки онлайн-кинотеатра",
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
        };
        yield return new PluginPageInfo
        {
            Name = "OnlineCinemaPortal",
            DisplayName = "Онлайн-кинотеатр",
            EnableInMainMenu = true,
            EmbeddedResourcePath = $"{GetType().Namespace}.Web.portal.html"
        };
    }
}
