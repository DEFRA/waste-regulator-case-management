using System.Text.Json;
using MongoDB.Bson;
using WasteRegulatorCaseManagement.Cases;

namespace WasteRegulatorCaseManagement.Test.TestSupport;

/// <summary>Shared completion event template for intake and persisted cases in tests.</summary>
public static class CaseTestCompletionEvent
{
	public const string DefaultEventType = "test.case.concluded";

	public static CompletionEventSpec DefaultSpec { get; } =
		new()
		{
			EventType = DefaultEventType,
			PayloadTemplate = BsonDocument.Parse(
				"""
				{
				  "status": "{{state.callbackStatus}}",
				  "reason": "{{decision.reason}}",
				  "user": {
				    "id": "{{actor.id}}",
				    "email": "{{actor.email}}",
				    "name": "{{actor.name}}"
				  }
				}
				"""
			),
		};

	public static object WirePayload { get; } =
		new
		{
			status = "{{state.callbackStatus}}",
			reason = "{{decision.reason}}",
			user = new
			{
				id = "{{actor.id}}",
				email = "{{actor.email}}",
				name = "{{actor.name}}",
			},
		};

	public static object WireBody { get; } =
		new { eventType = DefaultEventType, payload = WirePayload };

	public static object DefaultIntakeFields { get; } = new { completionEvent = WireBody };
}