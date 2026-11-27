using AwesomeAssertions.Execution;
using MongoDB.Bson;
using NUlid;
using WasteRegulatorCaseManagement.Cases;
using WasteRegulatorCaseManagement.Test.TestSupport;

namespace WasteRegulatorCaseManagement.Test.Cases;

public sealed class CaseWireProjectionTests
{
	[Fact]
	public void ToDetailResponse_projects_completion_event_and_newest_first_timeline()
	{
		var newer = new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc);
		var older = new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc);
		var caseRecord = new Case
		{
			Id = Ulid.NewUlid().ToString(),
			CaseType = "CSoC",
			StateId = "ACCEPTED",
			IsTerminal = true,
			SubmittedAt = older,
			LastModifiedAt = newer,
			SourceSystemId = Ulid.NewUlid().ToString(),
			OrgId = "org-1",
			Nation = "ENGLAND",
			CompletionEvent = CaseTestCompletionEvent.DefaultSpec,
			Timeline =
			{
				new CaseTimelineEntry
				{
					Id = Ulid.NewUlid().ToString(),
					Action = "note-added",
					ActionDisplayName = "Note added",
					PerformedAt = older,
					Details = new() { ["text"] = "older" },
				},
				new CaseTimelineEntry
				{
					Id = Ulid.NewUlid().ToString(),
					Action = "state-changed",
					ActionDisplayName = "Action applied",
					PerformedAt = newer,
					Details = new() { ["toStateId"] = "ACCEPTED" },
				},
			},
		};
		var projection = new CaseEngineProjection(caseRecord, "v1", []);

		var detail = CaseWireProjection.ToDetailResponse(projection);
		var list = CaseWireProjection.ToListItemResponse(projection);

		using var scope = new AssertionScope();
		list.Should().BeEquivalentTo(
			new
			{
				Id = caseRecord.Id,
				CaseType = caseRecord.CaseType,
				StateId = caseRecord.StateId,
				IsTerminal = caseRecord.IsTerminal,
				TemplateVersion = "v1",
			}
		);
		detail.CompletionEvent.EventType.Should().Be(CaseTestCompletionEvent.DefaultEventType);
		detail.CompletionEvent.Payload.GetProperty("status").GetString()
			.Should().Be("{{state.callbackStatus}}");
		detail.Timeline.Should().HaveCount(2);
		detail.Timeline!.Select(e => e.Action).Should().Equal("state-changed", "note-added");
	}
}