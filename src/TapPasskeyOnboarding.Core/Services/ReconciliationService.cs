using TapPasskeyOnboarding.Core.Abstractions;
using TapPasskeyOnboarding.Core.Eligibility;
using TapPasskeyOnboarding.Core.Models;

namespace TapPasskeyOnboarding.Core.Services;

public sealed class ReconciliationService(
    IGraphDirectoryClient graph,
    IAuditSink audit,
    EligibilityService eligibility,
    EligibilityOptions options,
    ICompletionTransitionDispatcher transitions,
    TimeProvider timeProvider)
{
    public async Task<int> RunAsync(int maxConcurrency, string correlationId, CancellationToken cancellationToken)
    {
        var removed = 0;
        await Parallel.ForEachAsync(
            graph.EnumerateGroupMemberObjectIdsAsync(options.OnboardingGroupId, cancellationToken),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(maxConcurrency, 1, 16), CancellationToken = cancellationToken },
            async (objectId, ct) =>
            {
                var decision = await eligibility.EvaluateAsync(objectId, ct);
                if (!decision.IsEligible || !await graph.HasFido2MethodAsync(objectId, ct))
                    return;

                await transitions.DispatchAsync(objectId, correlationId, ct);
                Interlocked.Increment(ref removed);
                await audit.WriteAsync(new AuditEvent("reconcile", objectId, "transition_queued", correlationId, timeProvider.GetUtcNow(), 202), ct);
            });
        return removed;
    }
}
