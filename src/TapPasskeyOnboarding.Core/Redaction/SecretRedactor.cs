using System.Text.RegularExpressions;

namespace TapPasskeyOnboarding.Core.Redaction;

public static partial class SecretRedactor
{
    [GeneratedRegex(@"(?i)(temporaryAccessPass|tap|secret|password)\s*[:=]\s*[""']?[^""'\s,;}]+")]
    private static partial Regex SecretPattern();

    public static string Redact(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : SecretPattern().Replace(value, "$1=[REDACTED]");
}
