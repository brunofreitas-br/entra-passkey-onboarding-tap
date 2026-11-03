using TapPasskeyOnboarding.Core.Abstractions;
using TapPasskeyOnboarding.Core.Eligibility;
using TapPasskeyOnboarding.Core.Models;

namespace TapPasskeyOnboarding.Core.Services;

public sealed class OnboardingService(
    IGraphDirectoryClient graph,
    IAuditSink audit,
    EligibilityService eligibility,
    ICompletionTransitionDispatcher transitions,
    TapIssuanceOptions tapOptions,
    TimeProvider timeProvider)
{
    public async Task<TapIssuance> IssueTapAsync(string objectId, string correlationId, CancellationToken cancellationToken)
    {
        var decision = await eligibility.EvaluateAsync(objectId, cancellationToken);
        if (!decision.IsEligible)
            throw new UnauthorizedAccessException(decision.Code);

        try
        {
            var tap = await graph.CreateSingleUseTapAsync(objectId, tapOptions.Lifetime, cancellationToken);
            await audit.WriteAsync(new("tap_issue", objectId, "issued", correlationId, timeProvider.GetUtcNow(), 201), cancellationToken);
            return tap;
        }
        catch
        {
            await audit.WriteAsync(new("tap_issue", objectId, "failed", correlationId, timeProvider.GetUtcNow()), cancellationToken);
            throw;
        }
    }

    public async Task<CompletionDecision> VerifyAndCompleteAsync(
        string objectId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var decision = await eligibility.EvaluateAsync(objectId, cancellationToken);
        if (!decision.IsEligible)
            throw new UnauthorizedAccessException(decision.Code);

        if (!await graph.HasFido2MethodAsync(objectId, cancellationToken))
        {
            await audit.WriteAsync(new("passkey_verify", objectId, "not_found", correlationId, timeProvider.GetUtcNow(), 200), cancellationToken);
            return new(false, "not_removed", correlationId);
        }

        await transitions.DispatchAsync(objectId, correlationId, cancellationToken);
        await audit.WriteAsync(new("passkey_verify", objectId, "transition_queued", correlationId, timeProvider.GetUtcNow(), 202), cancellationToken);
        return new(true, "transition_queued", correlationId);
    }
}
