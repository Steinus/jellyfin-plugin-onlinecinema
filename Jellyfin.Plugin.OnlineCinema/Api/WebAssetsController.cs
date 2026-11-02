using Jellyfin.Plugin.OnlineCinema.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.OnlineCinema.Api;

[ApiController]
[Route("OnlineCinema/Web")]
public sealed class WebAssetsController : ControllerBase
{
    [HttpGet("client.js")]
    public IActionResult Script()
    {
        WebIntegrationStatus.RecordScript();
        return Resource("client.js", "application/javascript; charset=utf-8");
    }

    [HttpGet("client.css")]
    public IActionResult Style() => Resource("client.css", "text/css; charset=utf-8");

    private IActionResult Resource(string name, string contentType)
    {
        var stream = typeof(Plugin).Assembly
            .GetManifestResourceStream($"Jellyfin.Plugin.OnlineCinema.Web.{name}");
        return stream is null ? NotFound() : File(stream, contentType);
    }
}

[ApiController]
[Authorize]
[Route("OnlineCinema/Integration")]
public sealed class IntegrationController : ControllerBase
{
    [HttpGet("Status")]
    public IActionResult Status() => Ok(new
    {
        registered = WebIntegrationStatus.Registered,
        registrationAttempts = WebIntegrationStatus.RegistrationAttempts,
        patchedPages = WebIntegrationStatus.PatchedPages,
        scriptRequests = WebIntegrationStatus.ScriptRequests,
        error = WebIntegrationStatus.Error,
        note = "Counts reset after Jellyfin restarts. Open the normal user home page to trigger patching."
    });
}
