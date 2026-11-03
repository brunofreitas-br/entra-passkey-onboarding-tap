using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TapPasskeyOnboarding.Core.Abstractions;
using TapPasskeyOnboarding.Core.Eligibility;
using TapPasskeyOnboarding.Core.Models;

namespace TapPasskeyOnboarding.TransitionWorker;

public sealed class CompletionTransitionFunction(
    IGraphDirectoryClient graph,
    EligibilityService eligibility,
    EligibilityOptions options,
    IAuditSink audit,
    TimeProvider timeProvider,
    ILogger<CompletionTransitionFunction> logger)
{
    [Function(nameof(CompletionTransitionFunction))]
    public async Task Run(
        [QueueTrigger("%CompletionQueueName%", Connection = "CompletionQueueConnection")] string payload,
        CancellationToken cancellationToken)
    {
        var message = JsonSerializer.Deserialize<CompletionTransitionMessage>(payload)
            ?? throw new InvalidOperationException("The queue message is invalid.");

        if (await graph.IsMemberOfGroupAsync(message.ObjectId, options.CompletionGroupId, cancellationToken) &&
            !await graph.IsMemberOfGroupAsync(message.ObjectId, options.OnboardingGroupId, cancellationToken))
            return;

        var decision = await eligibility.EvaluateAsync(message.ObjectId, cancellationToken);
        if (!decision.IsEligible)
            throw new UnauthorizedAccessException(decision.Code);
        if (!await graph.HasFido2MethodAsync(message.ObjectId, cancellationToken))
            throw new InvalidOperationException("No Passkey is registered for the subject.");

        await graph.AddMemberToGroupAsync(message.ObjectId, options.CompletionGroupId, cancellationToken);
        if (!await graph.IsMemberOfGroupAsync(message.ObjectId, options.CompletionGroupId, cancellationToken))
            throw new InvalidOperationException("Completion group membership wasn't confirmed.");

        await graph.RemoveMemberFromGroupAsync(message.ObjectId, options.OnboardingGroupId, cancellationToken);
        await audit.WriteAsync(new(
            "group_transition",
            message.ObjectId,
            "completed",
            message.CorrelationId,
            timeProvider.GetUtcNow(),
            204), cancellationToken);
        logger.LogInformation(
            "Completion transition succeeded CorrelationId={CorrelationId}",
            message.CorrelationId);
    }
}
