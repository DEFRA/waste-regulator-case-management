using System.Net;
using System.Net.Http.Json;
using WasteRegulatorCaseManagement.Test.TestSupport;
using WasteRegulatorCaseManagement.Utils.Mongo;
using WasteRegulatorCaseManagement.Cases;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using WasteRegulatorCaseManagement.Auth;
using NUlid;

namespace WasteRegulatorCaseManagement.Test.Cases;

/// <summary>
/// epr-efp: persistence is real <see cref="CasePersistence"/>
/// against ephemeral MongoDB. Tests seed via the exposed
/// <see cref="EngineFactory.Persistence"/> and re-read for assertions.
/// </summary>
public class CaseEngineEndpointsTests
{
    private const string TypeId = "test-type";
    private readonly MongoIntegrationFixture _fixture;

    public CaseEngineEndpointsTests(MongoIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Action_returns_200_when_no_tasks_gate_the_transition()
    {
        // RA-410: this used to assert 409 Conflict because the "approve"
        // transition was gated on an outstanding task. The task framework
        // (and the gate) are gone, so the same seed now simply succeeds —
        // regression cover for the ungating.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new EngineFactory(_fixture);
        using var client = factory.CreateClient();

        var caseId = Ulid.NewUlid().ToString();
        await factory.SeedAsync(new Case
        {
            SourceSystemId = Ulid.NewUlid().ToString(),
            CallbackUrl = "https://source-system.test/callback",
            Nation = "ENGLAND",
            OrgId = "test-org",
            Id = caseId,
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client"
        }, cancellationToken);

        var response = await client.PostAsync(
            $"/cases/{caseId}/actions/approve", content: null, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken);
        Assert.Equal("approved", body?.StateId);
    }

    [Fact]
    public async Task Action_transitions_state_when_tasks_complete()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new EngineFactory(_fixture);
        using var client = factory.CreateClient();

        var caseId = Ulid.NewUlid().ToString();
        await factory.SeedAsync(new Case
        {
            SourceSystemId = Ulid.NewUlid().ToString(),
            CallbackUrl = "https://source-system.test/callback",
            Nation = "ENGLAND",
            OrgId = "test-org",
            Id = caseId,
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client"
        }, cancellationToken);

        var response = await client.PostAsync(
            $"/cases/{caseId}/actions/approve", content: null, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken);
        Assert.Equal("approved", body?.StateId);
        Assert.Empty(body!.AvailableActions);
    }

    [Fact]
    public async Task Action_returns_404_when_case_missing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new EngineFactory(_fixture);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            $"/cases/{Ulid.NewUlid().ToString()}/actions/approve", content: null, cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Action_returns_400_when_action_unknown()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new EngineFactory(_fixture);
        using var client = factory.CreateClient();

        var caseId = Ulid.NewUlid().ToString();
        await factory.SeedAsync(new Case
        {
            SourceSystemId = Ulid.NewUlid().ToString(),
            CallbackUrl = "https://source-system.test/callback",
            Nation = "ENGLAND",
            OrgId = "test-org",
            Id = caseId,
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client"
        }, cancellationToken);

        var response = await client.PostAsync(
            $"/cases/{caseId}/actions/teleport", content: null, cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Action_rejects_a_non_caller_invocable_transition_without_reaching_the_engine()
    {
        // RA-311/MBE-1: a transition declared CallerInvocable: false on the
        // item's own frozen snapshot must be refused by the endpoint itself
        // — before ApplyActionAsync ever runs — so a caller cannot pick a
        // module's server-resolved-only target state directly.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new EngineFactory(_fixture);
        using var client = factory.CreateClient();

        var snapshotType = new TestCaseType(
            TypeId,
            "Test type",
            states:
            [
                new CaseState("submitted", "Submitted"),
                new CaseState("approved", "Approved", IsTerminal: true)
            ],
            transitions:
            [
                new CaseTransition("resume-a", "Resume", "submitted", "approved", CallerInvocable: false)
            ]);
        var caseId = Ulid.NewUlid().ToString();
        await factory.SeedAsync(new Case
        {
            SourceSystemId = Ulid.NewUlid().ToString(),
            CallbackUrl = "https://source-system.test/callback",
            Nation = "ENGLAND",
            OrgId = "test-org",
            Id = caseId,
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client",
            TemplateSnapshot = CaseTemplateSnapshot.Capture(snapshotType),
        }, cancellationToken);

        var response = await client.PostAsync(
            $"/cases/{caseId}/actions/resume-a", content: null, cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // The engine's real ApplyActionAsync never ran, so the item is
        // unchanged and carries no audit entry for the blocked action.
        var persisted = await factory.Persistence.GetByIdAsync(caseId, cancellationToken);
        Assert.Equal("submitted", persisted!.StateId);
        Assert.Empty(persisted.Timeline);
    }

    [Fact]
    public async Task Get_by_id_projects_engine_state_in_response()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new EngineFactory(_fixture);
        using var client = factory.CreateClient();

        var caseId = Ulid.NewUlid().ToString();
        await factory.SeedAsync(new Case
        {
            SourceSystemId = Ulid.NewUlid().ToString(),
            CallbackUrl = "https://source-system.test/callback",
            Nation = "ENGLAND",
            OrgId = "test-org",
            Id = caseId,
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client"
        }, cancellationToken);

        var body = await client.GetFromJsonAsync<CaseResponse>($"/cases/{caseId}", cancellationToken);

        Assert.NotNull(body);
        // RA-410: "approve" used to be absent here because it was gated on
        // an outstanding task. The task framework (and the gate) are gone,
        // so the same seed now offers it — regression cover for the
        // ungating.
        Assert.Contains(body!.AvailableActions, a => a.ActionId == "approve");
    }

    private sealed class EngineFactory : WebApplicationFactory<Program>
    {
        private readonly MongoIntegrationFixture _fixture;
        private readonly string _databaseName = MongoIntegrationFixture.NewDatabaseName("engine");

        public EngineFactory(MongoIntegrationFixture fixture) => _fixture = fixture;

        public ICasePersistence Persistence => Services.GetRequiredService<ICasePersistence>();

        public Task SeedAsync(Case item, CancellationToken cancellationToken) =>
            Persistence.CreateAsync(item, cancellationToken);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ICasePersistence>();
                services.RemoveAll<IMongoDbClientFactory>();
                var clientFactory = new TestMongoDbClientFactory(_fixture.ConnectionString, _databaseName);
                services.AddSingleton<IMongoDbClientFactory>(clientFactory);
                services.AddSingleton<ICasePersistence>(sp =>
                    new CasePersistence(clientFactory, sp.GetRequiredService<ILoggerFactory>(), sp.GetRequiredService<ICaseRegistry>()));

                services.AddSingleton<ICaseType>(new TestCaseType(
                    TypeId,
                    "Test type",
                    states:
                    [
                        new CaseState("submitted", "Submitted"),
                        new CaseState("approved", "Approved", IsTerminal: true)
                    ],
                    transitions:
                    [
                        new CaseTransition("approve", "Approve", "submitted", "approved")
                    ]));
            });
        }

        protected override void ConfigureClient(HttpClient client)
        {
            base.ConfigureClient(client);
            client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, "test-client");
            client.DefaultRequestHeaders.Add("x-cdp-user-id", "test-user");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    var clientFactory = Services.GetRequiredService<IMongoDbClientFactory>();
                    clientFactory.GetClient().DropDatabase(_databaseName);
                }
                catch
                {
                    // Best-effort — the ephemeral instance dies with the fixture.
                }
            }
            base.Dispose(disposing);
        }
    }
}
