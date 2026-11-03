namespace TapPasskeyOnboarding.Core.Eligibility;

public sealed class EligibilityOptions
{
    public required string OnboardingGroupId { get; init; }
    public required string CompletionGroupId { get; init; }
    public HashSet<string> BreakGlassObjectIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ServiceAccountObjectIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ExclusionGroupIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
