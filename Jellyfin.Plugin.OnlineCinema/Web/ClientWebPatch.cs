using Newtonsoft.Json;

namespace Jellyfin.Plugin.OnlineCinema.Web;

public sealed class PatchRequestPayload
{
    [JsonProperty("contents")]
    public string? Contents { get; set; }
}

public static class ClientWebPatch
{
    public static string PatchIndexHtml(PatchRequestPayload payload)
    {
        var source = payload.Contents ?? string.Empty;
        const string marker = "data-online-cinema-integration";
        if (source.Contains(marker, StringComparison.Ordinal)) return source;
        const string scripts = "<link data-online-cinema-integration rel=\"stylesheet\" href=\"../OnlineCinema/Web/client.css?v=0.1.1\" />" +
            "<script defer src=\"../OnlineCinema/Web/client.js?v=0.1.1\"></script>";
        return source.Replace("</head>", scripts + "</head>", StringComparison.OrdinalIgnoreCase);
    }
}
