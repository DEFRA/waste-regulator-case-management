using AwesomeAssertions.Execution;
using MongoDB.Bson;
using NUlid;
using WasteRegulatorCaseManagement.Cases;
using WasteRegulatorCaseManagement.Test.TestSupport;

namespace WasteRegulatorCaseManagement.Test.Cases;

public sealed class CaseCompletionRendererTests
{
	private static readonly DateTime s_occurredAt = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);

	[Fact]
	public void Build_renders_placeholders_for_a_declared_terminal_state()
	{
		var caseRecord = SampleCase();
		var template = CsocTemplate();

		var envelope = CaseCompletionRenderer.Build(
			caseRecord,
			template,
			"ACCEPTED",
			decision: null,
			TestCaseworkerClaims.Principal(),
			s_occurredAt
		);

		using var scope = new AssertionScope();
		envelope.CaseId.Should().Be(caseRecord.Id);
		envelope.StateId.Should().Be("ACCEPTED");
		envelope.EventType.Should().Be(CaseTestCompletionEvent.DefaultEventType);
		envelope.Payload["status"].AsString.Should().Be("Accepted");
		envelope.Payload["reason"].Should().Be(BsonNull.Value);
		envelope.Payload["user"]["id"].AsString.Should().Be(TestCaseworkerClaims.UserId);
	}

	[Fact]
	public void Build_maps_decision_fields_into_the_rendered_payload()
	{
		var caseRecord = SampleCase();
		var template = CsocTemplate();

		var envelope = CaseCompletionRenderer.Build(
			caseRecord,
			template,
			"CANCELLED",
			new Dictionary<string, string> { ["reason"] = "Duplicate submission" },
			TestCaseworkerClaims.Principal(),
			s_occurredAt
		);

		envelope.Payload["reason"].AsString.Should().Be("Duplicate submission");
		envelope.StateId.Should().Be("CANCELLED");
		envelope.Payload["status"].AsString.Should().Be("Cancelled");
	}

	[Fact]
	public void Build_throws_when_the_terminal_state_is_not_on_the_template()
	{
		var build = () =>
			CaseCompletionRenderer.Build(
				SampleCase(),
				CsocTemplate(),
				"UNKNOWN",
				null,
				TestCaseworkerClaims.Principal(),
				s_occurredAt
			);

		build.Should()
			.ThrowExactly<CaseEventPayloadException>()
			.WithMessage("Terminal state 'UNKNOWN' is not declared on case type 'CSoC'.");
	}

	private static Case SampleCase() =>
		new()
		{
			Id = Ulid.NewUlid().ToString(),
			CaseType = "CSoC",
			StateId = "SUBMITTED",
			SourceSystemId = Ulid.NewUlid().ToString(),
			OrgId = "org-1",
			Nation = "ENGLAND",
			CompletionEvent = CaseTestCompletionEvent.DefaultSpec,
		};

	private static CaseModuleConfig CsocTemplate() =>
		TestCaseType.Create(
			"CSoC",
			"CSoC",
			states:
			[
				new CaseState("SUBMITTED", "Submitted"),
				new CaseState("ACCEPTED", "Accepted", IsTerminal: true),
				new CaseState("CANCELLED", "Cancelled", IsTerminal: true),
			]
		);
}