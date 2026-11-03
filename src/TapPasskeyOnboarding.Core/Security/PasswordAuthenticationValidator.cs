using System.Security.Claims;

namespace TapPasskeyOnboarding.Core.Security;

public sealed class PasswordAuthenticationValidator(TimeProvider timeProvider, TimeSpan maximumAge)
{
    private static readonly HashSet<string> PasswordMethods =
        new(["pwd", "password", "passwordAuthentication"], StringComparer.OrdinalIgnoreCase);

    public bool Validate(ClaimsPrincipal principal, string expectedTenantId, out string reason)
    {
        var oid = principal.FindFirst("oid")?.Value;
        var tenant = principal.FindFirst("tid")?.Value;
        if (string.IsNullOrWhiteSpace(oid))
        {
            reason = "The validated token has no oid claim.";
            return false;
        }
        if (!string.Equals(tenant, expectedTenantId, StringComparison.OrdinalIgnoreCase))
        {
            reason = "The validated token tenant is not allowed.";
            return false;
        }

        var methods = principal.FindAll("amr").Select(c => c.Value)
            .Concat(principal.FindAll("acrs").Select(c => c.Value))
            .SelectMany(v => v.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries));
        if (!methods.Any(PasswordMethods.Contains))
        {
            reason = "Password authentication could not be established from validated claims.";
            return false;
        }

        var authTimeValue = principal.FindFirst("auth_time")?.Value;
        if (!long.TryParse(authTimeValue, out var seconds))
        {
            reason = "The authentication time claim is missing or invalid.";
            return false;
        }
        var age = timeProvider.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(seconds);
        if (age < TimeSpan.Zero || age > maximumAge)
        {
            reason = "The password-authenticated session is stale.";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
