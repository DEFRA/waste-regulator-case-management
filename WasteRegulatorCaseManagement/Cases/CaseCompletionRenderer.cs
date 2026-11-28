using System.Security.Claims;
using MongoDB.Bson;
using WasteRegulatorCaseManagement.AuditEvents;

namespace WasteRegulatorCaseManagement.Cases;

internal static class CaseCompletionRenderer
{
	public static CaseCompletionEnvelopeContent Build(
		Case caseRecord,
		ICaseTemplate template,
		string terminalStateId,
		IReadOnlyDictionary<string, string>? decision,
		ClaimsPrincipal user,
		DateTime occurredAt
	)
	{
		var terminalState =
			TerminalStates.Find(template, terminalStateId)
			?? throw new CaseEventPayloadException(
				$"Terminal state '{terminalStateId}' is not declared on case type '{caseRecord.CaseType}'."
			);

		var values = new Dictionary<string, string?>
		{
			["actor.id"] = user.FindFirstValue("user:id"),
			["actor.email"] = user.FindFirstValue("user:email"),
			["actor.name"] = user.FindFirstValue("user:name"),
			["state.callbackStatus"] = terminalState.DisplayName,
			["state.id"] = terminalState.Id,
		};
		foreach (var (field, value) in decision ?? new Dictionary<string, string>())
		{
			values[$"decision.{field}"] = value;
		}

		var renderedPayload = CaseEventPayload.Render(caseRecord.CompletionEvent.PayloadTemplate, values);

		return new CaseCompletionEnvelopeContent(
			caseRecord.Id,
			caseRecord.CaseType,
			caseRecord.SourceSystemId,
			caseRecord.OrgId,
			terminalStateId,
			occurredAt,
			caseRecord.CompletionEvent.EventType,
			renderedPayload
		);
	}
}