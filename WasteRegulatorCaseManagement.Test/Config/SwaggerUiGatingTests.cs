using WasteRegulatorCaseManagement.Config;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace WasteRegulatorCaseManagement.Test.Config;

public class SwaggerUiGatingTests
{
	[Theory]
	[InlineData("Production", null, false)]
	[InlineData("Production", "true", true)]
	[InlineData("Production", "false", false)]
	[InlineData("Development", null, true)]
	[InlineData("Staging", null, true)]
	public void ShouldEnableSwaggerUi_returns_expected(string environmentName, string? swaggerEnabled, bool expected)
	{
		var env = Substitute.For<IHostEnvironment>();
		env.EnvironmentName.Returns(environmentName);

		var settings = new Dictionary<string, string?>();
		if (swaggerEnabled is not null)
		{
			settings["Swagger:Enabled"] = swaggerEnabled;
		}
		var config = new ConfigurationBuilder()
			.AddInMemoryCollection(settings)
			.Build();

		SwaggerUiGating.ShouldEnableSwaggerUi(env, config).Should().Be(expected);
	}

	[Fact]
	public void ShouldEnableSwaggerUi_throws_when_env_is_null()
	{
		var config = new ConfigurationBuilder().Build();
		var act = () => SwaggerUiGating.ShouldEnableSwaggerUi(null!, config);
		act.Should().ThrowExactly<ArgumentNullException>();
	}

	[Fact]
	public void ShouldEnableSwaggerUi_throws_when_config_is_null()
	{
		var env = Substitute.For<IHostEnvironment>();
		var act = () => SwaggerUiGating.ShouldEnableSwaggerUi(env, null!);
		act.Should().ThrowExactly<ArgumentNullException>();
	}
}
