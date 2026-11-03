using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using TapPasskeyOnboarding.Core.Abstractions;
using TapPasskeyOnboarding.Core.Models;

namespace TapPasskeyOnboarding.Core.Graph;

public sealed class GraphHttpDirectoryClient(HttpClient httpClient, TimeProvider timeProvider) : IGraphDirectoryClient
{
    public async Task<bool> IsReachableAsync(CancellationToken cancellationToken)
    {
        // Use an already-required, read-only permission instead of adding Organization.Read.All.
        using var response = await httpClient.GetAsync("v1.0/users?$select=id&$top=1", cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<DirectoryUser?> GetUserAsync(string objectId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(
            $"v1.0/users/{Segment(objectId)}?$select=id,displayName,userType,accountEnabled,employeeType", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = json.RootElement;
        var employeeType = ReadString(root, "employeeType");
        return new(
            ReadString(root, "id"),
            ReadString(root, "displayName"),
            ReadString(root, "userType"),
            root.TryGetProperty("accountEnabled", out var enabled) && enabled.GetBoolean(),
            string.Equals(employeeType, "ServiceAccount", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<bool> IsMemberOfGroupAsync(string objectId, string groupId, CancellationToken cancellationToken)
    {
        var groups = await CheckMemberGroupsAsync(objectId, [groupId], cancellationToken);
        return groups.Contains(groupId, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<bool> IsMemberOfAnyGroupAsync(string objectId, IReadOnlyCollection<string> groupIds, CancellationToken cancellationToken)
    {
        if (groupIds.Count == 0) return false;
        return (await CheckMemberGroupsAsync(objectId, groupIds, cancellationToken)).Count > 0;
    }

    public async Task<bool> IsPrivilegedRoleHolderAsync(string objectId, CancellationToken cancellationToken)
    {
        if (await HasRoleScheduleInstanceAsync("roleAssignmentScheduleInstances", objectId, cancellationToken) ||
            await HasRoleScheduleInstanceAsync("roleEligibilityScheduleInstances", objectId, cancellationToken) ||
            await HasPrivilegedGroupEligibilityAsync(objectId, cancellationToken))
            return true;

        using var response = await httpClient.GetAsync(
            $"v1.0/users/{Segment(objectId)}/transitiveMemberOf/microsoft.graph.group?$select=id,isAssignableToRole&$top=999",
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        foreach (var group in json.RootElement.GetProperty("value").EnumerateArray())
        {
            if (!group.TryGetProperty("isAssignableToRole", out var assignable) ||
                assignable.ValueKind != JsonValueKind.True)
                continue;
            var groupId = ReadString(group, "id");
            if (await HasRoleScheduleInstanceAsync("roleAssignmentScheduleInstances", groupId, cancellationToken) ||
                await HasRoleScheduleInstanceAsync("roleEligibilityScheduleInstances", groupId, cancellationToken))
                return true;
        }
        return false;
    }

    public async Task<TapIssuance> CreateSingleUseTapAsync(string objectId, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        var payload = new
        {
            lifetimeInMinutes = checked((int)lifetime.TotalMinutes),
            isUsableOnce = true
        };
        using var response = await httpClient.PostAsJsonAsync(
            $"v1.0/users/{Segment(objectId)}/authentication/temporaryAccessPassMethods", payload, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var secret = ReadString(json.RootElement, "temporaryAccessPass");
        if (string.IsNullOrWhiteSpace(secret))
            throw new GraphOperationException("Graph returned no TAP value.", (int)response.StatusCode);
        return new(secret, timeProvider.GetUtcNow().Add(lifetime));
    }

    public async Task<bool> HasFido2MethodAsync(string objectId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(
            $"v1.0/users/{Segment(objectId)}/authentication/fido2Methods?$select=id,model", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return json.RootElement.GetProperty("value").GetArrayLength() > 0;
    }

    public async Task<bool> AddMemberToGroupAsync(string objectId, string groupId, CancellationToken cancellationToken)
    {
        if (await IsMemberOfGroupAsync(objectId, groupId, cancellationToken))
            return false;

        using var response = await httpClient.PostAsJsonAsync(
            $"v1.0/groups/{Segment(groupId)}/members/$ref",
            new Dictionary<string, string>
            {
                ["@odata.id"] = $"https://graph.microsoft.com/v1.0/directoryObjects/{Segment(objectId)}"
            },
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return true;
    }

    public async Task<bool> RemoveMemberFromGroupAsync(string objectId, string groupId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.DeleteAsync(
            $"v1.0/groups/{Segment(groupId)}/members/{Segment(objectId)}/$ref", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        await EnsureSuccessAsync(response, cancellationToken);
        return true;
    }

    public async IAsyncEnumerable<string> EnumerateGroupMemberObjectIdsAsync(
        string groupId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? next = $"v1.0/groups/{Segment(groupId)}/members/microsoft.graph.user?$select=id&$top=100";
        while (next is not null)
        {
            using var response = await httpClient.GetAsync(next, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            foreach (var item in json.RootElement.GetProperty("value").EnumerateArray())
                yield return ReadString(item, "id");
            next = json.RootElement.TryGetProperty("@odata.nextLink", out var link)
                ? link.GetString()?.Replace("https://graph.microsoft.com/", string.Empty, StringComparison.OrdinalIgnoreCase)
                : null;
        }
    }

    private async Task<IReadOnlyCollection<string>> CheckMemberGroupsAsync(
        string objectId,
        IReadOnlyCollection<string> groupIds,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"v1.0/users/{Segment(objectId)}/checkMemberGroups", new { groupIds }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return json.RootElement.GetProperty("value").EnumerateArray()
            .Select(v => v.GetString()).OfType<string>().ToArray();
    }

    private static string Segment(string value) =>
        Uri.EscapeDataString(Guid.TryParse(value, out var guid) ? guid.ToString() : throw new ArgumentException("Object IDs must be GUIDs."));

    private async Task<bool> HasRoleScheduleInstanceAsync(
        string collection,
        string principalId,
        CancellationToken cancellationToken)
    {
        var filter = Uri.EscapeDataString($"principalId eq '{principalId.Replace("'", "''", StringComparison.Ordinal)}'");
        using var response = await httpClient.GetAsync(
            $"v1.0/roleManagement/directory/{collection}?$filter={filter}&$select=id&$top=1",
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return json.RootElement.GetProperty("value").GetArrayLength() > 0;
    }

    private async Task<bool> HasPrivilegedGroupEligibilityAsync(
        string objectId,
        CancellationToken cancellationToken)
    {
        var filter = Uri.EscapeDataString($"principalId eq '{objectId.Replace("'", "''", StringComparison.Ordinal)}'");
        using var response = await httpClient.GetAsync(
            $"v1.0/identityGovernance/privilegedAccess/group/eligibilityScheduleInstances?$filter={filter}&$select=groupId&$top=999",
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        foreach (var eligibility in json.RootElement.GetProperty("value").EnumerateArray())
        {
            var groupId = ReadString(eligibility, "groupId");
            if (string.IsNullOrWhiteSpace(groupId))
                continue;
            using var groupResponse = await httpClient.GetAsync(
                $"v1.0/groups/{Segment(groupId)}?$select=isAssignableToRole",
                cancellationToken);
            await EnsureSuccessAsync(groupResponse, cancellationToken);
            using var groupJson = await JsonDocument.ParseAsync(
                await groupResponse.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            if (groupJson.RootElement.TryGetProperty("isAssignableToRole", out var assignable) &&
                assignable.ValueKind == JsonValueKind.True)
                return true;
        }
        return false;
    }

    private static string ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? value.GetString() ?? string.Empty : string.Empty;

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        _ = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new GraphOperationException("Microsoft Graph operation failed.", (int)response.StatusCode);
    }
}

public sealed class GraphOperationException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
