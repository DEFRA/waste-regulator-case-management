using System.Text.Json;
using WasteRegulatorCaseManagement.Cases;

namespace WasteRegulatorCaseManagement.Test.Cases;

public sealed class CaseIntakeBindingTests
{
	[Fact]
	public void TryRead_returns_an_error_when_nation_is_not_recognised()
	{
		using var payload = JsonDocument.Parse("""{"status":"ok"}""");
		var request = new CaseIntakeRequest(
			CaseType: "CSoC",
			SourceSystemId: "src-1",
			CompletionEvent: new CompletionEventIntakeRequest("csoc.case.concluded", payload.RootElement),
			Nation: "MARS",
			OrgId: "org-1"
		);

		var error = CaseIntakeBinding.TryRead(request, out var intake);

		error.Should().Contain("'nation' must be one of");
		intake.Should().BeNull();
	}

	[Fact]
	public void TryRead_returns_an_error_when_completion_event_type_is_empty()
	{
		using var payload = JsonDocument.Parse("""{"status":"{{state.callbackStatus}}"}""");
		var request = ValidRequest(completionEvent: new CompletionEventIntakeRequest("  ", payload.RootElement));

		var error = CaseIntakeBinding.TryRead(request, out var intake);

		error.Should().Be("'completionEvent.eventType' is required and must be a non-empty string.");
		intake.Should().BeNull();
	}

	[Fact]
	public void TryRead_returns_a_valid_intake_and_normalizes_unspecified_created_at_to_utc()
	{
		var createdAt = new DateTime(2026, 6, 15, 9, 30, 0, DateTimeKind.Unspecified);
		var request = ValidRequest(createdAt: createdAt);

		var error = CaseIntakeBinding.TryRead(request, out var intake);

		error.Should().BeNull();
		intake.Should().NotBeNull();
		intake.CreatedAt.Should().Be(DateTime.SpecifyKind(createdAt, DateTimeKind.Utc));
	}

	[Fact]
	public void TryRead_converts_local_created_at_to_utc()
	{
		var local = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Local);
		var request = ValidRequest(createdAt: local);

		var error = CaseIntakeBinding.TryRead(request, out var intake);

		error.Should().BeNull();
		intake.Should().NotBeNull();
		intake.CreatedAt.Should().Be(local.ToUniversalTime());
	}

	[Fact]
	public void TryRead_returns_an_error_when_initial_state_is_blank()
	{
		var request = ValidRequest(initialState: "   ");

		var error = CaseIntakeBinding.TryRead(request, out var intake);

		error.Should().Be("'initialState' is required and must be a non-empty string.");
		intake.Should().BeNull();
	}

	[Fact]
	public void TryRead_returns_an_error_when_completion_payload_is_not_a_json_object()
	{
		using var payload = JsonDocument.Parse("\"only a string\"");
		var request = ValidRequest(completionEvent: new CompletionEventIntakeRequest("csoc.case.concluded", payload.RootElement));

		var error = CaseIntakeBinding.TryRead(request, out var intake);

		error.Should().Be("'completionEvent.payload' must be a JSON object.");
		intake.Should().BeNull();
	}

	private static CaseIntakeRequest ValidRequest(
		CompletionEventIntakeRequest? completionEvent = null,
		DateTime? createdAt = null,
		string? initialState = null
	)
	{
		using var payload = JsonDocument.Parse("""{"status":"ok"}""");
		return new CaseIntakeRequest(
			CaseType: "CSoC",
			SourceSystemId: "src-1",
			CompletionEvent: completionEvent ?? new CompletionEventIntakeRequest("csoc.case.concluded", payload.RootElement.Clone()),
			Nation: "ENGLAND",
			OrgId: "org-1",
			InitialState: initialState,
			CreatedAt: createdAt
		);
	}
}