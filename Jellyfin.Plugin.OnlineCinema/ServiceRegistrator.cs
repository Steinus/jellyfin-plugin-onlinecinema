using Jellyfin.Plugin.OnlineCinema.Services;
using Jellyfin.Plugin.OnlineCinema.Filters;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.OnlineCinema;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
    {
        services.AddHttpClient("OnlineCinemaMetadata", c => c.Timeout = TimeSpan.FromSeconds(18));
        services.AddSingleton<CatalogService>();
        services.AddHttpClient("OnlineCinemaArtwork", client => client.Timeout = TimeSpan.FromSeconds(18));
        services.AddSingleton<TmdbImageService>();
        services.AddHttpClient("OnlineCinemaJacRed", client => client.Timeout = TimeSpan.FromSeconds(25));
        services.AddSingleton<TorrentSelections>();
        services.AddSingleton<JacRedService>();
        services.AddHttpClient("OnlineCinemaProwlarr", c => c.Timeout = TimeSpan.FromSeconds(25));
        services.AddSingleton<ProwlarrService>();
        services.AddHttpClient("OnlineCinemaTorrServer", c => c.Timeout = TimeSpan.FromSeconds(35));
        services.AddHttpClient("OnlineCinemaTorrServerStream", c => c.Timeout = TimeSpan.FromHours(4));
        services.AddSingleton<TorrServerService>();
        services.AddSingleton<NativePlaybackRegistry>();
        services.AddSingleton<StrmLibraryService>();
        services.AddSingleton<NativePlaybackFilter>();
        services.PostConfigure<Microsoft.AspNetCore.Mvc.MvcOptions>(options => options.Filters.AddService<NativePlaybackFilter>(order: -10));
        services.AddSingleton<IScheduledTask, ClientIntegrationTask>();
    }
}
