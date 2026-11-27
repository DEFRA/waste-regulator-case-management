using WasteRegulatorCaseManagement.AuditEvents;
using System.Net;
using System.Net.Http.Json;
using WasteRegulatorCaseManagement.Test.TestSupport;
using WasteRegulatorCaseManagement.Cases;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using WasteRegulatorCaseManagement.Auth;
using NSubstitute;

namespace WasteRegulatorCaseManagement.Test;

/// <summary>
/// This suite exists to prove the global exception handler
/// turns unhandled exceptions into RFC 7807 <see cref="ProblemDetails"/>
/// — a middleware contract, not a persistence contract. To force the
/// exception we wrap a real <see cref="CasePersistence"/>
/// (ephemeral Mongo) in a thin fault-injection decorator so the test
/// still exercises the production HTTP pipeline end-to-end without
/// substituting the persistence layer wholesale (the previous
/// implementation mocked <see cref="ICasePersistence"/>).
/// </summary>
public class ProblemDetailsExceptionHandlerTests
{
	private readonly MongoIntegrationFixture _fixture;

	public ProblemDetailsExceptionHandlerTests(MongoIntegrationFixture fixture)
	{
		_fixture = fixture;
	}

	[Fact]
	public async Task Unhandled_exception_is_returned_as_problem_details_with_500()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var factory = new ThrowingFactory(_fixture);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, "test-client");
		client.DefaultRequestHeaders.Add("x-cdp-user-nation", "ENGLAND");

		var response = await client.GetAsync("/cases", ct);

		response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
		response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
		var problem =
			await response.Content.ReadFromJsonAsync<ProblemDetails>(ct)
			?? throw new InvalidOperationException("Expected problem details body.");
		problem.Status.Should().Be(StatusCodes.Status500InternalServerError);
	}

	private sealed class ThrowingFactory(MongoIntegrationFixture fixture) : WebApplicationFactory<Program>
	{
		private readonly string _databaseName = MongoIntegrationFixture.NewDatabaseName("problem_details");

		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			builder.ConfigureServices(services =>
			{
				services.UseEphemeralMongoPersistence(fixture, _databaseName);

				services.RemoveAll<ICasePersistence>();
				var clientFactory = new TestMongoDbClientFactory(
					fixture.ConnectionString, _databaseName);
				var real = new CasePersistence(clientFactory, NullLoggerFactory.Instance, new CaseRegistry([]), new AuditEventService(clientFactory, TimeProvider.System));
				var persistence = Substitute.For<ICasePersistence>();
				persistence
					.QueryAsync(Arg.Any<CaseQuery>(), Arg.Any<CancellationToken>())
					.Returns<Task<CasePage>>(_ => throw new InvalidOperationException("boom"));
				persistence
					.GetByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
					.Returns(call =>
						real.GetByIdAsync(call.ArgAt<string>(0), call.ArgAt<CancellationToken>(1)));
				persistence
					.FindBySourceSystemIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
					.Returns(call =>
						real.FindBySourceSystemIdAsync(call.ArgAt<string>(0), call.ArgAt<CancellationToken>(1)));
				persistence
					.CreateAsync(Arg.Any<Case>(), Arg.Any<CancellationToken>())
					.Returns(call => real.CreateAsync(call.ArgAt<Case>(0), call.ArgAt<CancellationToken>(1)));
				persistence
					.ReplaceAsync(
						Arg.Any<Case>(),
						Arg.Any<CaseCompletionEnvelopeContent>(),
						Arg.Any<CancellationToken>())
					.Returns(call =>
						real.ReplaceAsync(
							call.ArgAt<Case>(0),
							call.ArgAt<CaseCompletionEnvelopeContent?>(1),
							call.ArgAt<CancellationToken>(2)));
				services.AddSingleton(persistence);
			});
		}
	}
}