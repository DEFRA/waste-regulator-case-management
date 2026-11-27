using WasteRegulatorCaseManagement.Utils.Mongo;
using MongoDB.Driver;
using WasteRegulatorCaseManagement.AuditEvents;
using System.Net;
using MongoDB.Bson;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using WasteRegulatorCaseManagement.Test.TestSupport;
using WasteRegulatorCaseManagement.Utils.Logging;
using WasteRegulatorCaseManagement.Cases;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using WasteRegulatorCaseManagement.Auth;
using NUlid;
using AwesomeAssertions.Execution;

namespace WasteRegulatorCaseManagement.Test.Cases;

/// <summary>
/// Persistence is real <see cref="CasePersistence"/> backed
/// by ephemeral MongoDB. Tests seed cases via
/// <see cref="TestApplicationFactory.Persistence"/> and assert on
/// fetched-back documents rather than on a captured-in-flight reference.
/// A thin <see cref="RecordingPersistence"/> wrapper is layered on top
/// for the handful of tests that still need to inspect the
/// <see cref="CaseQuery"/> the endpoint built — the wrapper records
/// calls but delegates to the real persistence so behaviour stays end-to-end.
/// </summary>
public class CaseEndpointsTests
{
	private static readonly string[] s_expectedCapturedStateIds = ["submitted"];
	private static readonly string[] s_expectedCapturedTypeIds = ["re-accreditation", "other-type"];
	private static readonly string[] s_expectedCapturedProducerTypes = ["DIRECT_PRODUCER", "COMPLIANCE_SCHEME"];

	private const string CaseType = "test-type";
	private readonly MongoIntegrationFixture _fixture;

	public CaseEndpointsTests(MongoIntegrationFixture fixture) => _fixture = fixture;

	private TestApplicationFactory NewFactory(
		bool includeAuthHeader = true,
		string? userId = "test-user",
		IReadOnlyCollection<CaseState>? states = null,
		string? nation = "ENGLAND"
	) => new(_fixture, includeAuthHeader, userId, states, nation);

	[Fact]
	public async Task Post_returns_unauthorized_without_client_id()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory(includeAuthHeader: false);
		using var client = factory.CreateClient();

		var response = await client.PostAsJsonAsync(
			"/cases",
			new
			{
				caseType = CaseType,
				sourceSystemId = Ulid.NewUlid().ToString(),
				callbackUrl = "https://source-system.test/callback",
				nation = "ENGLAND",
				orgId = "test-org",
			},
			cancellationToken
		);

		response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task Post_returns_problem_when_typeId_missing()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();

		var response = await client.PostAsJsonAsync(
			"/cases",
			new { caseType = string.Empty },
			cancellationToken
		);

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task Post_returns_problem_when_typeId_is_not_a_string()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();

		var response = await client.PostAsJsonAsync(
			"/cases",
			new { caseType = 42 },
			cancellationToken
		);

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task Post_returns_problem_when_typeId_unknown()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();

		var response = await client.PostAsJsonAsync(
			"/cases",
			new { caseType = "unknown-type" },
			cancellationToken
		);

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
		problem!.Title.Should().Be("Unknown case type");
	}

	[Fact]
	public async Task Post_persists_case_in_initial_state_with_payload_and_submitter()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();

		var response = await client.PostAsJsonAsync(
			"/cases",
			new
			{
				caseType = CaseType,
				sourceSystemId = "csoc-2024-eng-00123",
				callbackUrl = "https://source-system.test/declarations/00123",
				nation = "ENGLAND",
				orgId = "org-00456",
				producerType = "DIRECT_PRODUCER",
				displayData = new
				{
					organisationName = new { value = "Acme Packaging Ltd", type = "string" },
					percentageMet = new { value = "87.3", type = "number" },
				},
			},
			cancellationToken
		);

		response.StatusCode.Should().Be(HttpStatusCode.Created);
		response.Headers.Location.Should().NotBeNull();
		response.Headers.Location!.AbsolutePath.Should().StartWith("/cases/");
		var body = await response.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken);
		var persisted = await factory.Persistence.GetByIdAsync(body!.Id, cancellationToken);
		var expectedDisplayData = new Dictionary<string, CaseDisplayField>
		{
			["organisationName"] = new("Acme Packaging Ltd", "string"),
			["percentageMet"] = new("87.3", "number"),
		};

		using var scope = new AssertionScope();
		// Snapshot of the type's template (states, transitions, version)
		// is frozen onto the case at submission for faithful historical
		// rendering after the case type's configuration changes.
		persisted.Should().BeEquivalentTo(
			new
			{
				CaseType,
				StateId = "submitted",
				SubmittedBy = "test-client",
				SourceSystemId = "csoc-2024-eng-00123",
				CallbackUrl = "https://source-system.test/declarations/00123",
				Nation = "ENGLAND",
				OrgId = "org-00456",
				ProducerType = "DIRECT_PRODUCER",
				DisplayData = expectedDisplayData,
				TemplateVersion = "v1",
				TemplateSnapshot = new { TemplateVersion = "v1" },
			}
		);
		body.Should().BeEquivalentTo(
			new
			{
				CaseType,
				StateId = "submitted",
				IsTerminal = false,
				SubmittedBy = "test-client",
				TemplateVersion = "v1",
				SourceSystemId = "csoc-2024-eng-00123",
				Nation = "ENGLAND",
				OrgId = "org-00456",
				ProducerType = "DIRECT_PRODUCER",
				DisplayData = expectedDisplayData,
			}
		);
		Ulid.TryParse(body.Id, out _).Should().BeTrue($"'{body.Id}' should be a ULID");
	}

	[Fact]
	public async Task Post_creates_a_CSoC_case_from_the_embedded_case_type_configuration()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();

		// Act
		var response = await client.PostAsJsonAsync(
			"/cases",
			new
			{
				caseType = "CSoC",
				sourceSystemId = "csoc-2024-eng-00124",
				callbackUrl = "https://source-system.test/declarations/00124",
				nation = "ENGLAND",
				orgId = "org-00456",
			},
			cancellationToken
		);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.Created);
		var body = await response.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken);
		body!.StateId.Should().Be("SUBMITTED");
		body.AvailableActions.Select(a => a.ActionId).OrderBy(a => a, StringComparer.Ordinal).Should().Equal(["accept", "cancel"]);
	}

	[Fact]
	public async Task Post_records_case_submitted_audit_entry_as_birth_event()
	{
		// RA-97: the timeline must start at submission. The framework
		// appends one 'case-created' entry inside the same
		// CreateAsync call that persists the new document, attributed to
		// the calling service and stamped with Case.SubmittedAt.
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory(userId: "alice-1");
		using var client = factory.CreateClient();

		var response = await client.PostAsJsonAsync(
			"/cases",
			new
			{
				caseType = CaseType,
				sourceSystemId = Ulid.NewUlid().ToString(),
				callbackUrl = "https://source-system.test/callback",
				nation = "ENGLAND",
				orgId = "test-org",
			},
			cancellationToken
		);

		response.StatusCode.Should().Be(HttpStatusCode.Created);
		var body = await response.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken);
		var persisted = await factory.Persistence.GetByIdAsync(body!.Id, cancellationToken);

		using var scope = new AssertionScope();
		// Birth event was part of the original CreateAsync (no follow-up
		// ReplaceAsync), so Version is still 0.
		persisted.Should().BeEquivalentTo(
			new
			{
				Version = 0,
				Timeline = new[]
				{
					new
					{
						Action = "case-created",
						ActionDisplayName = "Case submitted",
						PerformedById = "service:test-client",
						PerformedByName = "test-client",
						PerformedAt = persisted?.SubmittedAt,
						Details = new Dictionary<string, string?>
						{
							["caseType"] = CaseType,
							["stateId"] = "submitted",
							["templateVersion"] = "v1",
							["source"] = null,
							["clientId"] = "test-client",
						},
						StateId = "submitted",
					},
				},
			}
		);
		// The per-event state snapshot must also survive the wire
		// projection so the FE can render each entry against its own state.
		body.Timeline.Should().BeEquivalentTo([new { Action = "case-created", StateId = "submitted" }]);
	}

	[Fact]
	public async Task Post_attributes_the_new_case_to_the_intake_service_rather_than_a_user()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory(userId: null);
		using var client = factory.CreateClient();

		var response = await client.PostAsJsonAsync(
			"/cases",
			new
			{
				caseType = CaseType,
				sourceSystemId = Ulid.NewUlid().ToString(),
				callbackUrl = "https://source-system.test/callback",
				nation = "ENGLAND",
				orgId = "test-org",
			},
			cancellationToken
		);

		response.StatusCode.Should().Be(HttpStatusCode.Created);
		var body = await response.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken);
		var created = body!.Timeline!.Should().ContainSingle().Subject;
		created.PerformedById.Should().Be("service:test-client");
		created.PerformedByName.Should().Be("test-client");
	}

	[Fact]
	public async Task Post_is_forbidden_to_a_caller_other_than_the_intake_service()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		factory.IntakeClientId = "case-intake-processor";
		using var client = factory.CreateClient();

		var response = await client.PostAsJsonAsync(
			"/cases",
			new
			{
				caseType = CaseType,
				sourceSystemId = Ulid.NewUlid().ToString(),
				callbackUrl = "https://source-system.test/callback",
				nation = "ENGLAND",
				orgId = "test-org",
			},
			cancellationToken
		);

		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
		(await factory.AllItemsAsync(cancellationToken)).Should().BeEmpty();
	}

	[Fact]
	public async Task Post_records_submission_metadata_on_birth_audit_entry()
	{
		// RA-126: birth audit entry must carry source / clientId / userId
		// alongside the existing caseType / stateId / templateVersion keys so audit consumers can reconstruct the
		// submission origin without joining back to request logs.
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory(userId: "alice-1");
		using var client = factory.CreateClient();

		var response = await client.PostAsJsonAsync(
			"/cases",
			new
			{
				caseType = CaseType,
				sourceSystemId = Ulid.NewUlid().ToString(),
				callbackUrl = "https://source-system.test/callback",
				nation = "ENGLAND",
				orgId = "test-org",
				source = "operator-fe",
			},
			cancellationToken
		);

		response.StatusCode.Should().Be(HttpStatusCode.Created);
		var body = await response.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken);
		var persisted = await factory.Persistence.GetByIdAsync(body!.Id, cancellationToken);
		persisted.Should().NotBeNull();
		var entry = persisted!.Timeline.Should().ContainSingle().Subject;
		entry.Should().BeEquivalentTo(
			new
			{
				Action = "case-created",
				Details = new Dictionary<string, string?>
				{
					["caseType"] = CaseType,
					["stateId"] = "submitted",
					["templateVersion"] = "v1",
					["source"] = "operator-fe",
					["clientId"] = "test-client",
				},
			}
		);
	}

	[Fact]
	public async Task Post_records_birth_audit_entry_with_null_metadata_when_omitted()
	{
		// RA-126: source is optional; when the caller omits it the key
		// still appears on Details with a null value so audit consumers can rely on a fixed shape.
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory(userId: "alice-1");
		using var client = factory.CreateClient();

		var response = await client.PostAsJsonAsync(
			"/cases",
			new
			{
				caseType = CaseType,
				sourceSystemId = Ulid.NewUlid().ToString(),
				callbackUrl = "https://source-system.test/callback",
				nation = "ENGLAND",
				orgId = "test-org",
			},
			cancellationToken
		);

		response.StatusCode.Should().Be(HttpStatusCode.Created);
		var body = await response.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken);
		var persisted = await factory.Persistence.GetByIdAsync(body!.Id, cancellationToken);
		var entry = persisted!.Timeline.Should().ContainSingle().Subject;
		entry.Details.Should().BeEquivalentTo(
			new Dictionary<string, string?>
			{
				["caseType"] = CaseType,
				["stateId"] = "submitted",
				["templateVersion"] = "v1",
				["source"] = null,
				["clientId"] = "test-client",
			}
		);
	}

	[Theory]
	[InlineData("source", "42")]
	[InlineData("producerType", "7")]
	[InlineData("displayData", "\"Acme\"")]
	[InlineData("displayData", "{ \"organisationName\": \"Acme\" }")]
	[InlineData("initialState", "3")]
	[InlineData("createdAt", "\"yesterday\"")]
	[InlineData("createdAt", "1714521600")]
	public async Task Post_rejects_an_intake_field_of_the_wrong_json_type(string field, string value)
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();
		var body = new System.Text.Json.Nodes.JsonObject
		{
			["caseType"] = CaseType,
			["sourceSystemId"] = Ulid.NewUlid().ToString(),
			["callbackUrl"] = "https://source-system.test/callback",
			["nation"] = "ENGLAND",
			["orgId"] = "test-org",
		};
		body[field] = System.Text.Json.Nodes.JsonNode.Parse(value);

		// Act
		var response = await client.PostAsync(
			"/cases",
			new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
			cancellationToken
		);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		(await factory.AllItemsAsync(cancellationToken)).Should().BeEmpty();
	}

	[Fact]
	public async Task Post_accepts_request_without_display_data()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();

		var response = await client.PostAsJsonAsync(
			"/cases",
			new
			{
				caseType = CaseType,
				sourceSystemId = Ulid.NewUlid().ToString(),
				callbackUrl = "https://source-system.test/callback",
				nation = "ENGLAND",
				orgId = "test-org",
			},
			cancellationToken
		);

		response.StatusCode.Should().Be(HttpStatusCode.Created);
		var body = await response.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken);
		var persisted = await factory.Persistence.GetByIdAsync(body!.Id, cancellationToken);
		persisted.Should().NotBeNull();
		persisted!.DisplayData.Should().BeEmpty();
	}

	[Fact]
	public async Task Post_returns_problem_when_a_required_intake_field_is_missing()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();

		var response = await client.PostAsJsonAsync(
			"/cases",
			new
			{
				caseType = CaseType,
				callbackUrl = "https://source-system.test/callback",
				nation = "ENGLAND",
				orgId = "test-org",
			},
			cancellationToken
		);

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
		problem!.Detail.Should().Contain("sourceSystemId");
	}

	[Fact]
	public async Task Post_returns_problem_when_nation_is_not_recognised()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();

		var response = await client.PostAsJsonAsync(
			"/cases",
			new
			{
				caseType = CaseType,
				sourceSystemId = Ulid.NewUlid().ToString(),
				callbackUrl = "https://source-system.test/callback",
				nation = "England",
				orgId = "test-org",
			},
			cancellationToken
		);

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
		problem!.Detail.Should().Contain("nation");
	}

	[Fact]
	public async Task Post_returns_problem_when_callback_url_is_not_absolute()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();

		var response = await client.PostAsJsonAsync(
			"/cases",
			new
			{
				caseType = CaseType,
				sourceSystemId = Ulid.NewUlid().ToString(),
				callbackUrl = "/declarations/00123",
				nation = "ENGLAND",
				orgId = "test-org",
			},
			cancellationToken
		);

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
		problem!.Detail.Should().Contain("callbackUrl");
	}

	[Theory]
	[InlineData("""{ "value": "Acme", "type": "text" }""")]
	[InlineData("""{ "type": "string" }""")]
	[InlineData("""{ "value": "Acme" }""")]
	[InlineData("null")]
	public async Task Post_returns_problem_when_a_display_data_field_is_not_a_typed_value(string field)
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();
		var body = new System.Text.Json.Nodes.JsonObject
		{
			["caseType"] = CaseType,
			["sourceSystemId"] = Ulid.NewUlid().ToString(),
			["callbackUrl"] = "https://source-system.test/callback",
			["nation"] = "ENGLAND",
			["orgId"] = "test-org",
			["displayData"] = new System.Text.Json.Nodes.JsonObject
			{
				["organisationName"] = System.Text.Json.Nodes.JsonNode.Parse(field),
			},
		};

		// Act
		var response = await client.PostAsync(
			"/cases",
			new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
			cancellationToken
		);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
		problem!.Detail.Should().Contain("displayData.organisationName");
	}

	[Theory]
	[InlineData("callbackUrl", "null", "'callbackUrl' is required and must be a non-empty string.")]
	[InlineData("callbackUrl", "\"ftp://source-system.test/callback\"", "'callbackUrl' must be an absolute http or https URL.")]
	[InlineData("callbackUrl", "\"source-system.test/callback\"", "'callbackUrl' must be an absolute http or https URL.")]
	[InlineData("nation", "null", "'nation' is required and must be a non-empty string.")]
	[InlineData("orgId", "\"   \"", "'orgId' is required and must be a non-empty string.")]
	[InlineData("initialState", "\"  \"", "'initialState' is required and must be a non-empty string.")]
	public async Task Post_returns_problem_when_an_intake_field_has_the_wrong_shape(
		string field,
		string value,
		string expectedDetail
	)
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();
		var body = new System.Text.Json.Nodes.JsonObject
		{
			["caseType"] = CaseType,
			["sourceSystemId"] = Ulid.NewUlid().ToString(),
			["callbackUrl"] = "https://source-system.test/callback",
			["nation"] = "ENGLAND",
			["orgId"] = "test-org",
		};
		body[field] = System.Text.Json.Nodes.JsonNode.Parse(value);

		// Act
		var response = await client.PostAsync(
			"/cases",
			new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
			cancellationToken
		);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
		problem!.Detail.Should().Be(expectedDetail);
	}

	[Fact]
	public async Task Post_returns_the_existing_case_when_the_source_system_id_was_already_submitted()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();
		var submission = new
		{
			caseType = CaseType,
			sourceSystemId = "csoc-2024-eng-00999",
			callbackUrl = "https://source-system.test/callback",
			nation = "ENGLAND",
			orgId = "test-org",
		};

		var first = await client.PostAsJsonAsync("/cases", submission, cancellationToken);
		var second = await client.PostAsJsonAsync("/cases", submission, cancellationToken);

		first.StatusCode.Should().Be(HttpStatusCode.Created);
		second.StatusCode.Should().Be(HttpStatusCode.OK);
		second.Headers.GetValues(CaseEndpoints.IdempotentReplayHeader).Single().Should().Be("true");
		var firstBody = await first.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken);
		var secondBody = await second.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken);
		secondBody!.Id.Should().Be(firstBody!.Id);
	}

	[Fact]
	public async Task Post_starts_a_loaded_case_in_the_supplied_initial_state()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory(
			states: [new CaseState("submitted", "Submitted"), new CaseState("accepted", "Accepted", IsTerminal: true)]
		);
		using var client = factory.CreateClient();

		var response = await client.PostAsJsonAsync(
			"/cases",
			new
			{
				caseType = CaseType,
				sourceSystemId = Ulid.NewUlid().ToString(),
				callbackUrl = "https://source-system.test/callback",
				nation = "ENGLAND",
				orgId = "test-org",
				initialState = "accepted",
			},
			cancellationToken
		);

		response.StatusCode.Should().Be(HttpStatusCode.Created);
		var body = await response.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken);
		body!.StateId.Should().Be("accepted");
		body.IsTerminal.Should().BeTrue();
	}

	[Fact]
	public async Task Post_returns_problem_when_the_initial_state_is_not_a_state_of_the_case_type()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();

		var response = await client.PostAsJsonAsync(
			"/cases",
			new
			{
				caseType = CaseType,
				sourceSystemId = Ulid.NewUlid().ToString(),
				callbackUrl = "https://source-system.test/callback",
				nation = "ENGLAND",
				orgId = "test-org",
				initialState = "accepted",
			},
			cancellationToken
		);

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
		problem!.Detail.Should().Contain("initialState");
	}

	[Theory]
	[InlineData("2024-11-01T10:32:00Z")]
	[InlineData("2024-11-01T10:32:00")]
	[InlineData("2024-11-01T11:32:00+01:00")]
	public async Task Post_keeps_the_original_submission_time_of_a_loaded_case(string createdAt)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();

		var response = await client.PostAsJsonAsync(
			"/cases",
			new
			{
				caseType = CaseType,
				sourceSystemId = Ulid.NewUlid().ToString(),
				callbackUrl = "https://source-system.test/callback",
				nation = "ENGLAND",
				orgId = "test-org",
				createdAt,
			},
			cancellationToken
		);

		response.StatusCode.Should().Be(HttpStatusCode.Created);
		var body = await response.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken);
		body!.SubmittedAt.Should().Be(new DateTime(2024, 11, 1, 10, 32, 0, DateTimeKind.Utc));
	}

	[Fact]
	public async Task Cancel_without_a_reason_is_rejected_and_leaves_the_case_submitted()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();
		var caseId = await SubmitCSoCCaseAsync(client, "csoc-2024-eng-00125", cancellationToken);

		// Act
		var response = await client.PostAsJsonAsync(
			$"/cases/{caseId}/actions/cancel",
			new { decision = new { } },
			cancellationToken
		);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		(await response.Content.ReadAsStringAsync(cancellationToken)).Should().Contain("requires decision field 'reason'");
		var persisted = await factory.Persistence.GetByIdAsync(caseId, cancellationToken);
		persisted!.StateId.Should().Be("SUBMITTED");
	}

	[Fact]
	public async Task Cancel_with_a_reason_records_the_decision_on_the_case_and_its_timeline()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();
		var caseId = await SubmitCSoCCaseAsync(client, "csoc-2024-eng-00126", cancellationToken);

		// Act
		var response = await client.PostAsJsonAsync(
			$"/cases/{caseId}/actions/cancel",
			new { decision = new { reason = "Submitted against the wrong obligation year." }, actorName = "Casey Worker", actorEmail = "caseworker@regulator.example" },
			cancellationToken
		);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await response.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken);
		body!.StateId.Should().Be("CANCELLED");
		body.IsTerminal.Should().BeTrue();
		body.Decision!["reason"].Should().Be("Submitted against the wrong obligation year.");
		var stateChange = body.Timeline!.Should().ContainSingle(e => e.Action == "state-changed").Subject;
		stateChange.Details["decision.reason"].Should().Be("Submitted against the wrong obligation year.");
		stateChange.PerformedByName.Should().Be("Casey Worker");
	}

	[Fact]
	public async Task Intake_and_cancel_each_record_an_audit_event_with_the_case_before_and_after()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory(userId: "alice-1");
		using var client = factory.CreateClient();
		var caseId = await SubmitCSoCCaseAsync(client, "csoc-2024-eng-00129", cancellationToken);

		// Act
		await client.PostAsJsonAsync(
			$"/cases/{caseId}/actions/cancel",
			new { decision = new { reason = "Duplicate submission." }, actorName = "Casey Worker", actorEmail = "caseworker@regulator.example" },
			cancellationToken
		);

		// Assert
		var auditEvents = await factory
			.Services.GetRequiredService<IMongoDbClientFactory>()
			.GetCollection<AuditEvent>("auditEvents")
			.Find(e => e.EntityId == $"case_{caseId}")
			.SortBy(e => e.Sequence)
			.ToListAsync(cancellationToken);
		auditEvents.Should().SatisfyRespectively(
			created =>
			{
				created.Operation.Should().Be("insert");
				created.EventType.Should().Be("case-created");
				created.Actor.Should().Be("service:test-client");
				created.Entity.Should().Be("case");
				created.SchemaVersion.Should().Be("case.v1.0");
				created.Before.Should().BeNull();
				created.After!["stateId"].AsString.Should().Be("SUBMITTED");
			},
			cancelled =>
			{
				cancelled.Operation.Should().Be("update");
				cancelled.EventType.Should().Be("state-changed");
				cancelled.Actor.Should().Be("user:alice-1");
				cancelled.Version.Should().Be(1);
				cancelled.Before!["stateId"].AsString.Should().Be("SUBMITTED");
				cancelled.After!["stateId"].AsString.Should().Be("CANCELLED");
			}
		);
	}

	[Fact]
	public async Task Accept_is_applied_without_a_decision()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();
		var caseId = await SubmitCSoCCaseAsync(client, "csoc-2024-eng-00127", cancellationToken);

		// Act
		var response = await client.PostAsJsonAsync(
			$"/cases/{caseId}/actions/accept",
			new { actorName = "Casey Worker", actorEmail = "caseworker@regulator.example" },
			cancellationToken
		);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await response.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken);
		body!.StateId.Should().Be("ACCEPTED");
		body.Decision.Should().BeNull();
	}

	[Theory]
	[InlineData("[]")]
	[InlineData("""{ "decision": "Submitted in error." }""")]
	[InlineData("""{ "decision": { "reason": 42 } }""")]
	public async Task Action_rejects_a_body_that_is_not_a_decision_object(string body)
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();
		var caseId = await SubmitCSoCCaseAsync(client, Ulid.NewUlid().ToString(), cancellationToken);

		// Act
		var response = await client.PostAsync(
			$"/cases/{caseId}/actions/cancel",
			new StringContent(body, Encoding.UTF8, "application/json"),
			cancellationToken
		);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task Accept_with_a_body_that_has_no_decision_is_applied()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();
		var caseId = await SubmitCSoCCaseAsync(client, Ulid.NewUlid().ToString(), cancellationToken);

		// Act
		var response = await client.PostAsJsonAsync($"/cases/{caseId}/actions/accept", new { actorName = "Casey Worker", actorEmail = "caseworker@regulator.example" }, cancellationToken);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await response.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken);
		body!.StateId.Should().Be("ACCEPTED");
	}

	[Fact]
	public async Task Accept_patches_the_callback_url_with_the_accepted_status_and_the_deciding_user()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory(userId: "ae5cf737-333a-4af0-8d91-18f7768ceb96");
		using var client = factory.CreateClient();
		var caseId = await SubmitCSoCCaseAsync(client, "csoc-2024-eng-00130", cancellationToken);

		// Act
		var response = await client.PostAsJsonAsync(
			$"/cases/{caseId}/actions/accept",
			new { actorName = "Casey Worker", actorEmail = "caseworker@regulator.example" },
			cancellationToken
		);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var callback = factory.Callbacks.Requests.Should().ContainSingle().Subject;
		callback.Method.Should().Be(HttpMethod.Patch);
		callback.Uri.ToString().Should().Be("https://source-system.test/declarations/csoc-2024-eng-00130");
		callback.Authorization.Should().Be($"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("case-management:callback-secret"))}");
		using var body = JsonDocument.Parse(callback.Body!);
		body.RootElement.GetProperty("status").GetString().Should().Be("Accepted");
		var callbackUser = body.RootElement.GetProperty("user");
		callbackUser.GetProperty("id").GetString().Should().Be("ae5cf737-333a-4af0-8d91-18f7768ceb96");
		callbackUser.GetProperty("email").GetString().Should().Be("caseworker@regulator.example");
		callbackUser.GetProperty("name").GetString().Should().Be("Casey Worker");
	}

	[Fact]
	public async Task Cancel_patches_the_callback_url_with_the_cancelled_status_and_reason()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();
		var caseId = await SubmitCSoCCaseAsync(client, "csoc-2024-eng-00131", cancellationToken);

		// Act
		await client.PostAsJsonAsync(
			$"/cases/{caseId}/actions/cancel",
			new { decision = new { reason = "Submitted against the wrong obligation year." }, actorName = "Casey Worker", actorEmail = "caseworker@regulator.example" },
			cancellationToken
		);

		// Assert
		using var body = JsonDocument.Parse(factory.Callbacks.Requests.Should().ContainSingle().Subject.Body!);
		body.RootElement.GetProperty("status").GetString().Should().Be("Cancelled");
		body.RootElement.GetProperty("reason").GetString().Should().Be("Submitted against the wrong obligation year.");
	}

	[Fact]
	public async Task Accept_rejected_by_the_source_system_returns_502_and_records_the_failure_on_a_submitted_case()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();
		var caseId = await SubmitCSoCCaseAsync(client, "csoc-2024-eng-00132", cancellationToken);
		factory.Callbacks.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

		// Act
		var response = await client.PostAsJsonAsync(
			$"/cases/{caseId}/actions/accept",
			new { actorName = "Casey Worker", actorEmail = "caseworker@regulator.example" },
			cancellationToken
		);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
		var persisted = await factory.Persistence.GetByIdAsync(caseId, cancellationToken);
		persisted!.StateId.Should().Be("SUBMITTED");
		var failure = persisted.Timeline.Should().ContainSingle(e => e.Action == "callback-failed").Subject;
		failure.Details["actionId"].Should().Be("accept");
		failure.Details["reason"].Should().Be("https://source-system.test/declarations/csoc-2024-eng-00132 returned 500 Internal Server Error.");
	}

	[Fact]
	public async Task Accept_is_applied_when_the_source_system_already_holds_the_accepted_status()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();
		var caseId = await SubmitCSoCCaseAsync(client, "csoc-2024-eng-00133", cancellationToken);
		factory.Callbacks.Respond = request =>
			request.Method == HttpMethod.Patch
				? new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
				: new HttpResponseMessage(HttpStatusCode.OK)
				{
					Content = new StringContent("""{ "id": "csoc-2024-eng-00133", "status": "Accepted" }"""),
				};

		// Act
		var response = await client.PostAsJsonAsync(
			$"/cases/{caseId}/actions/accept",
			new { actorName = "Casey Worker", actorEmail = "caseworker@regulator.example" },
			cancellationToken
		);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		factory.Callbacks.Requests.Select(r => r.Method).Should().Equal([HttpMethod.Patch, HttpMethod.Get]);
		var body = await response.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken);
		body!.StateId.Should().Be("ACCEPTED");
	}

	[Fact]
	public async Task Accept_returns_502_when_the_source_system_refuses_it_and_holds_a_different_status()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();
		var caseId = await SubmitCSoCCaseAsync(client, "csoc-2024-eng-00134", cancellationToken);
		factory.Callbacks.Respond = request =>
			request.Method == HttpMethod.Patch
				? new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
				: new HttpResponseMessage(HttpStatusCode.OK)
				{
					Content = new StringContent("""{ "id": "csoc-2024-eng-00134", "status": "Cancelled" }"""),
				};

		// Act
		var response = await client.PostAsJsonAsync(
			$"/cases/{caseId}/actions/accept",
			new { actorName = "Casey Worker", actorEmail = "caseworker@regulator.example" },
			cancellationToken
		);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
		var persisted = await factory.Persistence.GetByIdAsync(caseId, cancellationToken);
		persisted!.StateId.Should().Be("SUBMITTED");
	}

	[Fact]
	public async Task Accept_returns_502_when_the_source_system_refuses_it_and_cannot_be_read_back()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();
		var caseId = await SubmitCSoCCaseAsync(client, "csoc-2024-eng-00136", cancellationToken);
		factory.Callbacks.Respond = request =>
			request.Method == HttpMethod.Patch
				? new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
				: new HttpResponseMessage(HttpStatusCode.NotFound);

		// Act
		var response = await client.PostAsJsonAsync(
			$"/cases/{caseId}/actions/accept",
			new { actorName = "Casey Worker", actorEmail = "caseworker@regulator.example" },
			cancellationToken
		);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
		factory.Callbacks.Requests.Select(r => r.Method).Should().Equal([HttpMethod.Patch, HttpMethod.Get]);
		var persisted = await factory.Persistence.GetByIdAsync(caseId, cancellationToken);
		persisted!.StateId.Should().Be("SUBMITTED");
	}

	[Fact]
	public async Task Accept_returns_502_and_records_the_failure_when_the_source_system_cannot_be_reached()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();
		var caseId = await SubmitCSoCCaseAsync(client, "csoc-2024-eng-00137", cancellationToken);
		factory.Callbacks.Respond = _ => throw new HttpRequestException("Connection refused");

		// Act
		var response = await client.PostAsJsonAsync(
			$"/cases/{caseId}/actions/accept",
			new { actorName = "Casey Worker", actorEmail = "caseworker@regulator.example" },
			cancellationToken
		);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
		var persisted = await factory.Persistence.GetByIdAsync(caseId, cancellationToken);
		persisted!.StateId.Should().Be("SUBMITTED");
		var failure = persisted.Timeline.Should().ContainSingle(e => e.Action == "callback-failed").Subject;
		failure.Details["reason"].Should().Be(
			"https://source-system.test/declarations/csoc-2024-eng-00137 could not be reached: Connection refused"
		);
	}

	[Fact]
	public async Task Accept_by_a_caller_without_an_email_records_the_failure_without_calling_the_source_system()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();
		var caseId = await SubmitCSoCCaseAsync(client, "csoc-2024-eng-00135", cancellationToken);

		// Act
		var response = await client.PostAsJsonAsync(
			$"/cases/{caseId}/actions/accept",
			new { actorName = "Casey Worker" },
			cancellationToken
		);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
		factory.Callbacks.Requests.Should().BeEmpty();
		var persisted = await factory.Persistence.GetByIdAsync(caseId, cancellationToken);
		var failure = persisted!.Timeline.Should().ContainSingle(e => e.Action == "callback-failed").Subject;
		failure.Details["reason"].Should().Be("Callback field 'actor.email' has no value.");
	}

	private static async Task<string> SubmitCSoCCaseAsync(
		HttpClient client,
		string sourceSystemId,
		CancellationToken cancellationToken)
	{
		var response = await client.PostAsJsonAsync(
			"/cases",
			new
			{
				caseType = "CSoC",
				sourceSystemId,
				callbackUrl = $"https://source-system.test/declarations/{sourceSystemId}",
				nation = "ENGLAND",
				orgId = "org-00456",
			},
			cancellationToken
		);
		var body = await response.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken)
			?? throw new InvalidOperationException($"Intake returned {response.StatusCode}.");
		return body.Id;
	}

	[Fact]
	public async Task Get_by_id_returns_case_when_present()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();

		var id = Ulid.NewUlid().ToString();
		await factory.SeedAsync(
			new Case
			{
				SourceSystemId = Ulid.NewUlid().ToString(),
				CallbackUrl = "https://source-system.test/callback",
				Nation = "ENGLAND",
				OrgId = "test-org",
				Id = id,
				CaseType = CaseType,
				StateId = "submitted",
				SubmittedBy = "test-client",
			},
			cancellationToken
		);

		var response = await client.GetAsync($"/cases/{id}", cancellationToken);
		response.StatusCode.Should().Be(HttpStatusCode.OK);

		var body = await response.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken);
		body.Should().BeEquivalentTo(new { Id = id, CaseType });
	}

	[Fact]
	public async Task Get_by_id_returns_not_found_when_missing()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();

		var response = await client.GetAsync($"/cases/{Ulid.NewUlid().ToString()}", cancellationToken);
		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task Get_by_id_returns_ok_for_item_not_submitted_by_caller()
	{
		// RBAC (who may look at whose items) now lives entirely in the
		// frontend/BFF; the backend trusts any authenticated (API key
		// verified) caller and does not check ownership.
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();

		var id = Ulid.NewUlid().ToString();
		await factory.SeedAsync(
			new Case
			{
				SourceSystemId = Ulid.NewUlid().ToString(),
				CallbackUrl = "https://source-system.test/callback",
				Nation = "ENGLAND",
				OrgId = "test-org",
				Id = id,
				CaseType = CaseType,
				StateId = "submitted",
				SubmittedBy = "other-tenant",
			},
			cancellationToken
		);

		var response = await client.GetAsync($"/cases/{id}", cancellationToken);
		response.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task Get_by_id_returns_not_found_for_a_case_in_another_nation()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory(nation: "ENGLAND");
		using var client = factory.CreateClient();
		var id = Ulid.NewUlid().ToString();
		await factory.SeedAsync(
			new Case
			{
				SourceSystemId = Ulid.NewUlid().ToString(),
				CallbackUrl = "https://source-system.test/callback",
				Nation = "WALES",
				OrgId = "test-org",
				Id = id,
				CaseType = CaseType,
				StateId = "submitted",
				SubmittedBy = "test-client",
			},
			cancellationToken
		);

		// Act
		var response = await client.GetAsync($"/cases/{id}", cancellationToken);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task Get_by_id_is_forbidden_to_a_caller_without_a_nation()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory(nation: null);
		using var client = factory.CreateClient();
		var id = Ulid.NewUlid().ToString();
		await factory.SeedAsync(
			new Case
			{
				SourceSystemId = Ulid.NewUlid().ToString(),
				CallbackUrl = "https://source-system.test/callback",
				Nation = "ENGLAND",
				OrgId = "test-org",
				Id = id,
				CaseType = CaseType,
				StateId = "submitted",
				SubmittedBy = "test-client",
			},
			cancellationToken
		);

		// Act
		var response = await client.GetAsync($"/cases/{id}", cancellationToken);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}

	[Fact]
	public async Task Get_list_is_forbidden_to_a_caller_without_a_nation()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory(nation: null);
		using var client = factory.CreateClient();

		// Act
		var response = await client.GetAsync("/cases", cancellationToken);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}

	[Fact]
	public async Task Get_list_returns_only_cases_in_the_callers_nation()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory(nation: "WALES");
		using var client = factory.CreateClient();
		var walesId = Ulid.NewUlid().ToString();
		await factory.SeedAsync(
			new Case
			{
				SourceSystemId = Ulid.NewUlid().ToString(),
				CallbackUrl = "https://source-system.test/callback",
				Nation = "WALES",
				OrgId = "test-org",
				Id = walesId,
				CaseType = CaseType,
				StateId = "submitted",
				SubmittedBy = "test-client",
			},
			cancellationToken
		);
		await factory.SeedAsync(
			new Case
			{
				SourceSystemId = Ulid.NewUlid().ToString(),
				CallbackUrl = "https://source-system.test/callback",
				Nation = "ENGLAND",
				OrgId = "test-org",
				Id = Ulid.NewUlid().ToString(),
				CaseType = CaseType,
				StateId = "submitted",
				SubmittedBy = "test-client",
			},
			cancellationToken
		);

		// Act
		var body = await client.GetFromJsonAsync<CaseListResponse>("/cases", cancellationToken);

		// Assert
		var item = body!.Items.Should().ContainSingle().Subject;
		item.Id.Should().Be(walesId);
	}

	[Fact]
	public async Task AddNote_returns_not_found_for_a_case_in_another_nation()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory(nation: "ENGLAND");
		using var client = factory.CreateClient();
		var id = Ulid.NewUlid().ToString();
		await factory.SeedAsync(
			new Case
			{
				SourceSystemId = Ulid.NewUlid().ToString(),
				CallbackUrl = "https://source-system.test/callback",
				Nation = "SCOTLAND",
				OrgId = "test-org",
				Id = id,
				CaseType = CaseType,
				StateId = "submitted",
				SubmittedBy = "test-client",
			},
			cancellationToken
		);

		// Act
		var response = await client.PostAsJsonAsync(
			$"/cases/{id}/notes",
			new { text = "anything" },
			cancellationToken
		);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task Get_list_returns_all_items_by_default()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();

		await factory.SeedAsync(
			new Case
			{
				SourceSystemId = Ulid.NewUlid().ToString(),
				CallbackUrl = "https://source-system.test/callback",
				Nation = "ENGLAND",
				OrgId = "test-org",
				Id = Ulid.NewUlid().ToString(),
				CaseType = CaseType,
				StateId = "submitted",
				SubmittedBy = "test-client",
			},
			cancellationToken
		);
		await factory.SeedAsync(
			new Case
			{
				SourceSystemId = Ulid.NewUlid().ToString(),
				CallbackUrl = "https://source-system.test/callback",
				Nation = "ENGLAND",
				OrgId = "test-org",
				Id = Ulid.NewUlid().ToString(),
				CaseType = CaseType,
				StateId = "submitted",
				SubmittedBy = "other-tenant",
			},
			cancellationToken
		);

		var body = await client.GetFromJsonAsync<CaseListResponse>(
			"/cases",
			cancellationToken
		);
		body.Should().NotBeNull();
		body!.Items.Should().HaveCount(2);
	}

	[Fact]
	public async Task Get_list_filters_by_organisation()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();
		await factory.SeedAsync(ListedCase(orgId: "org-00456", stateId: "submitted", isTerminal: false), cancellationToken);
		await factory.SeedAsync(ListedCase(orgId: "org-00789", stateId: "submitted", isTerminal: false), cancellationToken);

		// Act
		var body = await client.GetFromJsonAsync<CaseListResponse>("/cases?orgId=org-00456", cancellationToken);

		// Assert
		var item = body!.Items.Should().ContainSingle().Subject;
		item.OrgId.Should().Be("org-00456");
	}

	[Fact]
	public async Task Get_list_filters_by_producer_type()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();
		await factory.SeedAsync(ListedCase(orgId: "org-00456", stateId: "submitted", isTerminal: false, producerType: "DIRECT_PRODUCER"), cancellationToken);
		await factory.SeedAsync(ListedCase(orgId: "org-00789", stateId: "submitted", isTerminal: false, producerType: "COMPLIANCE_SCHEME"), cancellationToken);

		// Act
		var body = await client.GetFromJsonAsync<CaseListResponse>("/cases?producerType=DIRECT_PRODUCER", cancellationToken);

		// Assert
		var item = body!.Items.Should().ContainSingle().Subject;
		item.Should().BeEquivalentTo(new { OrgId = "org-00456", ProducerType = "DIRECT_PRODUCER" });
	}

	[Fact]
	public async Task Get_list_filters_to_concluded_cases()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();
		await factory.SeedAsync(ListedCase(orgId: "org-00456", stateId: "submitted", isTerminal: false), cancellationToken);
		await factory.SeedAsync(ListedCase(orgId: "org-00456", stateId: "approved", isTerminal: true), cancellationToken);

		// Act
		var body = await client.GetFromJsonAsync<CaseListResponse>("/cases?isTerminal=true", cancellationToken);

		// Assert
		var item = body!.Items.Should().ContainSingle().Subject;
		item.StateId.Should().Be("approved");
	}

	private static Case ListedCase(string orgId, string stateId, bool isTerminal, string? producerType = null) =>
		new()
		{
			SourceSystemId = Ulid.NewUlid().ToString(),
			CallbackUrl = "https://source-system.test/callback",
			Nation = "ENGLAND",
			OrgId = orgId,
			Id = Ulid.NewUlid().ToString(),
			CaseType = CaseType,
			StateId = stateId,
			IsTerminal = isTerminal,
			SubmittedBy = "test-client",
			ProducerType = producerType,
		};

	[Fact]
	public async Task Post_returns_problem_when_body_is_not_a_json_object()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();

		var content = new StringContent("[1,2,3]", System.Text.Encoding.UTF8, "application/json");
		var response = await client.PostAsync("/cases", content, cancellationToken);

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task Submit_rethrows_when_the_engine_throws_an_unhandled_exception()
	{
		// The Submit handler logs the exception at Error level and then
		// rethrows unchanged rather than mapping it to a ProblemDetails —
		// an unexpected engine failure should surface as a 500, not be
		// disguised as a client error.
		var cancellationToken = TestContext.Current.CancellationToken;
		var type = TestCaseType.Create(CaseType, "Test type");
		var registry = new CaseRegistry([type]);
		var engine = Substitute.For<ICaseService>();
		engine
			.SubmitAsync(
				Arg.Any<CaseModuleConfig>(),
				Arg.Any<CaseIntake>(),
				Arg.Any<string?>(),
				Arg.Any<System.Security.Claims.ClaimsPrincipal>(),
				Arg.Any<IReadOnlyDictionary<string, string?>?>(),
				Arg.Any<CancellationToken>()
			)
			.Returns(Task.FromException<CaseActionResult>(new InvalidOperationException("boom")));

		var body = new CaseIntakeRequest(
			CaseType: CaseType,
			SourceSystemId: Ulid.NewUlid().ToString(),
			CallbackUrl: "https://source-system.test/callback",
			Nation: "ENGLAND",
			OrgId: "test-org"
		);
		var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
		{
			User = new System.Security.Claims.ClaimsPrincipal(
				new System.Security.Claims.ClaimsIdentity(authenticationType: "test")
			),
		};

		var act = () =>
			CaseEndpoints.Submit(
				body,
				httpContext,
				registry,
				engine,
				Substitute.For<IStructuredLogger<CaseEndpointsLogger>>(),
				cancellationToken
			);
		await act.Should().ThrowExactlyAsync<InvalidOperationException>();
	}

	[Fact]
	public async Task Assign_maps_concurrency_conflict_to_a_409_problem()
	{
		// Covers ToHttpResult's ConcurrencyConflict switch arm, which shares
		// its target (409, "Action not allowed") with TerminalState but is
		// a distinct pattern in the `or` combinator and so needs its own
		// exercise for full branch coverage. A real optimistic-concurrency
		// race is already proven at the CaseService level (see
		// CaseServiceTests' RacingPersistence); this only needs to
		// confirm the endpoint maps the failure code correctly.
		var cancellationToken = TestContext.Current.CancellationToken;
		var engine = Substitute.For<ICaseService>();
		var caseId = Ulid.NewUlid().ToString();
		engine
			.AssignAsync(
				caseId,
				Arg.Any<string>(),
				Arg.Any<string?>(),
				Arg.Any<string?>(),
				Arg.Any<System.Security.Claims.ClaimsPrincipal>(),
				Arg.Any<CancellationToken>()
			)
			.Returns(
				CaseActionResult.Failure(
					CaseActionFailureCode.ConcurrencyConflict,
					"Case was modified concurrently. Reload and retry."
				)
			);

		var body = new AssignCaseRequest(AssigneeId: "alice-1");
		var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
		{
			User = new System.Security.Claims.ClaimsPrincipal(
				new System.Security.Claims.ClaimsIdentity(authenticationType: "test")
			),
		};

		var result = await CaseEndpoints.Assign(
			caseId, body, httpContext, engine, cancellationToken);

		var problem = result.Result.Should().BeOfType<ProblemHttpResult>().Subject;
		problem.StatusCode.Should().Be(Microsoft.AspNetCore.Http.StatusCodes.Status409Conflict);
	}

	[Fact]
	public async Task Submit_maps_an_unrecognised_failure_code_to_a_generic_400_problem()
	{
		// The only CaseActionFailureCode SubmitAsync can plausibly return
		// is MissingActorIdentity, which has its own named switch arm; this
		// exercises the fallback arm that catches anything else.
		var cancellationToken = TestContext.Current.CancellationToken;
		var type = TestCaseType.Create(CaseType, "Test type");
		var registry = new CaseRegistry([type]);
		var engine = Substitute.For<ICaseService>();
		engine
			.SubmitAsync(
				Arg.Any<CaseModuleConfig>(),
				Arg.Any<CaseIntake>(),
				Arg.Any<string?>(),
				Arg.Any<System.Security.Claims.ClaimsPrincipal>(),
				Arg.Any<IReadOnlyDictionary<string, string?>?>(),
				Arg.Any<CancellationToken>()
			)
			.Returns(
				CaseActionResult.Failure(
					CaseActionFailureCode.UnknownAction,
					"Something else went wrong."
				)
			);

		var body = new CaseIntakeRequest(
			CaseType: CaseType,
			SourceSystemId: Ulid.NewUlid().ToString(),
			CallbackUrl: "https://source-system.test/callback",
			Nation: "ENGLAND",
			OrgId: "test-org"
		);
		var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
		{
			User = new System.Security.Claims.ClaimsPrincipal(
				new System.Security.Claims.ClaimsIdentity(authenticationType: "test")
			),
		};

		var result = await CaseEndpoints.Submit(
			body,
			httpContext,
			registry,
			engine,
			Substitute.For<IStructuredLogger<CaseEndpointsLogger>>(),
			cancellationToken
		);

		var problem = result.Result.Should().BeOfType<ProblemHttpResult>().Subject;
		problem.Should().BeEquivalentTo(
			new
			{
				StatusCode = Microsoft.AspNetCore.Http.StatusCodes.Status400BadRequest,
				ProblemDetails = new { Title = "Invalid request" },
			}
		);
	}

	[Fact]
	public async Task Get_list_rejects_page_above_cap_with_400()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();

		var response = await client.GetAsync(
			$"/cases?page={CaseQuery.MaxPage + 1}",
			cancellationToken
		);

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		// Persistence must never be hit for an out-of-range page — that's the
		// whole point of the cap.
		factory.Recording.LastQuery.Should().BeNull();
	}

	[Fact]
	public async Task Get_returns_paginated_envelope_for_all_persisted_cases()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();

		await factory.SeedAsync(
			new Case
			{
				SourceSystemId = Ulid.NewUlid().ToString(),
				CallbackUrl = "https://source-system.test/callback",
				Nation = "ENGLAND",
				OrgId = "test-org",
				Id = Ulid.NewUlid().ToString(),
				CaseType = CaseType,
				StateId = "submitted",
				SubmittedBy = "test-client",
			},
			cancellationToken
		);
		await factory.SeedAsync(
			new Case
			{
				SourceSystemId = Ulid.NewUlid().ToString(),
				CallbackUrl = "https://source-system.test/callback",
				Nation = "ENGLAND",
				OrgId = "test-org",
				Id = Ulid.NewUlid().ToString(),
				CaseType = CaseType,
				StateId = "submitted",
				SubmittedBy = "test-client",
			},
			cancellationToken
		);

		var body = await client.GetFromJsonAsync<CaseListResponse>(
			"/cases",
			cancellationToken
		);
		body.Should().NotBeNull();

		using var scope = new AssertionScope();
		body!.Items.Should().HaveCount(2);
		body.Should().BeEquivalentTo(
			new
			{
				TotalCount = 2L,
				Page = 1,
				PageSize = CaseQuery.DefaultPageSize,
			}
		);
	}

	[Fact]
	public async Task Get_list_omits_the_timeline_from_each_item()
	{
		// The list endpoint never renders the per-item Timeline,
		// so it must not appear on the wire at all (omitted, not nulled) —
		// even if the persisted document would otherwise have it.
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();

		var assignedAt = new DateTime(2026, 4, 27, 9, 0, 0, DateTimeKind.Utc);
		var caseRecord = new Case
		{
			SourceSystemId = Ulid.NewUlid().ToString(),
			CallbackUrl = "https://source-system.test/callback",
			Nation = "ENGLAND",
			OrgId = "test-org",
			Id = Ulid.NewUlid().ToString(),
			CaseType = CaseType,
			StateId = "submitted",
			SubmittedBy = "test-client",
			AssignedToId = "alice-1",
			AssignedToName = "Alice Example",
			AssignedAt = assignedAt,
			AssignedBy = "manager-1",
			Timeline =
			{
				new CaseTimelineEntry
				{
					Action = "assigned",
					ActionDisplayName = "Assigned",
					PerformedAt = assignedAt,
					PerformedById = "manager-1",
				},
			},
		};
		await factory.SeedAsync(caseRecord, cancellationToken);

		var response = await client.GetAsync("/cases", cancellationToken);
		response.StatusCode.Should().Be(HttpStatusCode.OK);

		// Property-level assertion: the JSON must not even mention
		// 'timeline' on a list item.
		var rawJson = await response.Content.ReadAsStringAsync(cancellationToken);
		var root = System.Text.Json.Nodes.JsonNode.Parse(rawJson);
		root.Should().NotBeNull();
		var firstItem = root!["items"]!.AsArray()[0]!.AsObject();
		firstItem.ContainsKey("timeline").Should().BeFalse("list item must not include 'timeline'");

		var body = System.Text.Json.JsonSerializer.Deserialize<CaseListResponse>(
			rawJson,
			new System.Text.Json.JsonSerializerOptions
			{
				PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
			}
		);
		body.Should().NotBeNull();
		var item = body!.Items.Should().ContainSingle().Subject;
		item.Should().BeEquivalentTo(
			new
			{
				AssignedToId = "alice-1",
				AssignedToName = "Alice Example",
				AssignedAt = assignedAt,
				AssignedBy = "manager-1",
				StateId = "submitted",
			}
		);
	}

	[Fact]
	public async Task Get_passes_query_string_filters_to_persistence()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();

		var response = await client.GetAsync(
			"/cases?caseType=re-accreditation&caseType=other-type&stateId=submitted&search=acme&assigneeId=alice-1&unassigned=true&orgId=org-00456&producerType=DIRECT_PRODUCER&producerType=COMPLIANCE_SCHEME&page=2&pageSize=5",
			cancellationToken
		);
		response.StatusCode.Should().Be(HttpStatusCode.OK);

		var captured = factory.Recording.LastQuery;
		captured.Should().NotBeNull();

		using var scope = new AssertionScope();
		captured!.CaseTypes.Should().Equal(s_expectedCapturedTypeIds);
		captured.StateIds.Should().Equal(s_expectedCapturedStateIds);
		captured.ProducerTypes.Should().Equal(s_expectedCapturedProducerTypes);
		captured.Should().BeEquivalentTo(
			new
			{
				Search = "acme",
				AssigneeId = "alice-1",
				UnassignedOnly = true,
				OrgId = "org-00456",
				Page = 2,
				PageSize = 5,
			}
		);
	}

	[Fact]
	public async Task Assign_persists_assignee_snapshot_and_returns_updated_response()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory(userId: "actor-1");
		using var client = factory.CreateClient();

		var id = Ulid.NewUlid().ToString();
		await factory.SeedAsync(
			new Case
			{
				SourceSystemId = Ulid.NewUlid().ToString(),
				CallbackUrl = "https://source-system.test/callback",
				Nation = "ENGLAND",
				OrgId = "test-org",
				Id = id,
				CaseType = CaseType,
				StateId = "submitted",
				SubmittedBy = "test-client",
			},
			cancellationToken
		);

		var response = await client.PostAsJsonAsync(
			$"/cases/{id}/assign",
			new { assigneeId = "alice-1", assigneeName = "Alice Example" },
			cancellationToken
		);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await response.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken);
		body.Should().NotBeNull();
		var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);

		using var scope = new AssertionScope();
		body.Should().BeEquivalentTo(
			new
			{
				AssignedToId = "alice-1",
				AssignedToName = "Alice Example",
				AssignedBy = "actor-1",
			}
		);
		body!.AssignedAt.Should().NotBeNull();
		persisted.Should().BeEquivalentTo(new { AssignedToId = "alice-1", Version = 1 });
	}

	[Fact]
	public async Task Assign_records_the_actor_name_from_the_body_on_the_timeline()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory(userId: "actor-1");
		using var client = factory.CreateClient();
		var id = Ulid.NewUlid().ToString();
		await factory.SeedAsync(
			new Case
			{
				SourceSystemId = Ulid.NewUlid().ToString(),
				CallbackUrl = "https://source-system.test/callback",
				Nation = "ENGLAND",
				OrgId = "test-org",
				Id = id,
				CaseType = CaseType,
				StateId = "submitted",
				SubmittedBy = "test-client",
			},
			cancellationToken
		);

		// Act
		var response = await client.PostAsJsonAsync(
			$"/cases/{id}/assign",
			new { assigneeId = "alice-1", assigneeName = "Alice Example", actorName = "Gwŷn Caseworker" },
			cancellationToken
		);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
		persisted!.Timeline.Should().ContainSingle(e => e.Action == "assigned")
			.Which.Should().BeEquivalentTo(new { PerformedById = "actor-1", PerformedByName = "Gwŷn Caseworker" });
	}

	[Fact]
	public async Task Assign_returns_400_when_assigneeId_missing()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory(userId: "actor-1");
		using var client = factory.CreateClient();

		var id = Ulid.NewUlid().ToString();
		await factory.SeedAsync(
			new Case
			{
				SourceSystemId = Ulid.NewUlid().ToString(),
				CallbackUrl = "https://source-system.test/callback",
				Nation = "ENGLAND",
				OrgId = "test-org",
				Id = id,
				CaseType = CaseType,
				StateId = "submitted",
				SubmittedBy = "test-client",
			},
			cancellationToken
		);

		var response = await client.PostAsJsonAsync(
			$"/cases/{id}/assign",
			new { },
			cancellationToken
		);

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task Assign_returns_bad_request_when_body_is_not_a_json_object()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory(userId: "actor-1");
		using var client = factory.CreateClient();

		var id = Ulid.NewUlid().ToString();
		await factory.SeedAsync(
			new Case
			{
				SourceSystemId = Ulid.NewUlid().ToString(),
				CallbackUrl = "https://source-system.test/callback",
				Nation = "ENGLAND",
				OrgId = "test-org",
				Id = id,
				CaseType = CaseType,
				StateId = "submitted",
				SubmittedBy = "test-client",
			},
			cancellationToken
		);

		var content = new StringContent("[1,2,3]", System.Text.Encoding.UTF8, "application/json");
		var response = await client.PostAsync($"/cases/{id}/assign", content, cancellationToken);

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task Assign_rejects_a_non_string_assigneeName()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory(userId: "actor-1");
		using var client = factory.CreateClient();

		var id = Ulid.NewUlid().ToString();
		await factory.SeedAsync(
			new Case
			{
				SourceSystemId = Ulid.NewUlid().ToString(),
				CallbackUrl = "https://source-system.test/callback",
				Nation = "ENGLAND",
				OrgId = "test-org",
				Id = id,
				CaseType = CaseType,
				StateId = "submitted",
				SubmittedBy = "test-client",
			},
			cancellationToken
		);

		var response = await client.PostAsJsonAsync(
			$"/cases/{id}/assign",
			new { assigneeId = "alice-1", assigneeName = 42 },
			cancellationToken
		);

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task Assign_succeeds_when_standard_user_assigns_to_someone_else()
	{
		// RA-323: every caseworker holds the same role.
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory(userId: "alice-1");
		using var client = factory.CreateClient();

		var id = Ulid.NewUlid().ToString();
		await factory.SeedAsync(
			new Case
			{
				SourceSystemId = Ulid.NewUlid().ToString(),
				CallbackUrl = "https://source-system.test/callback",
				Nation = "ENGLAND",
				OrgId = "test-org",
				Id = id,
				CaseType = CaseType,
				StateId = "submitted",
				SubmittedBy = "test-client",
			},
			cancellationToken
		);

		var response = await client.PostAsJsonAsync(
			$"/cases/{id}/assign",
			new { assigneeId = "bob-1", assigneeName = "Bob" },
			cancellationToken
		);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task Assign_returns_404_when_case_missing()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory(userId: "actor-1");
		using var client = factory.CreateClient();

		var response = await client.PostAsJsonAsync(
			$"/cases/{Ulid.NewUlid().ToString()}/assign",
			new { assigneeId = "alice-1", assigneeName = "Alice" },
			cancellationToken
		);

		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task Unassign_clears_assignment_when_actor_has_assign_role()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory(userId: "actor-1");
		using var client = factory.CreateClient();

		var id = Ulid.NewUlid().ToString();
		await factory.SeedAsync(
			new Case
			{
				SourceSystemId = Ulid.NewUlid().ToString(),
				CallbackUrl = "https://source-system.test/callback",
				Nation = "ENGLAND",
				OrgId = "test-org",
				Id = id,
				CaseType = CaseType,
				StateId = "submitted",
				SubmittedBy = "test-client",
				AssignedToId = "alice-1",
				AssignedToName = "Alice",
				AssignedAt = new DateTime(2026, 4, 27, 9, 0, 0, DateTimeKind.Utc),
				AssignedBy = "earlier-actor",
			},
			cancellationToken
		);

		var response = await client.PostAsync(
			$"/cases/{id}/unassign",
			content: null,
			cancellationToken
		);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await response.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken);
		body.Should().BeEquivalentTo(
			new
			{
				AssignedToId = (string?)null,
				AssignedToName = (string?)null,
				AssignedAt = (DateTime?)null,
				AssignedBy = (string?)null,
			}
		);
	}

	[Fact]
	public async Task Unassign_records_the_actor_name_from_the_body_on_the_timeline()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory(userId: "actor-1");
		using var client = factory.CreateClient();
		var id = Ulid.NewUlid().ToString();
		await factory.SeedAsync(
			new Case
			{
				SourceSystemId = Ulid.NewUlid().ToString(),
				CallbackUrl = "https://source-system.test/callback",
				Nation = "ENGLAND",
				OrgId = "test-org",
				Id = id,
				CaseType = CaseType,
				StateId = "submitted",
				SubmittedBy = "test-client",
				AssignedToId = "alice-1",
				AssignedToName = "Alice",
			},
			cancellationToken
		);

		// Act
		var response = await client.PostAsJsonAsync(
			$"/cases/{id}/unassign",
			new { actorName = "Gwŷn Caseworker" },
			cancellationToken
		);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
		persisted!.Timeline.Should().ContainSingle(e => e.Action == "unassigned")
			.Which.Should().BeEquivalentTo(new { PerformedById = "actor-1", PerformedByName = "Gwŷn Caseworker" });
	}

	[Fact]
	public async Task Unassign_succeeds_for_caller_with_only_standard_role()
	{
		// RA-323: every caseworker holds the same role.
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory(userId: "alice-1");
		using var client = factory.CreateClient();

		var id = Ulid.NewUlid().ToString();
		await factory.SeedAsync(
			new Case
			{
				SourceSystemId = Ulid.NewUlid().ToString(),
				CallbackUrl = "https://source-system.test/callback",
				Nation = "ENGLAND",
				OrgId = "test-org",
				Id = id,
				CaseType = CaseType,
				StateId = "submitted",
				SubmittedBy = "test-client",
				AssignedToId = "alice-1",
			},
			cancellationToken
		);

		var response = await client.PostAsync(
			$"/cases/{id}/unassign",
			content: null,
			cancellationToken
		);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task Assign_to_same_user_sets_idempotent_replay_header_and_does_not_persist()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory(userId: "actor-1");
		using var client = factory.CreateClient();

		var id = Ulid.NewUlid().ToString();
		await factory.SeedAsync(
			new Case
			{
				SourceSystemId = Ulid.NewUlid().ToString(),
				CallbackUrl = "https://source-system.test/callback",
				Nation = "ENGLAND",
				OrgId = "test-org",
				Id = id,
				CaseType = CaseType,
				StateId = "submitted",
				SubmittedBy = "test-client",
				AssignedToId = "alice-1",
				AssignedToName = "Alice Example",
				AssignedAt = new DateTime(2026, 4, 27, 9, 0, 0, DateTimeKind.Utc),
				AssignedBy = "earlier-actor",
			},
			cancellationToken
		);

		var response = await client.PostAsJsonAsync(
			$"/cases/{id}/assign",
			new { assigneeId = "alice-1", assigneeName = "Alice Example" },
			cancellationToken
		);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		response.Headers.TryGetValues("X-Idempotent-Replay", out var values).Should().BeTrue();
		var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);

		using var scope = new AssertionScope();
		values.Should().Equal("true");
		persisted.Should().BeEquivalentTo(new { Version = 0 });
	}

	[Fact]
	public async Task Unassign_already_unassigned_sets_idempotent_replay_header_and_does_not_persist()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory(userId: "actor-1");
		using var client = factory.CreateClient();

		var id = Ulid.NewUlid().ToString();
		await factory.SeedAsync(
			new Case
			{
				SourceSystemId = Ulid.NewUlid().ToString(),
				CallbackUrl = "https://source-system.test/callback",
				Nation = "ENGLAND",
				OrgId = "test-org",
				Id = id,
				CaseType = CaseType,
				StateId = "submitted",
				SubmittedBy = "test-client",
			},
			cancellationToken
		);

		var response = await client.PostAsync(
			$"/cases/{id}/unassign",
			content: null,
			cancellationToken
		);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		response.Headers.TryGetValues("X-Idempotent-Replay", out var values).Should().BeTrue();
		var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);

		using var scope = new AssertionScope();
		values.Should().Equal("true");
		persisted.Should().BeEquivalentTo(new { Version = 0 });
	}

	// ---- RA-358: assignment endpoints refuse a closed (terminal) case ----

	/// <summary>
	/// Every terminal state the case management types declare. A hand-crafted
	/// POST to a closed case must be refused by the API itself, not only by
	/// the front end hiding the button.
	/// </summary>
	private static readonly CaseState[] TerminalStateMachine =
	[
		new("submitted", "Submitted"),
		new("withdrawn", "Withdrawn", IsTerminal: true),
		new("approved", "Approved", IsTerminal: true),
		new("rejected", "Rejected", IsTerminal: true),
	];

	[Theory]
	[InlineData("withdrawn")]
	[InlineData("approved")]
	[InlineData("rejected")]
	public async Task Assign_returns_409_problem_when_case_is_terminal(string stateId)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory(userId: "actor-1", states: TerminalStateMachine);
		using var client = factory.CreateClient();

		var id = Ulid.NewUlid().ToString();
		await factory.SeedAsync(
			new Case
			{
				SourceSystemId = Ulid.NewUlid().ToString(),
				CallbackUrl = "https://source-system.test/callback",
				Nation = "ENGLAND",
				OrgId = "test-org",
				Id = id,
				CaseType = CaseType,
				StateId = stateId,
				SubmittedBy = "test-client",
			},
			cancellationToken
		);

		var response = await client.PostAsJsonAsync(
			$"/cases/{id}/assign",
			new { assigneeId = "alice-1", assigneeName = "Alice Example" },
			cancellationToken
		);

		response.StatusCode.Should().Be(HttpStatusCode.Conflict);
		var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
		problem.Should().NotBeNull();
		var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);

		using var scope = new AssertionScope();
		response.Headers.Contains("X-Idempotent-Replay").Should().BeFalse();
		problem.Should().BeEquivalentTo(new { Title = "Action not allowed", Status = 409 });
		problem!.Detail.Should().Contain(stateId).And.NotContain(id.ToString());
		persisted.Should().BeEquivalentTo(
			new
			{
				AssignedToId = (string?)null,
				Version = 0,
				Timeline = Array.Empty<CaseTimelineEntry>(),
			}
		);
	}

	[Theory]
	[InlineData("withdrawn")]
	[InlineData("approved")]
	[InlineData("rejected")]
	public async Task Unassign_returns_409_problem_when_case_is_terminal(string stateId)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory(userId: "actor-1", states: TerminalStateMachine);
		using var client = factory.CreateClient();

		var id = Ulid.NewUlid().ToString();
		await factory.SeedAsync(
			new Case
			{
				SourceSystemId = Ulid.NewUlid().ToString(),
				CallbackUrl = "https://source-system.test/callback",
				Nation = "ENGLAND",
				OrgId = "test-org",
				Id = id,
				CaseType = CaseType,
				StateId = stateId,
				SubmittedBy = "test-client",
				AssignedToId = "alice-1",
				AssignedToName = "Alice Example",
				AssignedAt = new DateTime(2026, 4, 27, 9, 0, 0, DateTimeKind.Utc),
				AssignedBy = "earlier-actor",
			},
			cancellationToken
		);

		var response = await client.PostAsync(
			$"/cases/{id}/unassign",
			content: null,
			cancellationToken
		);

		response.StatusCode.Should().Be(HttpStatusCode.Conflict);
		var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
		problem.Should().NotBeNull();
		var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);

		using var scope = new AssertionScope();
		response.Headers.Contains("X-Idempotent-Replay").Should().BeFalse();
		problem.Should().BeEquivalentTo(new { Title = "Action not allowed", Status = 409 });
		problem!.Detail.Should().NotContain(id.ToString());
		persisted.Should().BeEquivalentTo(
			new
			{
				AssignedToId = "alice-1",
				Version = 0,
				Timeline = Array.Empty<CaseTimelineEntry>(),
			}
		);
	}

	[Fact]
	public async Task Assign_still_succeeds_when_case_is_not_in_a_terminal_state()
	{
		// Regression guard for the RA-358 terminal check: the normal
		// assignment path through the same state machine is untouched.
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory(userId: "actor-1", states: TerminalStateMachine);
		using var client = factory.CreateClient();

		var id = Ulid.NewUlid().ToString();
		await factory.SeedAsync(
			new Case
			{
				SourceSystemId = Ulid.NewUlid().ToString(),
				CallbackUrl = "https://source-system.test/callback",
				Nation = "ENGLAND",
				OrgId = "test-org",
				Id = id,
				CaseType = CaseType,
				StateId = "submitted",
				SubmittedBy = "test-client",
			},
			cancellationToken
		);

		var assign = await client.PostAsJsonAsync(
			$"/cases/{id}/assign",
			new { assigneeId = "alice-1", assigneeName = "Alice Example" },
			cancellationToken
		);
		assign.StatusCode.Should().Be(HttpStatusCode.OK);

		var unassign = await client.PostAsync(
			$"/cases/{id}/unassign",
			content: null,
			cancellationToken
		);
		unassign.StatusCode.Should().Be(HttpStatusCode.OK);

		var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
		persisted!.AssignedToId.Should().BeNull();
	}

	[Fact]
	public async Task AddNote_returns_bad_request_when_body_is_not_a_json_object()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory(userId: "alice-1");
		using var client = factory.CreateClient();

		var id = Ulid.NewUlid().ToString();
		await factory.SeedAsync(
			new Case
			{
				SourceSystemId = Ulid.NewUlid().ToString(),
				CallbackUrl = "https://source-system.test/callback",
				Nation = "ENGLAND",
				OrgId = "test-org",
				Id = id,
				CaseType = CaseType,
				StateId = "submitted",
				SubmittedBy = "test-client",
			},
			cancellationToken
		);

		var content = new StringContent("[1,2,3]", System.Text.Encoding.UTF8, "application/json");
		var response = await client.PostAsync($"/cases/{id}/notes", content, cancellationToken);

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task AddNote_persists_note_and_returns_updated_response()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory(userId: "alice-1");
		using var client = factory.CreateClient();

		var id = Ulid.NewUlid().ToString();
		await factory.SeedAsync(
			new Case
			{
				SourceSystemId = Ulid.NewUlid().ToString(),
				CallbackUrl = "https://source-system.test/callback",
				Nation = "ENGLAND",
				OrgId = "test-org",
				Id = id,
				CaseType = CaseType,
				StateId = "submitted",
				SubmittedBy = "test-client",
			},
			cancellationToken
		);

		var response = await client.PostAsJsonAsync(
			$"/cases/{id}/notes",
			new { text = "Reviewed evidence; awaiting confirmation.", actorName = "Alice Example" },
			cancellationToken
		);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await response.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken);
		body!.Timeline.Should().NotBeNull();
		var note = body.Timeline!.Should().ContainSingle(a => a.Action == "note-added").Subject;
		var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
		persisted.Should().NotBeNull();

		using var scope = new AssertionScope();
		note.Should().BeEquivalentTo(new { PerformedById = "alice-1", PerformedByName = "Alice Example" });
		note.Details.Should().Contain("text", "Reviewed evidence; awaiting confirmation.");
		persisted!.Timeline.Should().ContainSingle(a => a.Action == "note-added");
		persisted.Version.Should().Be(1);
	}

	[Fact]
	public async Task AddNote_returns_400_when_text_missing()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();

		var id = Ulid.NewUlid().ToString();
		await factory.SeedAsync(
			new Case
			{
				SourceSystemId = Ulid.NewUlid().ToString(),
				CallbackUrl = "https://source-system.test/callback",
				Nation = "ENGLAND",
				OrgId = "test-org",
				Id = id,
				CaseType = CaseType,
				StateId = "submitted",
				SubmittedBy = "test-client",
			},
			cancellationToken
		);

		var response = await client.PostAsJsonAsync(
			$"/cases/{id}/notes",
			new { text = "   " },
			cancellationToken
		);

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task AddNote_returns_404_when_case_missing()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();

		var response = await client.PostAsJsonAsync(
			$"/cases/{Ulid.NewUlid().ToString()}/notes",
			new { text = "anything" },
			cancellationToken
		);

		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task AddNote_returns_unauthorized_without_client_id()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory(includeAuthHeader: false);
		using var client = factory.CreateClient();

		var response = await client.PostAsJsonAsync(
			$"/cases/{Ulid.NewUlid().ToString()}/notes",
			new { text = "anything" },
			cancellationToken
		);

		response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task Timeline_is_projected_newest_first_on_the_wire()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();

		var id = Ulid.NewUlid().ToString();
		var older = new DateTime(2026, 4, 27, 9, 0, 0, DateTimeKind.Utc);
		var newer = new DateTime(2026, 4, 27, 11, 0, 0, DateTimeKind.Utc);
		await factory.SeedAsync(
			new Case
			{
				SourceSystemId = Ulid.NewUlid().ToString(),
				CallbackUrl = "https://source-system.test/callback",
				Nation = "ENGLAND",
				OrgId = "test-org",
				Id = id,
				CaseType = CaseType,
				StateId = "submitted",
				SubmittedBy = "test-client",
				Timeline =
				{
                    // Insert out of order on purpose so we know the newest-first
                    // ordering on the wire is enforced by the projection rather
                    // than by storage order.
                    new CaseTimelineEntry
					{
						Action = "note-added",
						ActionDisplayName = "Note added",
						PerformedAt = newer,
						PerformedById = "alice-1",
						PerformedByName = "Alice",
					},
					new CaseTimelineEntry
					{
						Action = "task-completed",
						ActionDisplayName = "Task completed",
						Details = new() { ["taskId"] = "check-eligibility" },
						PerformedAt = older,
						PerformedById = "bob-1",
						PerformedByName = "Bob",
					},
				},
			},
			cancellationToken
		);

		var response = await client.GetAsync($"/cases/{id}", cancellationToken);
		response.StatusCode.Should().Be(HttpStatusCode.OK);

		var body = await response.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken);
		body!.Timeline.Should().NotBeNull();
		body.Timeline!.Should().SatisfyRespectively(
			first => first.Should().BeEquivalentTo(new { Action = "note-added", PerformedAt = newer }),
			second =>
			{
				second.Should().BeEquivalentTo(
					new
					{
						Action = "task-completed",
						ActionDisplayName = "Task completed",
						PerformedAt = older,
					}
				);
				second.Details.Should().Contain("taskId", "check-eligibility");
			}
		);
	}

	[Fact]
	public async Task Timeline_preserves_insertion_order_for_entries_with_identical_timestamps()
	{
		// Regression: when two entries share an exact PerformedAt
		// (common under FakeTimeProvider with the clock held still, and
		// possible in production when a single engine call appends two
		// entries back-to-back) the projection must keep their stored
		// insertion order reversed (newest-first) rather than fall back to
		// undefined behaviour from a tied OrderBy (RA-568).
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory();
		using var client = factory.CreateClient();

		var id = Ulid.NewUlid().ToString();
		var t = new DateTime(2026, 4, 30, 9, 0, 0, DateTimeKind.Utc);
		await factory.SeedAsync(
			new Case
			{
				SourceSystemId = Ulid.NewUlid().ToString(),
				CallbackUrl = "https://source-system.test/callback",
				Nation = "ENGLAND",
				OrgId = "test-org",
				Id = id,
				CaseType = CaseType,
				StateId = "submitted",
				SubmittedBy = "test-client",
				Timeline =
				{
					new CaseTimelineEntry
					{
						Action = "note-added",
						ActionDisplayName = "Note added",
						Details = new() { ["sequence"] = "1" },
						PerformedAt = t,
					},
					new CaseTimelineEntry
					{
						Action = "task-completed",
						ActionDisplayName = "Task completed",
						Details = new() { ["sequence"] = "2" },
						PerformedAt = t,
					},
					new CaseTimelineEntry
					{
						Action = "state-changed",
						ActionDisplayName = "Action applied",
						Details = new() { ["sequence"] = "3" },
						PerformedAt = t,
					},
				},
			},
			cancellationToken
		);

		var response = await client.GetAsync($"/cases/{id}", cancellationToken);
		response.StatusCode.Should().Be(HttpStatusCode.OK);

		var body = await response.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken);
		body!.Timeline.Should().NotBeNull();
		body.Timeline!.Select(e => e.Details["sequence"]).Should().Equal("3", "2", "1");
	}

	// ----------------------------------------------------------------
	// ADR-0005: mutation endpoints no longer gate on who submitted the
	// item — every mutation handler must reach the engine regardless of
	// submitter, since authorization is the frontend's responsibility now.
	// ----------------------------------------------------------------

	public static IEnumerable<TheoryDataRow<string, string, object?>> MutationCases() =>
		new TheoryDataRow<string, string, object?>[]
		{
			new("POST", "/cases/{id}/actions/approve", null)
			{
				TestDisplayName = "ApplyAction",
			},
			new(
				"POST",
				"/cases/{id}/assign",
				new { assigneeId = "alice-1", assigneeName = "Alice" }
			)
			{
				TestDisplayName = "Assign",
			},
			new("POST", "/cases/{id}/unassign", null) { TestDisplayName = "Unassign" },
			new("POST", "/cases/{id}/notes", new { text = "anything" })
			{
				TestDisplayName = "AddNote",
			},
		};

	[Theory]
	[MemberData(nameof(MutationCases))]
	public async Task Mutation_operates_on_item_regardless_of_submitter(
		string method,
		string pathTemplate,
		object? body
	)
	{
		// RBAC lives in the frontend now: the backend performs whatever
		// mutation is asked for on any item any authenticated caller
		// targets, regardless of who submitted it.
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = NewFactory(userId: "actor-1");
		using var client = factory.CreateClient();

		var id = Ulid.NewUlid().ToString();
		await factory.SeedAsync(
			new Case
			{
				SourceSystemId = Ulid.NewUlid().ToString(),
				CallbackUrl = "https://source-system.test/callback",
				Nation = "ENGLAND",
				OrgId = "test-org",
				Id = id,
				CaseType = CaseType,
				StateId = "submitted",
				SubmittedBy = "other-tenant",
			},
			cancellationToken
		);

		var path = pathTemplate.Replace("{id}", id.ToString());
		HttpResponseMessage response = method switch
		{
			"POST" when body is null => await client.PostAsync(
				path,
				content: null,
				cancellationToken
			),
			"POST" => await client.PostAsJsonAsync(path, body, cancellationToken),
			"PUT" => await client.PutAsJsonAsync(path, body!, cancellationToken),
			_ => throw new InvalidOperationException($"Unsupported method '{method}'."),
		};

		// Anything but 404 means the request reached the engine — that's
		// the contract for this test (individual engine-level outcomes,
		// e.g. invalid transitions, are covered by other tests).
		response.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
	}

	/// <summary>
	/// Test factory backed by ephemeral MongoDB. <see cref="Persistence"/>
	/// is the real <see cref="ICasePersistence"/>; tests use it to
	/// seed and re-fetch documents instead of stubbing call results.
	/// <see cref="Recording"/> exposes the last <see cref="CaseQuery"/>
	/// passed through, for the few tests that need to assert how the
	/// endpoint built it.
	/// </summary>
	private sealed class TestApplicationFactory : WebApplicationFactory<Program>
	{
		private readonly MongoIntegrationFixture _fixture;
		private readonly string _databaseName = MongoIntegrationFixture.NewDatabaseName(
			"endpoints"
		);
		private readonly bool _includeAuthHeader;
		private readonly string? _userId;
		private readonly IReadOnlyCollection<CaseState>? _states;
		private readonly string? _nation;

		/// <summary>
		/// The client id allowed to create cases. Defaults to the client
		/// these tests call every endpoint as; set before the first request.
		/// </summary>
		public string IntakeClientId { get; set; } = "test-client";

		public TestApplicationFactory(
			MongoIntegrationFixture fixture,
			bool includeAuthHeader,
			string? userId,
			IReadOnlyCollection<CaseState>? states,
			string? nation
		)
		{
			_nation = nation;
			_fixture = fixture;
			_includeAuthHeader = includeAuthHeader;
			_userId = userId;
			_states = states;
		}

		public StubCallbackHandler Callbacks { get; } = new();

		public ICasePersistence Persistence => Recording.Inner;

		public RecordingPersistence Recording =>
			(RecordingPersistence)Services.GetRequiredService<ICasePersistence>();

		public Task SeedAsync(Case item, CancellationToken cancellationToken) =>
			Services
				.GetRequiredService<IMongoDbClientFactory>()
				.GetCollection<Case>("cases")
				.InsertOneAsync(item, cancellationToken: cancellationToken);

		public async Task<List<Case>> AllItemsAsync(CancellationToken cancellationToken)
		{
			var page = await Persistence.QueryAsync(
				new CaseQuery(Page: 1, PageSize: 100),
				cancellationToken
			);
			return page.Items.ToList();
		}

		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			builder.UseSetting(CaseIntakePolicy.ClientIdKey, IntakeClientId);
			builder.UseSetting("CALLBACK_AUTH:CSoC", "case-management:callback-secret");

			builder.ConfigureServices(services =>
			{
				services.RemoveAll<ICasePersistence>();
				services.RemoveAll<WasteRegulatorCaseManagement.Utils.Mongo.IMongoDbClientFactory>();
				var clientFactory = new TestMongoDbClientFactory(
					_fixture.ConnectionString,
					_databaseName
				);
				services.AddSingleton<WasteRegulatorCaseManagement.Utils.Mongo.IMongoDbClientFactory>(
					clientFactory
				);
				// The real CasePersistence is wrapped with a recording
				// layer so query-capture tests can observe the
				// CaseQuery the endpoint built. Behaviour is
				// otherwise end-to-end against ephemeral Mongo.
				services.AddSingleton<ICasePersistence>(sp => new RecordingPersistence(
					new CasePersistence(
						clientFactory,
						sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>(),
						sp.GetRequiredService<CaseRegistry>(), new AuditEventService(clientFactory, TimeProvider.System))
				));
				services
					.AddHttpClient(CaseCallbackClient.HttpClientName)
					.ConfigurePrimaryHttpMessageHandler(() => Callbacks);
				services.UseCaseModuleConfigs(
					TestCaseType.Create(
						CaseType,
						"Test type",
						states: _states
					)
				);
			});
		}

		protected override void ConfigureClient(HttpClient client)
		{
			base.ConfigureClient(client);
			if (_includeAuthHeader)
			{
				client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, "test-client");
			}
			if (_userId is not null)
			{
				client.DefaultRequestHeaders.Add("x-cdp-user-id", _userId);
			}
			if (_nation is not null)
			{
				client.DefaultRequestHeaders.Add("x-cdp-user-nation", _nation);
			}
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				try
				{
					var clientFactory =
						Services.GetRequiredService<WasteRegulatorCaseManagement.Utils.Mongo.IMongoDbClientFactory>();
					clientFactory.GetClient().DropDatabase(_databaseName);
				}
				catch
				{
					// Best-effort cleanup; ephemeral instance dies with the
					// fixture anyway.
				}
			}
			base.Dispose(disposing);
		}
	}

	/// <summary>
	/// Pass-through wrapper around <see cref="ICasePersistence"/>
	/// that records the last <see cref="CaseQuery"/> the endpoint
	/// built. Behaviour is otherwise identical to the wrapped
	/// persistence — tests still go end-to-end against ephemeral Mongo.
	/// </summary>
	public sealed class RecordingPersistence(ICasePersistence inner) : ICasePersistence
	{
		public ICasePersistence Inner { get; } = inner;
		public CaseQuery? LastQuery { get; private set; }

		public Task CreateAsync(Case caseRecord, CancellationToken cancellationToken = default) =>
			Inner.CreateAsync(caseRecord, cancellationToken);

		public Task<Case?> GetByIdAsync(
			string id,
			CancellationToken cancellationToken = default
		) => Inner.GetByIdAsync(id, cancellationToken);

		public Task<Case?> FindBySourceSystemIdAsync(
			string sourceSystemId,
			CancellationToken cancellationToken = default
		) => Inner.FindBySourceSystemIdAsync(sourceSystemId, cancellationToken);

		public Task<CasePage> QueryAsync(
			CaseQuery query,
			CancellationToken cancellationToken = default
		)
		{
			LastQuery = query;
			return Inner.QueryAsync(query, cancellationToken);
		}

		public Task ReplaceAsync(
			Case caseRecord,
			CancellationToken cancellationToken = default
		) => Inner.ReplaceAsync(caseRecord, cancellationToken);
	}
}
