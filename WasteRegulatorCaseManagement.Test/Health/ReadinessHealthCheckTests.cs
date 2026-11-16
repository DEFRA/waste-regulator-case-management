using WasteRegulatorCaseManagement.Test.TestSupport;
using System.Net;
using WasteRegulatorCaseManagement.Health;
using WasteRegulatorCaseManagement.Utils.Mongo;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace WasteRegulatorCaseManagement.Test.Health;

public class ReadinessHealthCheckTests
{
	[Fact]
	public async Task Liveness_returns_healthy_even_when_mongo_is_down()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var factory = new HealthFactory(mongoHealthy: false);
		using var client = factory.CreateClient();

		var response = await client.GetAsync("/health", ct);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task Readiness_fails_when_mongo_factory_cannot_connect()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var factory = new HealthFactory(mongoHealthy: false);
		using var client = factory.CreateClient();

		var response = await client.GetAsync("/health/ready", ct);

		response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
	}

	[Fact]
	public async Task Readiness_succeeds_when_mongo_factory_is_healthy()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var factory = new HealthFactory(mongoHealthy: true);
		using var client = factory.CreateClient();

		var response = await client.GetAsync("/health/ready", ct);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task Head_health_returns_200()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var factory = new HealthFactory(mongoHealthy: true);
		using var client = factory.CreateClient();

		var response = await client.SendAsync(
			new HttpRequestMessage(HttpMethod.Head, "/health"),
			ct
		);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task Trace_health_returns_405()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var factory = new HealthFactory(mongoHealthy: true);
		using var client = factory.CreateClient();

		var response = await client.SendAsync(
			new HttpRequestMessage(HttpMethod.Trace, "/health"),
			ct
		);

		response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
	}

	[Fact]
	public async Task Trace_health_ready_returns_405()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var factory = new HealthFactory(mongoHealthy: true);
		using var client = factory.CreateClient();

		var response = await client.SendAsync(
			new HttpRequestMessage(HttpMethod.Trace, "/health/ready"),
			ct
		);

		response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
	}

	[Theory]
	[InlineData("PUT")]
	[InlineData("DELETE")]
	[InlineData("PATCH")]
	public async Task Other_verbs_on_health_return_405(string verb)
	{
		var ct = TestContext.Current.CancellationToken;
		await using var factory = new HealthFactory(mongoHealthy: true);
		using var client = factory.CreateClient();

		var response = await client.SendAsync(
			new HttpRequestMessage(new HttpMethod(verb), "/health"),
			ct
		);

		response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
	}

	private sealed class HealthFactory(bool mongoHealthy) : WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			builder.ConfigureServices(services =>
			{
				services.UseCaseModuleConfigs();
				services.RemoveAll<IMongoDbClientFactory>();
				var mongoFactory = Substitute.For<IMongoDbClientFactory>();
				var client = Substitute.For<IMongoClient>();
				var db = Substitute.For<IMongoDatabase>();
				client.GetDatabase("admin", Arg.Any<MongoDatabaseSettings?>()).Returns(db);
				if (mongoHealthy)
				{
					db.RunCommandAsync(
							Arg.Any<Command<BsonDocument>>(),
							Arg.Any<ReadPreference>(),
							Arg.Any<CancellationToken>()
						)
						.Returns(new BsonDocument("ok", 1));
				}
				else
				{
					db.RunCommandAsync(
							Arg.Any<Command<BsonDocument>>(),
							Arg.Any<ReadPreference>(),
							Arg.Any<CancellationToken>()
						)
						.Throws(new TimeoutException("mongo unreachable"));
				}
				mongoFactory.GetClient().Returns(client);
				services.AddSingleton(mongoFactory);
			});
		}
	}
}
