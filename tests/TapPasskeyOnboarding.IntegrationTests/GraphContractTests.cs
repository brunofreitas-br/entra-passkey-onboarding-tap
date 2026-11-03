using System.Net;
using System.Text;
using TapPasskeyOnboarding.Core.Graph;

namespace TapPasskeyOnboarding.IntegrationTests;

public sealed class GraphContractTests
{
    [Theory]
    [InlineData(null, "https://graph.microsoft.com/", "https://graph.microsoft.com/.default")]
    [InlineData("https://graph.microsoft.us/", "https://graph.microsoft.us/", "https://graph.microsoft.us/.default")]
    public void Graph_endpoint_configuration_is_typed_and_cloud_aware(
        string? configured,
        string expectedBase,
        string expectedScope)
    {
        var options = GraphEndpointOptions.Create(configured);
        Assert.Equal(expectedBase, options.BaseUri.AbsoluteUri);
        Assert.Equal(expectedScope, options.TokenScope);
    }

    [Theory]
    [InlineData("http://graph.microsoft.com/")]
    [InlineData("https://example.test/")]
    [InlineData("https://user@graph.microsoft.com/")]
    [InlineData("https://graph.microsoft.com/v1.0")]
    public void Graph_endpoint_configuration_rejects_unsafe_targets(string configured) =>
        Assert.Throws<InvalidOperationException>(() => GraphEndpointOptions.Create(configured));

    [Fact]
    public async Task Connectivity_probe_is_read_only_and_uses_existing_user_permission()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler(request =>
        {
            captured = request;
            return Task.FromResult(Json(HttpStatusCode.OK, """{"value":[]}"""));
        });
        var client = new GraphHttpDirectoryClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/") },
            TimeProvider.System);

        Assert.True(await client.IsReachableAsync(default));
        Assert.Equal(HttpMethod.Get, captured!.Method);
        Assert.Equal("/v1.0/users", captured.RequestUri!.AbsolutePath);
        Assert.Contains("$select=id", captured.RequestUri.Query);
    }

    [Fact]
    public async Task Tap_request_uses_fixed_shape_and_secret_exists_only_in_return_value()
    {
        string? capturedBody = null;
        var handler = new StubHandler(async request =>
        {
            capturedBody = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.Created, """{"temporaryAccessPass":"one-time-value"}""");
        });
        var client = new GraphHttpDirectoryClient(new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/") }, TimeProvider.System);
        var result = await client.CreateSingleUseTapAsync("11111111-1111-1111-1111-111111111111", TimeSpan.FromMinutes(10), default);
        Assert.Equal("one-time-value", result.Secret);
        Assert.Contains("\"lifetimeInMinutes\":10", capturedBody);
        Assert.Contains("\"isUsableOnce\":true", capturedBody);
        Assert.DoesNotContain("one-time-value", capturedBody);
    }

    [Fact]
    public async Task Group_removal_is_fixed_path_and_not_found_is_idempotent()
    {
        Uri? captured = null;
        var handler = new StubHandler(request =>
        {
            captured = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        var client = new GraphHttpDirectoryClient(new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/") }, TimeProvider.System);
        var removed = await client.RemoveMemberFromGroupAsync(
            "11111111-1111-1111-1111-111111111111",
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            default);
        Assert.False(removed);
        Assert.Equal("/v1.0/groups/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/members/11111111-1111-1111-1111-111111111111/$ref", captured!.AbsolutePath);
    }

    [Fact]
    public async Task Group_addition_checks_membership_and_uses_fixed_reference_path()
    {
        var requests = new List<HttpRequestMessage>();
        string? referenceBody = null;
        var handler = new StubHandler(async request =>
        {
            requests.Add(request);
            if (request.RequestUri!.AbsolutePath.EndsWith("/checkMemberGroups", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, """{"value":[]}""");
            referenceBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var client = new GraphHttpDirectoryClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/") },
            TimeProvider.System);

        Assert.True(await client.AddMemberToGroupAsync(
            "11111111-1111-1111-1111-111111111111",
            "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
            default));
        Assert.Equal(HttpMethod.Post, requests[1].Method);
        Assert.Equal(
            "/v1.0/groups/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb/members/$ref",
            requests[1].RequestUri!.AbsolutePath);
        Assert.Contains(
            "https://graph.microsoft.com/v1.0/directoryObjects/11111111-1111-1111-1111-111111111111",
            referenceBody);
    }

    [Fact]
    public async Task Privileged_check_covers_direct_pim_eligibility()
    {
        var requestedPaths = new List<string>();
        var handler = new StubHandler(request =>
        {
            requestedPaths.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(request.RequestUri.AbsolutePath.EndsWith("/roleEligibilityScheduleInstances", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK, """{"value":[{"id":"eligible"}]}""")
                : Json(HttpStatusCode.OK, """{"value":[]}"""));
        });
        var client = new GraphHttpDirectoryClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/") },
            TimeProvider.System);

        Assert.True(await client.IsPrivilegedRoleHolderAsync(
            "11111111-1111-1111-1111-111111111111",
            default));
        Assert.Contains(requestedPaths, path => path.Contains("roleEligibilityScheduleInstances", StringComparison.Ordinal));
        Assert.DoesNotContain(requestedPaths, path => path.Contains("transitiveMemberOf", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Privileged_check_covers_role_assignable_group_eligibility()
    {
        var roleGroupId = "22222222-2222-2222-2222-222222222222";
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path.Contains("transitiveMemberOf", StringComparison.Ordinal))
                return Task.FromResult(Json(HttpStatusCode.OK,
                    $$"""{"value":[{"id":"{{roleGroupId}}","isAssignableToRole":true}]}"""));
            if (path.Contains("roleEligibilityScheduleInstances", StringComparison.Ordinal) &&
                path.Contains(Uri.EscapeDataString(roleGroupId), StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(Json(HttpStatusCode.OK, """{"value":[{"id":"group-eligible"}]}"""));
            return Task.FromResult(Json(HttpStatusCode.OK, """{"value":[]}"""));
        });
        var client = new GraphHttpDirectoryClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/") },
            TimeProvider.System);

        Assert.True(await client.IsPrivilegedRoleHolderAsync(
            "11111111-1111-1111-1111-111111111111",
            default));
    }

    [Fact]
    public async Task Privileged_check_ignores_null_role_assignable_flag()
    {
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.PathAndQuery.Contains("transitiveMemberOf", StringComparison.Ordinal))
                return Task.FromResult(Json(HttpStatusCode.OK, """{"value":[{"id":"22222222-2222-2222-2222-222222222222","isAssignableToRole":null}]}"""));
            return Task.FromResult(Json(HttpStatusCode.OK, """{"value":[]}"""));
        });
        var client = new GraphHttpDirectoryClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/") },
            TimeProvider.System);

        Assert.False(await client.IsPrivilegedRoleHolderAsync(
            "11111111-1111-1111-1111-111111111111",
            default));
    }

    [Fact]
    public async Task Privileged_check_covers_pim_for_role_assignable_groups()
    {
        const string roleGroupId = "22222222-2222-2222-2222-222222222222";
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path.Contains("privilegedAccess/group/eligibilityScheduleInstances", StringComparison.Ordinal))
                return Task.FromResult(Json(HttpStatusCode.OK, $$"""{"value":[{"groupId":"{{roleGroupId}}"}]}"""));
            if (path.Contains($"/groups/{roleGroupId}", StringComparison.Ordinal))
                return Task.FromResult(Json(HttpStatusCode.OK, """{"isAssignableToRole":true}"""));
            return Task.FromResult(Json(HttpStatusCode.OK, """{"value":[]}"""));
        });
        var client = new GraphHttpDirectoryClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/") },
            TimeProvider.System);

        Assert.True(await client.IsPrivilegedRoleHolderAsync(
            "11111111-1111-1111-1111-111111111111",
            default));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request);
    }
}
