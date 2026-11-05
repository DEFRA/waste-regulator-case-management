using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
/// RA-364: pins the <c>availableActions</c> wire contract end-to-end, over the
/// real serialiser the minimal API uses rather than a hand-built
/// <see cref="JsonSerializerOptions"/>.
///
/// Two things are asserted because two consumers depend on them:
/// <list type="bullet">
/// <item>Non-caller-invocable transitions are absent from BOTH the single
/// case response and the list response (AC5) — one projection fix, two
/// call sites.</item>
/// <item>The flag serialises as the camelCase key <c>callerInvocable</c> and
/// is emitted on every entry, which management-fe relies on for its
/// defence-in-depth filter against an unpatched backend.</item>
/// </list>
/// </summary>
public class CaseAvailableActionsWireContractTests
{
    private const string TypeId = "wire-contract-type";
    private readonly MongoIntegrationFixture _fixture;

    public CaseAvailableActionsWireContractTests(MongoIntegrationFixture fixture) =>
        _fixture = fixture;

    [Fact]
    public async Task Single_case_response_omits_non_invocable_actions_and_names_the_flag()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ContractFactory(_fixture);
        using var client = factory.CreateClient();

        var caseId = await SeedAsync(factory, cancellationToken);

        var response = await client.GetAsync($"/cases/{caseId}", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(json);
        var actions = document.RootElement.GetProperty("availableActions");

        AssertContractHolds(actions);
    }

    [Fact]
    public async Task List_response_omits_non_invocable_actions_and_names_the_flag()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ContractFactory(_fixture);
        using var client = factory.CreateClient();

        var caseId = await SeedAsync(factory, cancellationToken);

        // pageSize=100: the seeded item has no SubmittedAt so it sorts last,
        // behind the demo items and test-organisation fixtures, which together
        // outnumber the default page of 20.
        var response = await client.GetAsync("/cases?pageSize=100", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(json);

        // Pick ours out by id rather than assuming a single-element list.
        var item = Assert.Single(
            document.RootElement.GetProperty("items").EnumerateArray()
                .Where(i => i.GetProperty("id").GetString() == caseId.ToString()));

        AssertContractHolds(item.GetProperty("availableActions"));
    }

    private static void AssertContractHolds(JsonElement actions)
    {
        var actionIds = actions
            .EnumerateArray()
            .Select(a => a.GetProperty("actionId").GetString())
            .ToArray();

        // The bug: two transitions sharing a DisplayName, both non-invocable,
        // previously rendered as two identical dead buttons.
        Assert.Equal(["withdraw"], actionIds);
        Assert.DoesNotContain("resume-a", actionIds);
        Assert.DoesNotContain("resume-b", actionIds);

        // The exact key management-fe filters on, and its type. Emitted on
        // every entry — no WhenWritingDefault suppression.
        foreach (var action in actions.EnumerateArray())
        {
            Assert.True(
                action.TryGetProperty("callerInvocable", out var flag),
                "availableActions entries must carry the camelCase 'callerInvocable' key.");
            Assert.Equal(JsonValueKind.True, flag.ValueKind);
        }
    }

    private static async Task<string> SeedAsync(
        ContractFactory factory,
        CancellationToken cancellationToken)
    {
        var caseId = Ulid.NewUlid().ToString();
        await factory.SeedAsync(
            new Case
            {
                SourceSystemId = Ulid.NewUlid().ToString(),
                CallbackUrl = "https://source-system.test/callback",
                Nation = "ENGLAND",
                OrgId = "test-org",
                Id = caseId,
                TypeId = TypeId,
                StateId = "queried",
                SubmittedBy = "test-client",
            },
            cancellationToken);
        return caseId;
    }

    private sealed class ContractFactory : WebApplicationFactory<Program>
    {
        private readonly MongoIntegrationFixture _fixture;
        private readonly string _databaseName = MongoIntegrationFixture.NewDatabaseName("wire");

        public ContractFactory(MongoIntegrationFixture fixture) => _fixture = fixture;

        public ICasePersistence Persistence =>
            Services.GetRequiredService<ICasePersistence>();

        public Task SeedAsync(Case item, CancellationToken cancellationToken) =>
            Persistence.CreateAsync(item, cancellationToken);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ICasePersistence>();
                services.RemoveAll<IMongoDbClientFactory>();
                var clientFactory = new TestMongoDbClientFactory(
                    _fixture.ConnectionString, _databaseName);
                services.AddSingleton<IMongoDbClientFactory>(clientFactory);
                services.AddSingleton<ICasePersistence>(sp =>
                    new CasePersistence(clientFactory, sp.GetRequiredService<ILoggerFactory>(), sp.GetRequiredService<ICaseRegistry>()));

                // Mirrors the shape that produced the bug: several
                // non-invocable transitions sharing a FromStateId and
                // a DisplayName, alongside one genuine caller-invocable action.
                services.AddSingleton<ICaseType>(new TestCaseType(
                    TypeId,
                    "Wire contract type",
                    states:
                    [
                        new CaseState("queried", "Queried"),
                        new CaseState("updated", "Updated"),
                        new CaseState("withdrawn", "Withdrawn", IsTerminal: true)
                    ],
                    transitions:
                    [
                        new CaseTransition(
                            "resume-a", "Resume", "queried", "updated", CallerInvocable: false),
                        new CaseTransition(
                            "resume-b", "Resume", "queried", "updated", CallerInvocable: false),
                        new CaseTransition(
                            "withdraw", "Withdraw", "queried", "withdrawn")
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
                    Services.GetRequiredService<IMongoDbClientFactory>()
                        .GetClient()
                        .DropDatabase(_databaseName);
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
