using System.Net;
using System.Net.Http.Json;
using WasteRegulatorCaseManagement.Test.TestSupport;
using WasteRegulatorCaseManagement.Utils.Mongo;
using WasteRegulatorCaseManagement.Cases;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using WasteRegulatorCaseManagement.Auth;
using NUlid;

namespace WasteRegulatorCaseManagement.Test.Cases;

/// <summary>
/// epr-dt2: negative-path coverage for the auth and assignment role
/// boundaries documented in AGENTS.md. Every test exercises the real
/// pipeline (ClientIdAuthenticationHandler, routing,
/// ProblemDetails) against ephemeral MongoDB so it can also assert the
/// fail-closed property — that no audit entry is written and no on-disk
/// version is bumped when the request is denied.
/// </summary>
public class CaseAuthBoundaryTests
{
    private const string CaseType = "test-type";
    private const string TenantClientId = "test-client";
    private readonly MongoIntegrationFixture _fixture;

    public CaseAuthBoundaryTests(MongoIntegrationFixture fixture) => _fixture = fixture;

    // Coverage for "mutations require a 'user:id' claim — fail closed" used to
    // live here, driven through the task-complete endpoint. RA-410 removed
    // that endpoint along with the rest of the task framework; the
    // missing-actor-identity boundary itself is unit-tested in
    // CaseServiceTests and HTTP-tested in CaseEndpointsTests /
    // CaseEngineEndpointsTests against the endpoints that remain.

    // ---------------- (2) Assign / Unassign — RA-323: any caseworker ----------------

    [Fact]
    public async Task Assign_succeeds_when_standard_user_targets_another_user()
    {
        // RA-323: every caseworker holds the same role, so a standard user
        // can assign a case to anyone, not just claim it for themselves.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BoundaryFactory(_fixture, userId: "alice-1");
        using var client = factory.CreateClient();

        var id = Ulid.NewUlid().ToString();
        await factory.SeedAsync(NewSubmittedItem(id), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/cases/{id}/assign",
            new { assigneeId = "bob-2", assigneeName = "Bob" },
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("bob-2", persisted!.AssignedToId);
        Assert.Equal(1, persisted.Version);
        Assert.Single(persisted.Timeline);
    }

    [Fact]
    public async Task Assign_succeeds_when_standard_user_reassigns_already_assigned_item()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BoundaryFactory(_fixture, userId: "alice-1");
        using var client = factory.CreateClient();

        var id = Ulid.NewUlid().ToString();
        var seeded = NewSubmittedItem(id);
        seeded.AssignedToId = "bob-2";
        seeded.AssignedToName = "Bob";
        await factory.SeedAsync(seeded, cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/cases/{id}/assign",
            new { assigneeId = "alice-1", assigneeName = "Alice" },
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("alice-1", persisted!.AssignedToId);
        Assert.Equal(1, persisted.Version);
        Assert.Single(persisted.Timeline);
    }

    [Fact]
    public async Task Unassign_succeeds_for_standard_user()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BoundaryFactory(_fixture, userId: "alice-1");
        using var client = factory.CreateClient();

        var id = Ulid.NewUlid().ToString();
        var seeded = NewSubmittedItem(id);
        seeded.AssignedToId = "alice-1";
        seeded.AssignedToName = "Alice";
        await factory.SeedAsync(seeded, cancellationToken);

        var response = await client.PostAsync($"/cases/{id}/unassign", content: null, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Null(persisted!.AssignedToId);
        Assert.Equal(1, persisted.Version);
        Assert.Single(persisted.Timeline);
    }

    // Coverage for "RBAC lives in the frontend now" (no ownership gate on
    // GetById/list) lives in CaseEndpointsTests — this class stays
    // scoped to the RA-323 assign/unassign permission model boundary above.

    // ---------------------------- Helpers ----------------------------

    private static Case NewSubmittedItem(string id) => new()
    {
        Id = id,
        SourceSystemId = Ulid.NewUlid().ToString(),
        CallbackUrl = "https://source-system.test/callback",
        Nation = "ENGLAND",
        OrgId = "test-org",
        CaseType = CaseType,
        StateId = "submitted",
        SubmittedBy = TenantClientId
    };

    private sealed class BoundaryFactory : WebApplicationFactory<Program>
    {
        private readonly MongoIntegrationFixture _fixture;
        private readonly string _databaseName = MongoIntegrationFixture.NewDatabaseName("authbnd");
        private readonly string? _userId;

        public BoundaryFactory(
            MongoIntegrationFixture fixture,
            string? userId)
        {
            _fixture = fixture;
            _userId = userId;
        }

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
                    new CasePersistence(clientFactory, sp.GetRequiredService<ILoggerFactory>(), sp.GetRequiredService<CaseRegistry>()));
                services.UseCaseModuleConfigs(TestCaseType.Create(CaseType, "Test type"));
            });
        }

        protected override void ConfigureClient(HttpClient client)
        {
            base.ConfigureClient(client);
            client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, TenantClientId);
            if (_userId is not null)
            {
                // Use TryAddWithoutValidation so the test can deliberately
                // send a whitespace user-id (which HttpClient would
                // otherwise normalise away) for the
                // ResolveActorUserId-returns-null path.
                client.DefaultRequestHeaders.TryAddWithoutValidation("x-cdp-user-id", _userId);
                client.DefaultRequestHeaders.Add("x-cdp-user-nation", "ENGLAND");
            }
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
                    // Best-effort.
                }
            }
            base.Dispose(disposing);
        }
    }
}
