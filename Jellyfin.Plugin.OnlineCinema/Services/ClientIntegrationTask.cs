using System.Reflection;
using System.Runtime.Loader;
using Jellyfin.Plugin.OnlineCinema.Web;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.OnlineCinema.Services;

public sealed class ClientIntegrationTask : IScheduledTask
{
    private readonly ILogger<ClientIntegrationTask> _logger;
    public ClientIntegrationTask(ILogger<ClientIntegrationTask> logger) => _logger = logger;
    public string Name => "Online Cinema web integration";
    public string Key => "OnlineCinemaWebIntegration";
    public string Description => "Register Online Cinema in Jellyfin web via File Transformation.";
    public string Category => "Online Cinema";
    public bool IsHidden => true;
    public bool IsEnabled => true;
    public bool IsLogged => false;
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
    }

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        Assembly? assembly = AssemblyLoadContext.All.SelectMany(c => c.Assemblies)
            .FirstOrDefault(a => a.GetName().Name == "Jellyfin.Plugin.FileTransformation");
        MethodInfo? register = assembly?.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface")
            ?.GetMethod("RegisterTransformation");
        if (register is null)
        {
            _logger.LogWarning("File Transformation plugin not available. User-facing web navigation will not be injected.");
            return Task.CompletedTask;
        }
        try
        {
            var patch = new JObject
            {
                ["id"] = "fa21972e-11c6-4d16-a165-40e174f5da08",
                ["fileNamePattern"] = "(^|/)index\\.html$",
                ["callbackAssembly"] = typeof(ClientWebPatch).Assembly.FullName,
                ["callbackClass"] = typeof(ClientWebPatch).FullName,
                ["callbackMethod"] = nameof(ClientWebPatch.PatchIndexHtml)
            };
            register.Invoke(null, [patch]);
            _logger.LogInformation("Online Cinema web client integration registered.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cannot register Online Cinema web integration");
        }
        return Task.CompletedTask;
    }
}
