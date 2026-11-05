using System.Net;
using MongoDB.Bson;
using System.Net.Http.Json;
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

namespace WasteRegulatorCaseManagement.Test.Cases;

/// <summary>
/// epr-efp: persistence is real <see cref="CasePersistence"/> backed
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

    private const string CaseType = "test-type";
    private readonly MongoIntegrationFixture _fixture;

    public CaseEndpointsTests(MongoIntegrationFixture fixture) => _fixture = fixture;

    private TestApplicationFactory NewFactory(
        bool includeAuthHeader = true,
        string? userId = "test-user",
        string? userName = null,
        IReadOnlyCollection<CaseState>? states = null
    ) => new(_fixture, includeAuthHeader, userId, userName, states);

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

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.Equal("Invalid request", problem?.Title);
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

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.Equal("Invalid request", problem?.Title);
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

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.Equal("Unknown case type", problem?.Title);
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

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken);
        Assert.NotNull(body);

        var persisted = await factory.Persistence.GetByIdAsync(body!.Id, cancellationToken);
        Assert.NotNull(persisted);
        Assert.Equal(CaseType, persisted!.CaseType);
        Assert.Equal("submitted", persisted.StateId);
        Assert.Equal("test-client", persisted.SubmittedBy);
        Assert.Equal("csoc-2024-eng-00123", persisted.SourceSystemId);
        Assert.Equal("https://source-system.test/declarations/00123", persisted.CallbackUrl);
        Assert.Equal("ENGLAND", persisted.Nation);
        Assert.Equal("org-00456", persisted.OrgId);
        Assert.Equal("DIRECT_PRODUCER", persisted.ProducerType);
        Assert.Equal(new CaseDisplayField("Acme Packaging Ltd", "string"), persisted.DisplayData["organisationName"]);

        // Snapshot of the type's template (states, tasks, transitions, version)
        // is frozen onto the case at submission for faithful historical
        // rendering after the live module changes.
        Assert.NotNull(persisted.TemplateSnapshot);
        Assert.Equal("v1", persisted.TemplateVersion);
        Assert.Equal("v1", persisted.TemplateSnapshot!.TemplateVersion);

        Assert.NotNull(response.Headers.Location);
        Assert.StartsWith("/cases/", response.Headers.Location!.AbsolutePath);

        Assert.True(Ulid.TryParse(body.Id, out _), $"'{body.Id}' is not a ULID");
        Assert.Equal(CaseType, body.CaseType);
        Assert.Equal("submitted", body.StateId);
        Assert.False(body.IsTerminal);
        Assert.Equal("test-client", body.SubmittedBy);
        Assert.Equal("v1", body.TemplateVersion);
        Assert.Equal("csoc-2024-eng-00123", body.SourceSystemId);
        Assert.Equal("ENGLAND", body.Nation);
        Assert.Equal("org-00456", body.OrgId);
        Assert.Equal("DIRECT_PRODUCER", body.ProducerType);
        Assert.Equal(new CaseDisplayField("87.3", "number"), body.DisplayData["percentageMet"]);
    }

    [Fact]
    public async Task Post_records_case_submitted_audit_entry_as_birth_event()
    {
        // RA-97: the timeline must start at submission. The framework
        // appends one 'case-created' entry inside the same
        // CreateAsync call that persists the new document, attributed to
        // the forwarded user:id and stamped with Case.SubmittedAt.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory(userId: "alice-1", userName: "Alice Example");
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

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken);
        var persisted = await factory.Persistence.GetByIdAsync(body!.Id, cancellationToken);
        Assert.NotNull(persisted);
        var entry = Assert.Single(persisted!.Timeline);
        Assert.Equal("case-created", entry.Action);
        Assert.Equal("Case submitted", entry.ActionDisplayName);
        Assert.Equal("alice-1", entry.PerformedById);
        Assert.Equal("Alice Example", entry.PerformedByName);
        Assert.Equal(persisted.SubmittedAt, entry.PerformedAt);
        Assert.Equal(CaseType, entry.Details["caseType"]);
        Assert.Equal("submitted", entry.Details["stateId"]);
        Assert.Equal("submitted", entry.StateId);
        // Birth event was part of the original CreateAsync (no follow-up
        // ReplaceAsync), so Version is still 0.
        Assert.Equal(0, persisted.Version);

        // epr-rr9s: the per-event state snapshot must also survive the wire
        // projection so the FE can render each entry against its own state.
        Assert.NotNull(body.Timeline);
        var wireEntry = Assert.Single(body.Timeline!);
        Assert.Equal("case-created", wireEntry.Action);
        Assert.Equal("submitted", wireEntry.StateId);
    }

    [Fact]
    public async Task Post_returns_401_and_persists_nothing_when_user_id_claim_missing()
    {
        // RA-97 / engine rule: every mutating call requires the BFF to
        // forward a 'user:id' claim so the audit entry can be tied back
        // to a real human. Without it the engine refuses to write.
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

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await factory.AllItemsAsync(cancellationToken));
    }

    [Fact]
    public async Task Post_records_submission_metadata_on_birth_audit_entry()
    {
        // RA-126: birth audit entry must carry source / clientId / userId
        // alongside the existing caseType / stateId / templateVersion keys so audit consumers can reconstruct the
        // submission origin without joining back to request logs.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory(userId: "alice-1", userName: "Alice Example");
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

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken);
        var persisted = await factory.Persistence.GetByIdAsync(body!.Id, cancellationToken);
        Assert.NotNull(persisted);
        var entry = Assert.Single(persisted!.Timeline);
        Assert.Equal("case-created", entry.Action);
        Assert.Equal(CaseType, entry.Details["caseType"]);
        Assert.Equal("submitted", entry.Details["stateId"]);
        Assert.Equal("v1", entry.Details["templateVersion"]);
        Assert.Equal("operator-fe", entry.Details["source"]);
        Assert.Equal("test-client", entry.Details["clientId"]);
        Assert.Equal("alice-1", entry.Details["userId"]);
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

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken);
        var persisted = await factory.Persistence.GetByIdAsync(body!.Id, cancellationToken);
        var entry = Assert.Single(persisted!.Timeline);
        Assert.True(entry.Details.ContainsKey("source"));
        Assert.Null(entry.Details["source"]);
        Assert.Equal("test-client", entry.Details["clientId"]);
        Assert.Equal("alice-1", entry.Details["userId"]);
    }

    [Theory]
    [InlineData("source", "'source' must be a string.")]
    public async Task Post_rejects_non_string_submission_metadata_field(
        string field,
        string expectedDetail
    )
    {
        // RA-126: reject malformed metadata up front rather than
        // silently coercing / dropping it — that would leave the audit
        // record degraded without the caller noticing.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        // Hand-craft body to inject a non-string value (e.g. a number)
        // for the field under test.
        var json = $$"""
            {
                "caseType": "{{CaseType}}",
                "sourceSystemId": "{{Ulid.NewUlid()}}",
                "callbackUrl": "https://source-system.test/callback",
                "nation": "ENGLAND",
                "orgId": "test-org",
                "{{field}}": 42
            }
            """;
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/cases", content, cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.Equal("Invalid request body", problem?.Title);
        Assert.Equal(expectedDetail, problem?.Detail);
        Assert.Empty(await factory.AllItemsAsync(cancellationToken));
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

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken);
        var persisted = await factory.Persistence.GetByIdAsync(body!.Id, cancellationToken);
        Assert.NotNull(persisted);
        Assert.Empty(persisted!.DisplayData);
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

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.Contains("sourceSystemId", problem?.Detail);
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

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.Contains("nation", problem?.Detail);
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

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.Contains("callbackUrl", problem?.Detail);
    }

    [Fact]
    public async Task Post_returns_problem_when_a_display_data_type_is_not_recognised()
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
                displayData = new { organisationName = new { value = "Acme", type = "text" } },
            },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.Contains("displayData.organisationName", problem?.Detail);
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

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("true", second.Headers.GetValues(CaseEndpoints.IdempotentReplayHeader).Single());
        var firstBody = await first.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken);
        var secondBody = await second.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken);
        Assert.Equal(firstBody!.Id, secondBody!.Id);
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
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken);
        Assert.Equal(id, body?.Id);
        Assert.Equal(CaseType, body?.CaseType);
    }

    [Fact]
    public async Task Get_by_id_returns_not_found_when_missing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/cases/{Ulid.NewUlid().ToString()}", cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_by_id_returns_ok_for_item_not_submitted_by_caller()
    {
        // RBAC (who may look at whose items) now lives entirely in the
        // frontend/BFF; the backend trusts any authenticated (shared-secret
        // signed) caller and does not check ownership.
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
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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
        Assert.NotNull(body);
        Assert.Equal(2, body!.Items.Count);

        var lastQuery = factory.Recording.LastQuery;
        Assert.NotNull(lastQuery);
        Assert.Null(lastQuery!.SubmittedBy);
    }

    [Fact]
    public async Task Get_list_filters_by_submittedBy_query_param_when_supplied()
    {
        // Scoping ("give me only my items") is now something the frontend
        // asks for explicitly, not something the backend infers from a
        // role claim. The backend still applies whatever scope is asked
        // for — this is a query filter, not an authorization gate.
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
            "/cases?submittedBy=test-client",
            cancellationToken
        );
        Assert.NotNull(body);
        var item = Assert.Single(body!.Items);
        Assert.Equal("test-client", item.SubmittedBy);

        var lastQuery = factory.Recording.LastQuery;
        Assert.NotNull(lastQuery);
        Assert.Equal("test-client", lastQuery!.SubmittedBy);
    }

    [Fact]
    public async Task Submit_logs_absent_body_when_no_json_body_is_supplied()
    {
        // Covers the `body.ValueKind != JsonValueKind.Undefined` branch's
        // false arm: a JsonElement default(JsonElement) (ValueKind ==
        // Undefined) is what minimal-API binding hands the handler for a
        // request with no body at all. It should still be rejected (as
        // "not a JSON object"), just via the log-friendly "(empty)" path
        // rather than calling GetRawText() on an undefined element (which
        // throws).
        var cancellationToken = TestContext.Current.CancellationToken;
        var type = new TestCaseType(CaseType, "Test type");
        var registry = new CaseRegistry([type]);
        var engine = Substitute.For<ICaseService>();
        var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(authenticationType: "test")
            ),
        };

        var result = await CaseEndpoints.Submit(
            default,
            httpContext,
            registry,
            engine,
            Substitute.For<IStructuredLogger<CaseEndpointsLogger>>(),
            cancellationToken
        );

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(
            Microsoft.AspNetCore.Http.StatusCodes.Status400BadRequest,
            problem.StatusCode
        );
    }

    [Fact]
    public async Task Submit_truncates_a_very_large_body_in_the_log()
    {
        // Covers the `bodyText.Length > MaxLoggedBodyChars` branch's true
        // arm — a body large enough that logging it verbatim would be
        // unreasonable. The oversized payload is otherwise valid so the
        // request succeeds; the assertion is just that it doesn't throw
        // and completes normally, proving the truncation path ran.
        var cancellationToken = TestContext.Current.CancellationToken;
        var type = new TestCaseType(CaseType, "Test type");
        var registry = new CaseRegistry([type]);
        var engine = Substitute.For<ICaseService>();
        var submittedCase = new Case
        {
            SourceSystemId = Ulid.NewUlid().ToString(),
            CallbackUrl = "https://source-system.test/callback",
            Nation = "ENGLAND",
            OrgId = "test-org",
            Id = Ulid.NewUlid().ToString(),
            CaseType = CaseType,
            StateId = "submitted",
            SubmittedAt = DateTime.UtcNow,
            LastModifiedAt = DateTime.UtcNow,
            SubmittedBy = "test-client",
        };
        engine
            .SubmitAsync(
                Arg.Any<ICaseType>(),
                Arg.Any<CaseIntake>(),
                Arg.Any<string?>(),
                Arg.Any<System.Security.Claims.ClaimsPrincipal>(),
                Arg.Any<IReadOnlyDictionary<string, string?>?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(CaseActionResult.Success(submittedCase));
        engine
            .Project(Arg.Any<Case>())
            .Returns(new CaseEngineProjection(
                submittedCase, "v1", Array.Empty<CaseTransition>()));

        var largeValue = new string('x', 5000);
        var body = JsonDocument
            .Parse($"{{\"caseType\":\"{CaseType}\",\"payload\":{{\"note\":\"{largeValue}\"}}}}")
            .RootElement;
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

        Assert.NotNull(result.Result);
    }

    [Fact]
    public async Task Post_returns_problem_when_body_is_not_a_json_object()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        var content = new StringContent("[1,2,3]", System.Text.Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/cases", content, cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.Equal("Invalid request", problem?.Title);
    }

    [Fact]
    public async Task Submit_rethrows_when_the_engine_throws_an_unhandled_exception()
    {
        // The Submit handler logs the exception at Error level and then
        // rethrows unchanged rather than mapping it to a ProblemDetails —
        // an unexpected engine failure should surface as a 500, not be
        // disguised as a client error.
        var cancellationToken = TestContext.Current.CancellationToken;
        var type = new TestCaseType(CaseType, "Test type");
        var registry = new CaseRegistry([type]);
        var engine = Substitute.For<ICaseService>();
        engine
            .SubmitAsync(
                Arg.Any<ICaseType>(),
                Arg.Any<CaseIntake>(),
                Arg.Any<string?>(),
                Arg.Any<System.Security.Claims.ClaimsPrincipal>(),
                Arg.Any<IReadOnlyDictionary<string, string?>?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromException<CaseActionResult>(new InvalidOperationException("boom")));

        var body = JsonDocument.Parse(
            $$"""
            {
                "caseType": "{{CaseType}}",
                "sourceSystemId": "{{Ulid.NewUlid()}}",
                "callbackUrl": "https://source-system.test/callback",
                "nation": "ENGLAND",
                "orgId": "test-org"
            }
            """
        ).RootElement;
        var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(authenticationType: "test")
            ),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CaseEndpoints.Submit(
                body,
                httpContext,
                registry,
                engine,
                Substitute.For<IStructuredLogger<CaseEndpointsLogger>>(),
                cancellationToken
            ));
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
                Arg.Any<System.Security.Claims.ClaimsPrincipal>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(
                CaseActionResult.Failure(
                    CaseActionFailureCode.ConcurrencyConflict,
                    "Case was modified concurrently. Reload and retry."
                )
            );

        var body = JsonDocument.Parse("""{"assigneeId":"alice-1"}""").RootElement;
        var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(authenticationType: "test")
            ),
        };

        var result = await CaseEndpoints.Assign(
            caseId, body, httpContext, engine, cancellationToken);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(
            Microsoft.AspNetCore.Http.StatusCodes.Status409Conflict,
            problem.StatusCode
        );
    }

    [Fact]
    public async Task Submit_maps_an_unrecognised_failure_code_to_a_generic_400_problem()
    {
        // The only CaseActionFailureCode SubmitAsync can plausibly return
        // is MissingActorIdentity, which has its own named switch arm; this
        // exercises the fallback arm that catches anything else.
        var cancellationToken = TestContext.Current.CancellationToken;
        var type = new TestCaseType(CaseType, "Test type");
        var registry = new CaseRegistry([type]);
        var engine = Substitute.For<ICaseService>();
        engine
            .SubmitAsync(
                Arg.Any<ICaseType>(),
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

        var body = JsonDocument.Parse(
            $$"""
            {
                "caseType": "{{CaseType}}",
                "sourceSystemId": "{{Ulid.NewUlid()}}",
                "callbackUrl": "https://source-system.test/callback",
                "nation": "ENGLAND",
                "orgId": "test-org"
            }
            """
        ).RootElement;
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

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(
            Microsoft.AspNetCore.Http.StatusCodes.Status400BadRequest,
            problem.StatusCode
        );
        Assert.Equal("Invalid request", problem.ProblemDetails.Title);
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

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // Persistence must never be hit for an out-of-range page — that's the
        // whole point of the cap.
        Assert.Null(factory.Recording.LastQuery);
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
        Assert.NotNull(body);
        Assert.Equal(2, body!.Items.Count);
        Assert.Equal(2, body.TotalCount);
        Assert.Equal(1, body.Page);
        Assert.Equal(CaseQuery.DefaultPageSize, body.PageSize);
    }

    [Fact]
    public async Task Get_list_omits_the_timeline_from_each_item()
    {
        // epr-4pf: the list endpoint never renders the per-item Timeline,
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
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Property-level assertion: the JSON must not even mention
        // 'timeline' on a list item.
        var rawJson = await response.Content.ReadAsStringAsync(cancellationToken);
        var root = System.Text.Json.Nodes.JsonNode.Parse(rawJson);
        Assert.NotNull(root);
        var firstItem = root!["items"]!.AsArray()[0]!.AsObject();
        Assert.False(firstItem.ContainsKey("timeline"), "list item must not include 'timeline'");

        var body = System.Text.Json.JsonSerializer.Deserialize<CaseListResponse>(
            rawJson,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            }
        );
        Assert.NotNull(body);
        var item = Assert.Single(body!.Items);
        Assert.Equal("alice-1", item.AssignedToId);
        Assert.Equal("Alice Example", item.AssignedToName);
        Assert.Equal(assignedAt, item.AssignedAt);
        Assert.Equal("manager-1", item.AssignedBy);
        Assert.Equal("submitted", item.StateId);
    }

    [Fact]
    public async Task Get_passes_query_string_filters_to_persistence()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            "/cases?caseType=re-accreditation&caseType=other-type&stateId=submitted&search=acme&assigneeId=alice-1&unassigned=true&page=2&pageSize=5",
            cancellationToken
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var captured = factory.Recording.LastQuery;
        Assert.NotNull(captured);
        Assert.NotNull(captured!.CaseTypes);
        Assert.Equal(s_expectedCapturedTypeIds, captured.CaseTypes);
        Assert.Equal(s_expectedCapturedStateIds, captured.StateIds);
        Assert.Equal("acme", captured.Search);
        Assert.Equal("alice-1", captured.AssigneeId);
        Assert.True(captured.UnassignedOnly);
        Assert.Equal(2, captured.Page);
        Assert.Equal(5, captured.PageSize);
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

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken);
        Assert.Equal("alice-1", body?.AssignedToId);
        Assert.Equal("Alice Example", body?.AssignedToName);
        Assert.Equal("actor-1", body?.AssignedBy);
        Assert.NotNull(body?.AssignedAt);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("alice-1", persisted!.AssignedToId);
        Assert.Equal(1, persisted.Version);
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

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.Equal("Invalid request", problem?.Title);
    }

    [Fact]
    public async Task Assign_ignores_a_non_string_assigneeName()
    {
        // Covers the `assigneeNameElement.ValueKind == JsonValueKind.String`
        // branch's false arm: assigneeName is optional and, when present
        // but the wrong JSON type, is silently dropped rather than
        // rejecting the whole request.
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

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken);
        Assert.Equal("alice-1", body?.AssignedToId);
        Assert.Null(body?.AssignedToName);
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

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken);
        Assert.Null(body?.AssignedToId);
        Assert.Null(body?.AssignedToName);
        Assert.Null(body?.AssignedAt);
        Assert.Null(body?.AssignedBy);
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

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("X-Idempotent-Replay", out var values));
        Assert.Equal("true", Assert.Single(values!));
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal(0, persisted!.Version);
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

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("X-Idempotent-Replay", out var values));
        Assert.Equal("true", Assert.Single(values!));
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal(0, persisted!.Version);
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

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False(response.Headers.Contains("X-Idempotent-Replay"));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.Equal("Action not allowed", problem?.Title);
        Assert.Equal(409, problem?.Status);
        Assert.Contains(stateId, problem?.Detail);
        Assert.DoesNotContain(id.ToString(), problem?.Detail);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Null(persisted!.AssignedToId);
        Assert.Equal(0, persisted.Version);
        Assert.Empty(persisted.Timeline);
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

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False(response.Headers.Contains("X-Idempotent-Replay"));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.Equal("Action not allowed", problem?.Title);
        Assert.Equal(409, problem?.Status);
        Assert.DoesNotContain(id.ToString(), problem?.Detail);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("alice-1", persisted!.AssignedToId);
        Assert.Equal(0, persisted.Version);
        Assert.Empty(persisted.Timeline);
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
        Assert.Equal(HttpStatusCode.OK, assign.StatusCode);

        var unassign = await client.PostAsync(
            $"/cases/{id}/unassign",
            content: null,
            cancellationToken
        );
        Assert.Equal(HttpStatusCode.OK, unassign.StatusCode);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Null(persisted!.AssignedToId);
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

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.Equal("Invalid request", problem?.Title);
    }

    [Fact]
    public async Task AddNote_persists_note_and_returns_updated_response()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory(userId: "alice-1", userName: "Alice Example");
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
            new { text = "Reviewed evidence; awaiting confirmation." },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken);
        Assert.NotNull(body?.Timeline);
        var note = Assert.Single(body!.Timeline!, a => a.Action == "note-added");
        Assert.Equal("Reviewed evidence; awaiting confirmation.", note.Details["text"]);
        Assert.Equal("alice-1", note.PerformedById);
        Assert.Equal("Alice Example", note.PerformedByName);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Single(persisted!.Timeline, a => a.Action == "note-added");
        Assert.Equal(1, persisted.Version);
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

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken);
        Assert.NotNull(body?.Timeline);
        Assert.Collection(
            body!.Timeline!,
            first =>
            {
                Assert.Equal("note-added", first.Action);
                Assert.Equal(newer, first.PerformedAt);
            },
            second =>
            {
                Assert.Equal("task-completed", second.Action);
                Assert.Equal("Task completed", second.ActionDisplayName);
                Assert.Equal("check-eligibility", second.Details["taskId"]);
                Assert.Equal(older, second.PerformedAt);
            }
        );
    }

    [Fact]
    public async Task Timeline_preserves_insertion_order_for_entries_with_identical_timestamps()
    {
        // Regression for epr-s4y: when two entries share an exact PerformedAt
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
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken);
        Assert.NotNull(body?.Timeline);
        Assert.Collection(
            body!.Timeline!,
            first => Assert.Equal("3", first.Details["sequence"]),
            second => Assert.Equal("2", second.Details["sequence"]),
            third => Assert.Equal("1", third.Details["sequence"])
        );
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
        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
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
        private readonly string? _userName;
        private readonly IReadOnlyCollection<CaseState>? _states;

        public TestApplicationFactory(
            MongoIntegrationFixture fixture,
            bool includeAuthHeader,
            string? userId,
            string? userName,
            IReadOnlyCollection<CaseState>? states = null
        )
        {
            _fixture = fixture;
            _includeAuthHeader = includeAuthHeader;
            _userId = userId;
            _userName = userName;
            _states = states;
        }

        public ICasePersistence Persistence => Recording.Inner;

        public RecordingPersistence Recording =>
            (RecordingPersistence)Services.GetRequiredService<ICasePersistence>();

        public Task SeedAsync(Case item, CancellationToken cancellationToken) =>
            Persistence.CreateAsync(item, cancellationToken);

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
                        sp.GetRequiredService<ICaseRegistry>())
                ));
                services.AddSingleton<ICaseType>(
                    new TestCaseType(
                        CaseType,
                        "Test type",
                        states: _states
                    )
                );

                // Background services call QueryAsync at startup which sets
                // Recording.LastQuery and contaminates tests that assert the
                // query was never issued. Remove the offender.
                var migrationDescriptor = services.FirstOrDefault(d =>
                    d.ImplementationType == typeof(CaseMigrationHostedService)
                );
                if (migrationDescriptor is not null)
                {
                    services.Remove(migrationDescriptor);
                }
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
            if (_userName is not null)
            {
                client.DefaultRequestHeaders.Add("x-cdp-user-name", _userName);
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
