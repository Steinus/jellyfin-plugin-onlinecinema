using Jellyfin.Plugin.OnlineCinema.Services;
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
        services.AddSingleton<IScheduledTask, ClientIntegrationTask>();
    }
}
