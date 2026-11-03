namespace TapPasskeyOnboarding.Core.Graph;

public sealed record GraphEndpointOptions(Uri BaseUri, string TokenScope)
{
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "graph.microsoft.com",
        "graph.microsoft.us",
        "dod-graph.microsoft.us",
        "microsoftgraph.chinacloudapi.cn"
    };

    public static GraphEndpointOptions Create(string? configuredBaseUrl)
    {
        var value = string.IsNullOrWhiteSpace(configuredBaseUrl)
            ? "https://graph.microsoft.com/"
            : configuredBaseUrl;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !AllowedHosts.Contains(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath != "/")
        {
            throw new InvalidOperationException(
                "Graph:BaseUrl must be the HTTPS root of a supported Microsoft Graph cloud.");
        }

        var baseUri = new Uri(uri.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
        return new(baseUri, $"{baseUri.GetLeftPart(UriPartial.Authority)}/.default");
    }
}
