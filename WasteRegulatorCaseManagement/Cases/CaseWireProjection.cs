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
		MapListItem(projection.Case, projection.TemplateVersion, projection.AvailableActions);

	internal static CaseResponse ToDetailResponse(CaseEngineProjection projection)
	{
		var w = projection.Case;
		var list = MapListItem(w, projection.TemplateVersion, projection.AvailableActions);
		return new CaseResponse(
			list.Id,
			list.CaseType,
			list.StateId,
			list.IsTerminal,
			list.SubmittedAt,
			list.LastModifiedAt,
			list.SubmittedBy,
			list.TemplateVersion,
			list.SourceSystemId,
			MapCompletionEvent(w.CompletionEvent),
			list.Nation,
			list.OrgId,
			list.ProducerType,
			list.DisplayData,
			list.AvailableActions,
			w.Decision,
			list.AssignedToId,
			list.AssignedToName,
			list.AssignedAt,
			list.AssignedBy,
			MapTimeline(w.Timeline)
		);
	}

	[SuppressMessage(
		"Major Code Smell",
		"S107:Methods should not have too many parameters",
		Justification = "Flat OpenAPI list row; nested DTOs would change the wire shape."
	)]
	private static CaseListItemResponse MapListItem(
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
}