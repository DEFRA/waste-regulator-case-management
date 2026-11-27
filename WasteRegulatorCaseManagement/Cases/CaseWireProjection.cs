using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using MongoDB.Bson;

namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// Maps engine projections to API wire shapes. Kept out of
/// <see cref="CaseEndpoints"/> so endpoint methods stay within Sonar's
/// parameter-count budget (S107).
/// </summary>
internal static class CaseWireProjection
{
	internal static CaseListItemResponse ToListItemResponse(CaseEngineProjection projection) =>
		ToListItem(MapListCore(projection.Case, projection.TemplateVersion, projection.AvailableActions));

	internal static CaseResponse ToDetailResponse(CaseEngineProjection projection)
	{
		var w = projection.Case;
		return ToDetail(
			MapListCore(w, projection.TemplateVersion, projection.AvailableActions),
			w.CompletionEvent,
			w.Decision,
			w.Timeline
		);
	}

	[SuppressMessage(
		"Major Code Smell",
		"S107:Methods should not have too many parameters",
		Justification = "Flat OpenAPI list row; nested DTOs would change the wire shape."
	)]
	private static CaseWireListCore MapListCore(
		Case caseRecord,
		string templateVersion,
		IReadOnlyCollection<CaseTransition> availableActions
	) =>
		new(
			caseRecord.Id,
			caseRecord.CaseType,
			caseRecord.StateId,
			caseRecord.IsTerminal,
			caseRecord.SubmittedAt,
			caseRecord.LastModifiedAt,
			caseRecord.SubmittedBy,
			templateVersion,
			caseRecord.SourceSystemId,
			caseRecord.Nation,
			caseRecord.OrgId,
			caseRecord.ProducerType,
			caseRecord.DisplayData,
			availableActions,
			caseRecord.AssignedToId,
			caseRecord.AssignedToName,
			caseRecord.AssignedAt,
			caseRecord.AssignedBy
		);

	private static CaseListItemResponse ToListItem(CaseWireListCore core) =>
		new(
			core.Id,
			core.CaseType,
			core.StateId,
			core.IsTerminal,
			core.SubmittedAt,
			core.LastModifiedAt,
			core.SubmittedBy,
			core.TemplateVersion,
			core.SourceSystemId,
			core.Nation,
			core.OrgId,
			core.ProducerType,
			core.DisplayData,
			core.AvailableActions,
			core.AssignedToId,
			core.AssignedToName,
			core.AssignedAt,
			core.AssignedBy
		);

	[SuppressMessage(
		"Major Code Smell",
		"S107:Methods should not have too many parameters",
		Justification = "Flat OpenAPI case detail; nested DTOs would change the wire shape."
	)]
	private static CaseResponse ToDetail(
		CaseWireListCore core,
		CompletionEventSpec completionEvent,
		IReadOnlyDictionary<string, string>? decision,
		IReadOnlyList<CaseTimelineEntry> timeline
	) =>
		new(
			core.Id,
			core.CaseType,
			core.StateId,
			core.IsTerminal,
			core.SubmittedAt,
			core.LastModifiedAt,
			core.SubmittedBy,
			core.TemplateVersion,
			core.SourceSystemId,
			MapCompletionEvent(completionEvent),
			core.Nation,
			core.OrgId,
			core.ProducerType,
			core.DisplayData,
			core.AvailableActions,
			decision,
			core.AssignedToId,
			core.AssignedToName,
			core.AssignedAt,
			core.AssignedBy,
			MapTimeline(timeline)
		);

	private static CompletionEventResponse MapCompletionEvent(CompletionEventSpec spec) =>
		new(
			spec.EventType,
			JsonDocument.Parse(spec.PayloadTemplate.ToJson()).RootElement.Clone()
		);

	private static IReadOnlyCollection<CaseTimelineEntryResponse> MapTimeline(
		IReadOnlyList<CaseTimelineEntry> timeline
	) =>
		timeline
			.Select((e, i) => (Entry: e, Index: i))
			.OrderByDescending(x => x.Entry.PerformedAt)
			.ThenByDescending(x => x.Index)
			.Select(x => new CaseTimelineEntryResponse(
				x.Entry.Id,
				x.Entry.Action,
				x.Entry.ActionDisplayName,
				x.Entry.Details,
				x.Entry.PerformedAt,
				x.Entry.PerformedById,
				x.Entry.PerformedByName,
				x.Entry.StateId
			))
			.ToList();

	private readonly record struct CaseWireListCore(
		string Id,
		string CaseType,
		string StateId,
		bool IsTerminal,
		DateTime SubmittedAt,
		DateTime LastModifiedAt,
		string? SubmittedBy,
		string TemplateVersion,
		string SourceSystemId,
		string Nation,
		string OrgId,
		string? ProducerType,
		IReadOnlyDictionary<string, CaseDisplayField> DisplayData,
		IReadOnlyCollection<CaseTransition> AvailableActions,
		string? AssignedToId,
		string? AssignedToName,
		DateTime? AssignedAt,
		string? AssignedBy
	);
}