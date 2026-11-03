using System.Net.Http.Headers;
using Azure.Core;
using TapPasskeyOnboarding.Core.Graph;

namespace TapPasskeyOnboarding.Web.Graph;

public sealed class GraphTokenHandler(
    TokenCredential credential,
    GraphEndpointOptions endpoint) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await credential.GetTokenAsync(
            new TokenRequestContext([endpoint.TokenScope]), cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return await base.SendAsync(request, cancellationToken);
    }
}
