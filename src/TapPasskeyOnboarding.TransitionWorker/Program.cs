using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TapPasskeyOnboarding.Core.Abstractions;
using TapPasskeyOnboarding.Core.Eligibility;
using TapPasskeyOnboarding.Core.Graph;
using TapPasskeyOnboarding.Core.Telemetry;
using TapPasskeyOnboarding.TransitionWorker;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        var configuration = context.Configuration;
        var onboardingGroupId = RequiredGuid(configuration, "Graph:OnboardingGroupId");
        var completionGroupId = RequiredGuid(configuration, "Graph:CompletionGroupId");
        var graphEndpoint = GraphEndpointOptions.Create(configuration["Graph:BaseUrl"]);

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(graphEndpoint);
        services.AddSingleton(new EligibilityOptions
        {
            OnboardingGroupId = onboardingGroupId,
            CompletionGroupId = completionGroupId
        });
        services.AddSingleton<TokenCredential>(
            new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned));
        services.AddSingleton<IAuditSink, LoggingAuditSink>();
        services.AddTransient<WorkerGraphTokenHandler>();
        services.AddHttpClient<IGraphDirectoryClient, GraphHttpDirectoryClient>(client =>
        {
            client.BaseAddress = graphEndpoint.BaseUri;
            client.Timeout = TimeSpan.FromSeconds(30);
        }).AddHttpMessageHandler<WorkerGraphTokenHandler>().AddStandardResilienceHandler();
        services.AddScoped<EligibilityService>();
    })
    .Build();

host.Run();

static string RequiredGuid(IConfiguration configuration, string key)
{
    var value = configuration[key];
    return Guid.TryParse(value, out var guid) && guid != Guid.Empty
        ? guid.ToString()
        : throw new InvalidOperationException($"{key} must be a non-empty GUID.");
}
