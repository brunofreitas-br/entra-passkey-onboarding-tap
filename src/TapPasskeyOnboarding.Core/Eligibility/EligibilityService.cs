using TapPasskeyOnboarding.Core.Abstractions;
using TapPasskeyOnboarding.Core.Models;

namespace TapPasskeyOnboarding.Core.Eligibility;

public sealed class EligibilityService(IGraphDirectoryClient graph, EligibilityOptions options)
{
    public async Task<EligibilityDecision> EvaluateAsync(string objectId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(objectId))
            return Denied("missing_oid", "A validated object identifier is required.");

        var user = await graph.GetUserAsync(objectId, cancellationToken);
        if (user is null || !user.AccountEnabled)
            return Denied("user_unavailable", "The account is unavailable or disabled.");
        if (!string.Equals(user.UserType, "Member", StringComparison.OrdinalIgnoreCase))
            return Denied("guest_excluded", "Guest accounts are excluded.", user);
        if (user.IsServiceAccount || options.ServiceAccountObjectIds.Contains(objectId))
            return Denied("service_account_excluded", "Service accounts are excluded.", user);
        if (options.BreakGlassObjectIds.Contains(objectId))
            return Denied("break_glass_excluded", "Break-glass identities are excluded.", user);
        if (await graph.IsPrivilegedRoleHolderAsync(objectId, cancellationToken))
            return Denied("privileged_role_excluded", "Privileged role holders are excluded.", user);
        if (options.ExclusionGroupIds.Count > 0 &&
            await graph.IsMemberOfAnyGroupAsync(objectId, options.ExclusionGroupIds, cancellationToken))
            return Denied("exclusion_group", "Membership in a configured exclusion group was detected.", user);
        if (!await graph.IsMemberOfGroupAsync(objectId, options.OnboardingGroupId, cancellationToken))
            return Denied("not_in_onboarding_group", "Fixed onboarding group membership is required.", user);

        return new(true, "eligible", "All deterministic eligibility controls passed.", user);
    }

    private static EligibilityDecision Denied(string code, string detail, DirectoryUser? user = null) =>
        new(false, code, detail, user);
}
