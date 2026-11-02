using System.Threading;
using Newtonsoft.Json;

namespace Jellyfin.Plugin.OnlineCinema.Web;

public sealed class PatchRequestPayload
{
    [JsonProperty("contents")]
    public string? Contents { get; set; }
}

public static class WebIntegrationStatus
{
    public static bool Registered { get; set; }
    public static int RegistrationAttempts { get; set; }
    public static string Error { get; set; } = string.Empty;
    private static long _patched;
    private static long _scriptRequests;
    public static long PatchedPages => Interlocked.Read(ref _patched);
    public static long ScriptRequests => Interlocked.Read(ref _scriptRequests);
    public static void RecordPatch() => Interlocked.Increment(ref _patched);
    public static void RecordScript() => Interlocked.Increment(ref _scriptRequests);
}

public static class ClientWebPatch
{
    public static string PatchIndexHtml(PatchRequestPayload payload)
    {
        var source = payload.Contents ?? string.Empty;
        if (source.Contains("data-online-cinema-integration", StringComparison.Ordinal))
            return source;

        const string injection =
            "<link data-online-cinema-integration rel=\"stylesheet\" href=\"../OnlineCinema/Web/client.css?v=0.1.6\" />" +
            "<script defer src=\"../OnlineCinema/Web/client.js?v=0.1.6\"></script>";

        if (source.IndexOf("</head>", StringComparison.OrdinalIgnoreCase) < 0)
            return source;

        WebIntegrationStatus.RecordPatch();
        return source.Replace("</head>", injection + "</head>", StringComparison.OrdinalIgnoreCase);
    }
}
