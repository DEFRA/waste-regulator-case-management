using MongoDB.Bson;
using WasteRegulatorCaseManagement.Cases;

namespace WasteRegulatorCaseManagement.Test.Cases;

public sealed class CaseEventPayloadTests
{
	private static readonly BsonDocument Template = BsonDocument.Parse(
		"""
		{
		  "status": "{{state.callbackStatus}}",
		  "reason": "{{decision.reason}}",
		  "user": { "id": "{{actor.id}}" }
		}
		"""
	);

	[Fact]
	public void Render_maps_decision_placeholder_to_null_when_the_value_is_absent()
	{
		var values = new Dictionary<string, string?>
		{
			["state.callbackStatus"] = "Accepted",
			["actor.id"] = "user-1",
		};

		var rendered = CaseEventPayload.Render(Template, values);

		rendered["reason"].IsBsonNull.Should().BeTrue();
		rendered["status"].AsString.Should().Be("Accepted");
	}

	[Fact]
	public void Render_maps_decision_placeholder_to_the_supplied_value()
	{
		var values = new Dictionary<string, string?>
		{
			["state.callbackStatus"] = "Cancelled",
			["actor.id"] = "user-1",
			["decision.reason"] = "Wrong year",
		};

		var rendered = CaseEventPayload.Render(Template, values);

		rendered["reason"].AsString.Should().Be("Wrong year");
	}

	[Fact]
	public void Render_substitutes_placeholders_embedded_in_a_larger_string()
	{
		var template = BsonDocument.Parse("""{ "summary": "Outcome: {{state.callbackStatus}}" }""");
		var values = new Dictionary<string, string?> { ["state.callbackStatus"] = "Accepted" };

		var rendered = CaseEventPayload.Render(template, values);

		rendered["summary"].AsString.Should().Be("Outcome: Accepted");
	}

	[Fact]
	public void Render_throws_when_a_state_placeholder_has_no_value()
	{
		var template = BsonDocument.Parse("""{ "status": "{{state.callbackStatus}}" }""");
		var values = new Dictionary<string, string?>();

		var render = () => CaseEventPayload.Render(template, values);

		render.Should().Throw<CaseEventPayloadException>().WithMessage("Event field 'state.callbackStatus' has no value.");
	}

	[Fact]
	public void Render_throws_when_an_actor_placeholder_has_no_value()
	{
		var template = BsonDocument.Parse("""{ "id": "{{actor.id}}" }""");
		var values = new Dictionary<string, string?>();

		var render = () => CaseEventPayload.Render(template, values);

		render.Should().Throw<CaseEventPayloadException>().WithMessage("Event field 'actor.id' has no value.");
	}

	[Fact]
	public void Render_substitutes_placeholders_inside_an_array()
	{
		var template = BsonDocument.Parse("""{ "tags": ["{{state.callbackStatus}}"] }""");
		var values = new Dictionary<string, string?> { ["state.callbackStatus"] = "Accepted" };

		var rendered = CaseEventPayload.Render(template, values);

		rendered["tags"].AsBsonArray[0].AsString.Should().Be("Accepted");
	}

	[Fact]
	public void Render_throws_when_an_embedded_decision_placeholder_has_no_value()
	{
		var template = BsonDocument.Parse("""{ "text": "Because {{decision.reason}}" }""");
		var values = new Dictionary<string, string?> { ["state.callbackStatus"] = "Cancelled" };

		var render = () => CaseEventPayload.Render(template, values);

		render.Should().Throw<CaseEventPayloadException>().WithMessage("Event field 'decision.reason' has no value.");
	}

	[Fact]
	public void Render_preserves_numeric_values_that_are_not_templates()
	{
		var template = BsonDocument.Parse("""{ "count": 3 }""");
		var values = new Dictionary<string, string?>();

		var rendered = CaseEventPayload.Render(template, values);

		rendered["count"].AsInt32.Should().Be(3);
	}
}