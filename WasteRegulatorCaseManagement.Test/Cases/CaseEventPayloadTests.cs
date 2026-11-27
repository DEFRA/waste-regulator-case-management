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
}