using TapPasskeyOnboarding.Core.Abstractions;

namespace TapPasskeyOnboarding.Web.Services;

public sealed class InMemoryCompletionTransitionDispatcher : ICompletionTransitionDispatcher
{
    public Task DispatchAsync(string objectId, string correlationId, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
