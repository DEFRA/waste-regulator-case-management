using System.ComponentModel.DataAnnotations;

namespace WasteRegulatorCaseManagement.AuditEvents.Completion;

public sealed record CaseCompletionEventProcessorOptions
{
	public const string SectionName = "CaseCompletionEventProcessor";

	public bool ProcessingEnabled { get; init; }

	/// <summary>The case completion topic. Required when <see cref="ProcessingEnabled"/> is true.</summary>
	public string TopicArn { get; init; } = string.Empty;

	[Range(1, 500)]
	public int BatchSize { get; init; } = 25;

	[Range(1, 300)]
	public int PollIntervalSeconds { get; init; } = 15;

	[Range(0, 300)]
	public int PollJitterSeconds { get; init; } = 5;

	[Range(5, 3600)]
	public int LeaseDurationSeconds { get; init; } = 60;

	[Range(1, 100)]
	public int MaxDispatchAttempts { get; init; } = 5;

	[Range(1, 86400)]
	public int FailedDispatchRetryDelaySeconds { get; init; } = 60;
}