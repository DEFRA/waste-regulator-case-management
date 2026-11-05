using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace WasteRegulatorCaseManagement.AuditEvents.Analytics;

/// <summary>
/// Polls for audit events not yet published to the analytics topic and
/// publishes them in sequence order while holding the process's lease. A
/// failed publish is retried after a delay until the attempt limit, then
/// dead-lettered on the event.
/// </summary>
public sealed class AnalyticsAuditEventProcessor(
    AuditEventLeaseService auditEventLeaseService,
    AuditEventDispatchService auditEventDispatchService,
    IAnalyticsEventSender analyticsEventSender,
    IOptions<AnalyticsAuditEventProcessorOptions> options,
    ILogger<AnalyticsAuditEventProcessor> logger
) : BackgroundService
{
    private const string ProcessName = AuditEventService.AnalyticsProcessName;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.TopicArn))
        {
            throw new InvalidOperationException(
                $"{AnalyticsAuditEventProcessorOptions.SectionName}:TopicArn is required when processing is enabled."
            );
        }

        await Delay(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatch(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Analytics audit event processing failed");
            }

            await Delay(stoppingToken);
        }
    }

    internal async Task ProcessBatch(CancellationToken cancellationToken)
    {
        var leaseDuration = TimeSpan.FromSeconds(options.Value.LeaseDurationSeconds);

        if (!await auditEventLeaseService.TryAcquire(ProcessName, leaseDuration, cancellationToken))
        {
            return;
        }

        try
        {
            var auditEvents = await auditEventDispatchService.ReadUnsent(
                ProcessName,
                options.Value.BatchSize,
                cancellationToken
            );

            foreach (var auditEvent in auditEvents)
            {
                if (!await auditEventLeaseService.TryRenew(ProcessName, leaseDuration, cancellationToken))
                {
                    logger.LogError(
                        "Stopped analytics audit event processing because lease renewal failed for {ProcessName}",
                        ProcessName
                    );
                    break;
                }

                try
                {
                    await analyticsEventSender.Send(auditEvent.ToAnalyticsEvent(), cancellationToken);
                    await auditEventDispatchService.MarkDispatched(ProcessName, auditEvent, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    await auditEventDispatchService.MarkFailed(
                        ProcessName,
                        auditEvent,
                        exception,
                        options.Value.MaxDispatchAttempts,
                        TimeSpan.FromSeconds(options.Value.FailedDispatchRetryDelaySeconds),
                        cancellationToken
                    );
                    logger.LogError(
                        exception,
                        "Failed to process audit event {EventId} for {ProcessName}",
                        auditEvent.EventId,
                        ProcessName
                    );
                }
            }
        }
        finally
        {
            await auditEventLeaseService.Release(ProcessName, cancellationToken);
        }
    }

    private Task Delay(CancellationToken cancellationToken)
    {
        var pollInterval = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);
        var jitter = TimeSpan.FromSeconds(Random.Shared.Next(0, options.Value.PollJitterSeconds + 1));

        return Task.Delay(pollInterval + jitter, cancellationToken);
    }
}
