namespace TapPasskeyOnboarding.Core.Services;

public sealed class TapIssuanceOptions
{
    public const int RequiredLifetimeMinutes = 10;

    public int LifetimeMinutes { get; init; } = RequiredLifetimeMinutes;

    public TimeSpan Lifetime => TimeSpan.FromMinutes(LifetimeMinutes);
}
