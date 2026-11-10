using WasteRegulatorCaseManagement.Test.TestSupport;
using System.Net;
using WasteRegulatorCaseManagement.Utils.Mongo;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace WasteRegulatorCaseManagement.Test;

public class ReDocEndpointTests
{
	[Fact]
	public async Task ReDoc_index_is_served_in_production_without_auth()
	{
		// Arrange
		var ct = TestContext.Current.CancellationToken;
		await using var factory = new ProductionFactory();
		using var client = factory.CreateClient();

		// Act
		var response = await client.GetAsync("/documentation/index.html", ct);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task ReDoc_script_loads_the_openapi_document()
	{
		// Arrange
		var ct = TestContext.Current.CancellationToken;
		await using var factory = new ProductionFactory();
		using var client = factory.CreateClient();

		// Act
		var response = await client.GetAsync("/documentation/index.js", ct);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await response.Content.ReadAsStringAsync(ct);
		body.Should().Contain("/openapi/v1.json");
	}

	private sealed class ProductionFactory : WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			builder.UseEnvironment("Production");
			builder.ConfigureServices(services =>
			{
				services.UseCaseModuleConfigs();
				services.RemoveAll<IMongoDbClientFactory>();
				services.AddSingleton(Substitute.For<IMongoDbClientFactory>());
			});
		}
	}
}
