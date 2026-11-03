using TapPasskeyOnboarding.Core.Models;

namespace TapPasskeyOnboarding.Core.Abstractions;

public interface IAuditSink
{
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken);
    Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken);
}
