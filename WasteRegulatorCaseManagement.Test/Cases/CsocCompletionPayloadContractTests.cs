using System.Text.Json;
using MongoDB.Bson;
using WasteRegulatorCaseManagement.Cases;

namespace WasteRegulatorCaseManagement.Test.Cases;

/// <summary>
/// Mirrors the Waste Obligations API <c>UpdateComplianceDeclarationRequest</c>
/// shape without taking a project reference — ensures rendered CSoC payloads
/// stay compatible with the downstream consumer.
/// </summary>
public sealed class CsocCompletionPayloadContractTests
{
	private static readonly BsonDocument CsOcTemplate = BsonDocument.Parse(
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
	);

	[Fact]
	public void Accept_rendered_payload_deserializes_to_wo_update_compliance_declaration_shape()
	{
		var rendered = CaseEventPayload.Render(
			CsOcTemplate,
			new Dictionary<string, string?>
			{
				["state.callbackStatus"] = "Accepted",
				["actor.id"] = "ae5cf737-333a-4af0-8d91-18f7768ceb96",
				["actor.email"] = "caseworker@regulator.example",
				["actor.name"] = "Casey Worker",
			}
		);

		var dto = JsonSerializer.Deserialize<UpdateComplianceDeclarationRequest>(
			rendered.ToJson(),
			s_jsonOptions
		);

		dto.Should().BeEquivalentTo(
			new UpdateComplianceDeclarationRequest
			{
				Status = "Accepted",
				Reason = null,
				User = new ComplianceDeclarationUser
				{
					Id = "ae5cf737-333a-4af0-8d91-18f7768ceb96",
					Email = "caseworker@regulator.example",
					Name = "Casey Worker",
				},
			}
		);
	}

	[Fact]
	public void Cancel_rendered_payload_deserializes_to_wo_update_compliance_declaration_shape()
	{
		var rendered = CaseEventPayload.Render(
			CsOcTemplate,
			new Dictionary<string, string?>
			{
				["state.callbackStatus"] = "Cancelled",
				["decision.reason"] = "Submitted against the wrong obligation year.",
				["actor.id"] = "ae5cf737-333a-4af0-8d91-18f7768ceb96",
				["actor.email"] = "caseworker@regulator.example",
				["actor.name"] = "Casey Worker",
			}
		);

		var dto = JsonSerializer.Deserialize<UpdateComplianceDeclarationRequest>(
			rendered.ToJson(),
			s_jsonOptions
		);

		ArgumentNullException.ThrowIfNull(dto);
		dto.Reason.Should().Be("Submitted against the wrong obligation year.");
		dto.Status.Should().Be("Cancelled");
	}

	private static readonly JsonSerializerOptions s_jsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
	};

	private sealed class UpdateComplianceDeclarationRequest
	{
		public string? Status { get; init; }
		public string? Reason { get; init; }
		public ComplianceDeclarationUser? User { get; init; }
	}

	private sealed class ComplianceDeclarationUser
	{
		public string? Id { get; init; }
		public string? Email { get; init; }
		public string? Name { get; init; }
	}
}