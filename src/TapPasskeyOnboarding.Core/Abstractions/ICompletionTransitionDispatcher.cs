namespace TapPasskeyOnboarding.Core.Abstractions;

public interface ICompletionTransitionDispatcher
{
    Task DispatchAsync(string objectId, string correlationId, CancellationToken cancellationToken);
}
