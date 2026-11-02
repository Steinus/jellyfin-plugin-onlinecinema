using System.Reflection;
using System.Runtime.Loader;
using Jellyfin.Plugin.OnlineCinema.Web;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.OnlineCinema.Services;

/// <summary>
/// Registers the web client modification with the already-installed File Transformation plugin.
/// </summary>
public sealed class ClientIntegrationTask : IScheduledTask
{
    private readonly ILogger<ClientIntegrationTask> _logger;

    public ClientIntegrationTask(ILogger<ClientIntegrationTask> logger) => _logger = logger;

    public string Name => "Online Cinema web integration";
    public string Key => "OnlineCinemaWebIntegration";
    public string Description => "Adds Online Cinema to the Jellyfin user interface.";
    public string Category => "Online Cinema";
    public bool IsHidden => false;
    public bool IsEnabled => true;
    public bool IsLogged => true;

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
    }

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        WebIntegrationStatus.RegistrationAttempts++;
        Assembly? assembly = AssemblyLoadContext.All.SelectMany(c => c.Assemblies)
            .FirstOrDefault(a => a.GetName().Name == "Jellyfin.Plugin.FileTransformation");
        MethodInfo? register = assembly?.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface")
            ?.GetMethod("RegisterTransformation");

        if (register is null)
        {
            WebIntegrationStatus.Error = "File Transformation not loaded; check that it is enabled.";
            _logger.LogWarning("File Transformation not loaded. Online Cinema user menu unavailable.");
            return Task.CompletedTask;
        }

        try
        {
            // Use the exact index.html filename like other plugins. The File Transformation
            // service prefers exact matches and will not run regex-based pipelines if
            // any other plugin already registered the exact filename.
            var registration = new JObject
            {
                ["id"] = "fa21972e-11c6-4d16-a165-40e174f5da08",
                ["fileNamePattern"] = "index.html",
                ["callbackAssembly"] = typeof(ClientWebPatch).Assembly.FullName,
                ["callbackClass"] = typeof(ClientWebPatch).FullName,
                ["callbackMethod"] = nameof(ClientWebPatch.PatchIndexHtml)
            };
            register.Invoke(null, [registration]);
            WebIntegrationStatus.Registered = true;
            WebIntegrationStatus.Error = string.Empty;
            _logger.LogInformation("Online Cinema registered for exact index.html transformation.");
        }
        catch (Exception ex)
        {
            WebIntegrationStatus.Error = ex.GetBaseException().Message;
            _logger.LogError(ex, "Online Cinema web integration registration failed");
        }

        return Task.CompletedTask;
    }
}
