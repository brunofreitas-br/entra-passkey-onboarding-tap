using Microsoft.Extensions.Logging;
using TapPasskeyOnboarding.Core.Abstractions;
using TapPasskeyOnboarding.Core.Models;

namespace TapPasskeyOnboarding.Core.Telemetry;

public sealed class LoggingAuditSink(ILogger<LoggingAuditSink> logger) : IAuditSink
{
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Audit Operation={Operation} SubjectObjectId={SubjectObjectId} Decision={Decision} CorrelationId={CorrelationId} Timestamp={Timestamp} GraphStatusCode={GraphStatusCode}",
            auditEvent.Operation,
            auditEvent.SubjectObjectId,
            auditEvent.Decision,
            auditEvent.CorrelationId,
            auditEvent.Timestamp,
            auditEvent.GraphStatusCode);
        return Task.CompletedTask;
    }
}
