using Jellyfin.Plugin.OnlineCinema.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.OnlineCinema;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
    {
        services.AddHttpClient("OnlineCinemaMetadata", c => c.Timeout = TimeSpan.FromSeconds(18));
        services.AddSingleton<CatalogService>();
    }
}
