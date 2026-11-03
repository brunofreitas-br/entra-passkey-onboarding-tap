using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using TapPasskeyOnboarding.Core.Abstractions;
using TapPasskeyOnboarding.Core.Graph;

namespace TapPasskeyOnboarding.SmokeTests;

public sealed class RouteSmokeTests : IClassFixture<TestApplication>
{
    private readonly HttpClient _client;
    public RouteSmokeTests(TestApplication application) => _client = application.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        BaseAddress = new Uri("http://localhost")
    });

    [Fact]
    public async Task Health_routes_and_protected_pages_respond()
    {
        Assert.Equal(System.Net.HttpStatusCode.OK, (await _client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await _client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await _client.GetAsync("/")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Gone, (await _client.GetAsync("/tap")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await _client.GetAsync("/confirm")).StatusCode);
    }

    [Fact]
    public async Task Page_and_static_requests_do_not_consume_mutation_rate_limit()
    {
        for (var attempt = 0; attempt < 25; attempt++)
            Assert.Equal(System.Net.HttpStatusCode.OK, (await _client.GetAsync("/")).StatusCode);
    }

    [Theory]
    [InlineData("/?handler=IssueTap")]
    [InlineData("/confirm?handler=Verify")]
    public async Task Mutation_routes_reject_missing_antiforgery_without_calling_graph(string route)
    {
        using var response = await _client.PostAsync(route, content: null);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("LOCAL-ONE-TIME-TAP", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public void Production_wiring_uses_real_graph_client_and_user_assigned_managed_identity()
    {
        using var application = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            var contentRoot = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "../../../../../src/TapPasskeyOnboarding.Web"));
            builder.UseContentRoot(contentRoot);
            builder.UseEnvironment("Production");
            builder.UseSetting("AzureAd:TenantId", "11111111-1111-1111-1111-111111111111");
            builder.UseSetting("AzureAd:ClientId", "22222222-2222-2222-2222-222222222222");
            builder.UseSetting("Graph:OnboardingGroupId", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            builder.UseSetting("Graph:CompletionGroupId", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
            builder.UseSetting("Graph:ManagedIdentityClientId", "33333333-3333-3333-3333-333333333333");
            builder.UseSetting("Graph:BaseUrl", "https://graph.microsoft.com/");
            builder.UseSetting("Graph:UseMock", "true");
            builder.UseSetting("CompletionQueue:Uri", "https://storage.queue.core.windows.net/completion-transitions");
        });

        Assert.IsType<ManagedIdentityCredential>(application.Services.GetRequiredService<TokenCredential>());
        Assert.IsType<GraphHttpDirectoryClient>(application.Services.GetRequiredService<IGraphDirectoryClient>());
        var oidc = application.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OpenIdConnectDefaults.AuthenticationScheme);
        Assert.Equal(OpenIdConnectPrompt.Login, oidc.Prompt);
        Assert.Equal(TimeSpan.FromMinutes(15), oidc.MaxAge);
        Assert.False(oidc.MapInboundClaims);
    }
}

public sealed class TestApplication : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var contentRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../src/TapPasskeyOnboarding.Web"));
        builder.UseEnvironment("Testing").UseContentRoot(contentRoot);
    }
}
