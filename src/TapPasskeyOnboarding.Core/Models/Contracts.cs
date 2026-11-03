namespace TapPasskeyOnboarding.Core.Models;

public sealed record DirectoryUser(
    string ObjectId,
    string DisplayName,
    string UserType,
    bool AccountEnabled,
    bool IsServiceAccount);

public sealed record EligibilityDecision(
    bool IsEligible,
    string Code,
    string Detail,
    DirectoryUser? User = null);

public sealed record TapIssuance(string Secret, DateTimeOffset ExpiresAt);

public sealed record CompletionDecision(
    bool PasskeyConfirmed,
    string GroupTransitionStatus,
    string CorrelationId);

public sealed record AuditEvent(
    string Operation,
    string SubjectObjectId,
    string Decision,
    string CorrelationId,
    DateTimeOffset Timestamp,
    int? GraphStatusCode = null);
