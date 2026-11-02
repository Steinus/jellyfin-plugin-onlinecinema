using System.Reflection;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.OnlineCinema.Api;

[ApiController]
[Route("OnlineCinema/Web")]
public sealed class WebAssetsController : ControllerBase
{
    [HttpGet("client.js")]
    public IActionResult Script() => Resource("client.js", "application/javascript; charset=utf-8");

    [HttpGet("client.css")]
    public IActionResult Style() => Resource("client.css", "text/css; charset=utf-8");

    private IActionResult Resource(string file, string type)
    {
        var stream = typeof(Plugin).Assembly.GetManifestResourceStream($"Jellyfin.Plugin.OnlineCinema.Web.{file}");
        return stream is null ? NotFound() : File(stream, type);
    }
}
