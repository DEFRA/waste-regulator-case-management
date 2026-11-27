using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Hosting;

namespace WasteRegulatorCaseManagement.AuditEvents;

/// <summary>
/// Base class for audit-event background processors. Owns the timer loop and
/// error handling so each processor only needs to implement the batch logic.
/// </summary>
public abstract class AuditEventProcessorBase(
	string topicArn,
	string optionsSectionName,
	int pollIntervalSeconds,
	int pollJitterSeconds,
	string processingFailedMessage,
	ILogger logger
) : BackgroundService
{
	internal abstract Task ProcessBatch(CancellationToken cancellationToken);

	[ExcludeFromCodeCoverage(Justification = "BackgroundService timer loop is not unit testable.")]
	protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		if (string.IsNullOrWhiteSpace(topicArn))
		{
			throw new InvalidOperationException(
				$"{optionsSectionName}:TopicArn is required when processing is enabled."
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
				logger.LogError(exception, processingFailedMessage);
			}

			await Delay(stoppingToken);
		}
	}

	[ExcludeFromCodeCoverage(Justification = "BackgroundService timer loop is not unit testable.")]
	private Task Delay(CancellationToken cancellationToken)
	{
		var pollInterval = TimeSpan.FromSeconds(pollIntervalSeconds);
		var jitter = TimeSpan.FromSeconds(Random.Shared.Next(0, pollJitterSeconds + 1));

		return Task.Delay(pollInterval + jitter, cancellationToken);
	}
}
