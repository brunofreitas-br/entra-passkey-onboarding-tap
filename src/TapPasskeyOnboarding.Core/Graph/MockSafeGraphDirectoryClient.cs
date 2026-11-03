using System.Runtime.CompilerServices;
using TapPasskeyOnboarding.Core.Abstractions;
using TapPasskeyOnboarding.Core.Models;

namespace TapPasskeyOnboarding.Core.Graph;

public sealed class MockSafeGraphDirectoryClient(TimeProvider timeProvider) : IGraphDirectoryClient
{
    private readonly HashSet<string> _members = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _completed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _fido = new(StringComparer.OrdinalIgnoreCase);

    public void AddEligibleUser(string objectId, bool hasFido = false)
    {
        _members.Add(objectId);
        if (hasFido) _fido.Add(objectId);
    }

    public Task<bool> IsReachableAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    public Task<DirectoryUser?> GetUserAsync(string objectId, CancellationToken cancellationToken) =>
        Task.FromResult<DirectoryUser?>(new(objectId, "Local test user", "Member", true, false));
    public Task<bool> IsMemberOfGroupAsync(string objectId, string groupId, CancellationToken cancellationToken) =>
        Task.FromResult(groupId.Equals("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", StringComparison.OrdinalIgnoreCase)
            ? _members.Contains(objectId)
            : _completed.Contains(objectId));
    public Task<bool> IsPrivilegedRoleHolderAsync(string objectId, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> IsMemberOfAnyGroupAsync(string objectId, IReadOnlyCollection<string> groupIds, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<TapIssuance> CreateSingleUseTapAsync(string objectId, TimeSpan lifetime, CancellationToken cancellationToken) =>
        Task.FromResult(new TapIssuance("LOCAL-ONE-TIME-TAP", timeProvider.GetUtcNow().Add(lifetime)));
    public Task<bool> HasFido2MethodAsync(string objectId, CancellationToken cancellationToken) => Task.FromResult(_fido.Contains(objectId));
    public Task<bool> AddMemberToGroupAsync(string objectId, string groupId, CancellationToken cancellationToken) =>
        Task.FromResult(_completed.Add(objectId));
    public Task<bool> RemoveMemberFromGroupAsync(string objectId, string groupId, CancellationToken cancellationToken) =>
        Task.FromResult(_members.Remove(objectId));

    public async IAsyncEnumerable<string> EnumerateGroupMemberObjectIdsAsync(
        string groupId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var objectId in _members.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return objectId;
            await Task.Yield();
        }
    }
}
