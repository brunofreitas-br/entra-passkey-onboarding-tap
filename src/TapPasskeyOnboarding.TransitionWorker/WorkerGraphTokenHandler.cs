using Azure.Core;
using TapPasskeyOnboarding.Core.Graph;

namespace TapPasskeyOnboarding.TransitionWorker;

public sealed class WorkerGraphTokenHandler(
    TokenCredential credential,
    GraphEndpointOptions endpoint) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var token = await credential.GetTokenAsync(
            new TokenRequestContext([endpoint.TokenScope]),
            cancellationToken);
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);
        return await base.SendAsync(request, cancellationToken);
    }
}
