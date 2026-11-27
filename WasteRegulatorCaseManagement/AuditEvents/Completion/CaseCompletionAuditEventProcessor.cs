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
) : AuditEventProcessorBase(
	options.Value.TopicArn,
	CaseCompletionEventProcessorOptions.SectionName,
	options.Value.PollIntervalSeconds,
	options.Value.PollJitterSeconds,
	"Case completion event processing failed",
	logger
)
{
	private const string ProcessName = AuditEventService.CompletionProcessName;

	internal override async Task ProcessBatch(CancellationToken cancellationToken)
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
				// ReadUnsent(requireCompletionEnvelope: true) guarantees the envelope is present.
				var envelope = auditEvent.CompletionEnvelope!;

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
}
