using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace WasteRegulatorCaseManagement.AuditEvents.Completion;

/// <summary>
/// Publishes case completion envelopes from the audit outbox to the completion
/// SNS topic.
/// </summary>
public sealed class CaseCompletionAuditEventProcessor(
	AuditEventLeaseService auditEventLeaseService,
	AuditEventDispatchService auditEventDispatchService,
	ICompletionEventSender completionEventSender,
	IOptions<CaseCompletionEventProcessorOptions> options,
	ILogger<CaseCompletionAuditEventProcessor> logger
) : BackgroundService
{
	private const string ProcessName = AuditEventService.CompletionProcessName;

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		if (string.IsNullOrWhiteSpace(options.Value.TopicArn))
		{
			throw new InvalidOperationException(
				$"{CaseCompletionEventProcessorOptions.SectionName}:TopicArn is required when processing is enabled."
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
				logger.LogError(exception, "Case completion event processing failed");
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
				cancellationToken,
				requireCompletionEnvelope: true
			);

			foreach (var auditEvent in auditEvents)
			{
				if (auditEvent.CompletionEnvelope is not { } envelope)
				{
					continue;
				}

				if (!await auditEventLeaseService.TryRenew(ProcessName, leaseDuration, cancellationToken))
				{
					logger.LogError(
						"Stopped case completion event processing because lease renewal failed for {ProcessName}",
						ProcessName
					);
					break;
				}

				try
				{
					await completionEventSender.Send(envelope, cancellationToken);
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
						"Failed to process completion audit event {EventId} for {ProcessName}",
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