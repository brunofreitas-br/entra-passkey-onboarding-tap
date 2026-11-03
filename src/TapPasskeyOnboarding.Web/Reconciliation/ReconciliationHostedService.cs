using Microsoft.Extensions.Options;
using TapPasskeyOnboarding.Core.Services;

namespace TapPasskeyOnboarding.Web.Reconciliation;

public sealed class ReconciliationOptions
{
    public const string SectionName = "Reconciliation";

    public bool Enabled { get; init; } = true;
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(10);
    public int MaxConcurrency { get; init; } = 4;
}

public sealed class ReconciliationHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<ReconciliationOptions> options,
    ILogger<ReconciliationHostedService> logger) : BackgroundService
{
    private readonly ReconciliationOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Periodic reconciliation is disabled");
            return;
        }

        if (_options.Interval < TimeSpan.FromMinutes(1))
            throw new InvalidOperationException("Reconciliation:Interval must be at least one minute.");

        if (_options.MaxConcurrency is < 1 or > 16)
            throw new InvalidOperationException("Reconciliation:MaxConcurrency must be between 1 and 16.");

        await RunOnceAsync(stoppingToken);
        using var timer = new PeriodicTimer(_options.Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await RunOnceAsync(stoppingToken);
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<ReconciliationService>();
            var removed = await service.RunAsync(_options.MaxConcurrency, correlationId, cancellationToken);
            logger.LogInformation(
                "Reconciliation completed CorrelationId={CorrelationId} RemovedCount={RemovedCount}",
                correlationId,
                removed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Reconciliation failed CorrelationId={CorrelationId}", correlationId);
        }
    }
}
