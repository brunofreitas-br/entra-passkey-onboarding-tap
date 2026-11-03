using TapPasskeyOnboarding.Core.Models;

namespace TapPasskeyOnboarding.Core.Abstractions;

public interface IGraphDirectoryClient
{
    Task<bool> IsReachableAsync(CancellationToken cancellationToken);
    Task<DirectoryUser?> GetUserAsync(string objectId, CancellationToken cancellationToken);
    Task<bool> IsMemberOfGroupAsync(string objectId, string groupId, CancellationToken cancellationToken);
    Task<bool> IsPrivilegedRoleHolderAsync(string objectId, CancellationToken cancellationToken);
    Task<bool> IsMemberOfAnyGroupAsync(string objectId, IReadOnlyCollection<string> groupIds, CancellationToken cancellationToken);
    Task<TapIssuance> CreateSingleUseTapAsync(string objectId, TimeSpan lifetime, CancellationToken cancellationToken);
    Task<bool> HasFido2MethodAsync(string objectId, CancellationToken cancellationToken);
    Task<bool> AddMemberToGroupAsync(string objectId, string groupId, CancellationToken cancellationToken);
    Task<bool> RemoveMemberFromGroupAsync(string objectId, string groupId, CancellationToken cancellationToken);
    IAsyncEnumerable<string> EnumerateGroupMemberObjectIdsAsync(string groupId, CancellationToken cancellationToken);
}
