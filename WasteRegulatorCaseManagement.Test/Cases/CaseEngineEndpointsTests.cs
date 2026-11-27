using WasteRegulatorCaseManagement.AuditEvents;
using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions.Execution;
using WasteRegulatorCaseManagement.Test.TestSupport;
using WasteRegulatorCaseManagement.Utils.Mongo;
using WasteRegulatorCaseManagement.Cases;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NUlid;

namespace WasteRegulatorCaseManagement.Test.Cases;

/// <summary>
/// Persistence is real <see cref="CasePersistence"/>
/// against ephemeral MongoDB. Tests seed via the exposed
/// <see cref="EngineFactory.Persistence"/> and re-read for assertions.
/// </summary>
public class CaseEngineEndpointsTests
{
	private const string CaseType = "test-type";
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
			CompletionEvent = CaseTestCompletionEvent.DefaultSpec,
			Nation = "ENGLAND",
			OrgId = "test-org",
			Id = caseId,
			CaseType = CaseType,
			StateId = "submitted",
			SubmittedBy = "test-client"
		}, cancellationToken);

		var response = await client.PostAsync(
			$"/cases/{caseId}/actions/approve", content: null, cancellationToken);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await response.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken);
		body.Should().BeEquivalentTo(new { StateId = "approved" });
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
			CompletionEvent = CaseTestCompletionEvent.DefaultSpec,
			Nation = "ENGLAND",
			OrgId = "test-org",
			Id = caseId,
			CaseType = CaseType,
			StateId = "submitted",
			SubmittedBy = "test-client"
		}, cancellationToken);

		var response = await client.PostAsync(
			$"/cases/{caseId}/actions/approve", content: null, cancellationToken);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = TestAsserts.NotNull(await response.Content.ReadFromJsonAsync<CaseResponse>(cancellationToken));

		using var scope = new AssertionScope();
		body.StateId.Should().Be("approved");
		body.AvailableActions.Should().BeEmpty();
	}

	[Fact]
	public async Task Action_returns_404_when_case_missing()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = new EngineFactory(_fixture);
		using var client = factory.CreateClient();

		var response = await client.PostAsync(
			$"/cases/{Ulid.NewUlid().ToString()}/actions/approve", content: null, cancellationToken);

		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
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
			CompletionEvent = CaseTestCompletionEvent.DefaultSpec,
			Nation = "ENGLAND",
			OrgId = "test-org",
			Id = caseId,
			CaseType = CaseType,
			StateId = "submitted",
			SubmittedBy = "test-client"
		}, cancellationToken);

		var response = await client.PostAsync(
			$"/cases/{caseId}/actions/teleport", content: null, cancellationToken);

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
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
			CompletionEvent = CaseTestCompletionEvent.DefaultSpec,
			Nation = "ENGLAND",
			OrgId = "test-org",
			Id = caseId,
			CaseType = CaseType,
			StateId = "submitted",
			SubmittedBy = "test-client"
		}, cancellationToken);

		var body = TestAsserts.NotNull(
			await client.GetFromJsonAsync<CaseResponse>($"/cases/{caseId}", cancellationToken)
		);
		// RA-410: "approve" used to be absent here because it was gated on
		// an outstanding task. The task framework (and the gate) are gone,
		// so the same seed now offers it — regression cover for the
		// ungating.
		body.AvailableActions.Should().Contain(a => a.ActionId == "approve");
	}

	private sealed class EngineFactory : WebApplicationFactory<Program>
	{
		private readonly MongoIntegrationFixture _fixture;
		private readonly string _databaseName = MongoIntegrationFixture.NewDatabaseName("engine");

		public EngineFactory(MongoIntegrationFixture fixture) => _fixture = fixture;

		public ICasePersistence Persistence => Services.GetRequiredService<ICasePersistence>();

		public Task SeedAsync(Case item, CancellationToken cancellationToken) =>
			Services
				.GetRequiredService<IMongoDbClientFactory>()
				.GetCollection<Case>("cases")
				.InsertOneAsync(item, cancellationToken: cancellationToken);

		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			builder.ConfigureServices(services =>
			{
				services.RemoveAll<ICasePersistence>();
				services.RemoveAll<IMongoDbClientFactory>();
				var clientFactory = new TestMongoDbClientFactory(_fixture.ConnectionString, _databaseName);
				services.AddSingleton<IMongoDbClientFactory>(clientFactory);
				services.AddSingleton<ICasePersistence>(sp =>
					new CasePersistence(clientFactory, sp.GetRequiredService<ILoggerFactory>(), sp.GetRequiredService<CaseRegistry>(), new AuditEventService(clientFactory, TimeProvider.System)));

				services.UseCaseModuleConfigs(TestCaseType.Create(
					CaseType,
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
			TestCaseworkerClaims.ApplyCaseworkerHeaders(client);
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
