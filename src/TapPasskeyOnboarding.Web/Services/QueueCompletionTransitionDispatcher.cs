using System.Text.Json;
using Azure.Storage.Queues;
using TapPasskeyOnboarding.Core.Abstractions;
using TapPasskeyOnboarding.Core.Models;

namespace TapPasskeyOnboarding.Web.Services;

public sealed class QueueCompletionTransitionDispatcher(QueueClient queue) : ICompletionTransitionDispatcher
{
    public async Task DispatchAsync(string objectId, string correlationId, CancellationToken cancellationToken)
    {
        var message = JsonSerializer.Serialize(new CompletionTransitionMessage(objectId, correlationId));
        await queue.SendMessageAsync(
            BinaryData.FromString(message),
            cancellationToken: cancellationToken);
    }
}
