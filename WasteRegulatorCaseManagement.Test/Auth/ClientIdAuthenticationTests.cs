using WasteRegulatorCaseManagement.Test.TestSupport;
using AwesomeAssertions.Execution;
using System.Net;
using System.Net.Http.Headers;
using WasteRegulatorCaseManagement.Auth;
using WasteRegulatorCaseManagement.Cases;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace WasteRegulatorCaseManagement.Test.Auth;

public class ClientIdAuthenticationTests
{
	private const string ClientId = "upstream-service";
	private static readonly string ApiKey = Guid.NewGuid().ToString();
	private const string OtherClientId = "other-upstream-service";
	private static readonly string OtherApiKey = Guid.NewGuid().ToString();

	[Fact]
	public async Task Protected_endpoint_returns_401_without_client_id_header()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = new BareFactory();
		using var client = factory.CreateClient();

		var response = await client.GetAsync("/cases", cancellationToken);

		response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task Protected_endpoint_returns_401_with_empty_client_id_header()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = new BareFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, string.Empty);

		var response = await client.GetAsync("/cases", cancellationToken);

		response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task Protected_endpoint_succeeds_with_client_id_header()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = new BareFactory();
		factory
			.MockPersistence.QueryAsync(Arg.Any<CaseQuery>(), Arg.Any<CancellationToken>())
			.Returns(new CasePage(Array.Empty<Case>(), 0, 1, 20));

		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, ClientId);
		client.DefaultRequestHeaders.Add("x-cdp-user-nation", "ENGLAND");

		var response = await client.GetAsync("/cases", cancellationToken);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task Health_endpoint_is_anonymous()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = new BareFactory();
		using var client = factory.CreateClient();

		var response = await client.GetAsync("/health", cancellationToken);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task Request_without_api_key_is_401_when_api_keys_are_configured()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = new BareFactory(
			apiKeys: new Dictionary<string, string> { [ClientId] = ApiKey }
		);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, ClientId);
		client.DefaultRequestHeaders.Add("x-cdp-user-nation", "ENGLAND");

		// Act
		var response = await client.GetAsync("/cases", cancellationToken);

		// Assert
		using var scope = new AssertionScope();
		response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		response.Headers.WwwAuthenticate.ToString().Should().Contain("Missing x-cdp-auth-key header");
	}

	[Fact]
	public async Task Request_with_wrong_api_key_is_401()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = new BareFactory(
			apiKeys: new Dictionary<string, string> { [ClientId] = ApiKey }
		);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, ClientId);
		client.DefaultRequestHeaders.Add("x-cdp-user-nation", "ENGLAND");
		client.DefaultRequestHeaders.Add("x-cdp-auth-key", Guid.NewGuid().ToString());

		// Act
		var response = await client.GetAsync("/cases", cancellationToken);

		// Assert
		using var scope = new AssertionScope();
		response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		response.Headers.WwwAuthenticate.ToString().Should().Contain("Invalid x-cdp-auth-key header");
	}

	[Fact]
	public async Task Request_with_the_callers_api_key_is_200()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = new BareFactory(
			apiKeys: new Dictionary<string, string> { [ClientId] = ApiKey }
		);
		factory
			.MockPersistence.QueryAsync(Arg.Any<CaseQuery>(), Arg.Any<CancellationToken>())
			.Returns(new CasePage(Array.Empty<Case>(), 0, 1, 20));
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, ClientId);
		client.DefaultRequestHeaders.Add("x-cdp-user-nation", "ENGLAND");
		client.DefaultRequestHeaders.Add("x-cdp-auth-key", ApiKey);

		// Act
		var response = await client.GetAsync("/cases", cancellationToken);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task Request_with_another_known_callers_api_key_is_401()
	{
		// RA-345: the key must be the one registered for the clientId the
		// request asserts, or a leak of one caller's key would let it
		// impersonate the other.

		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = new BareFactory(
			apiKeys: new Dictionary<string, string>
			{
				[ClientId] = ApiKey,
				[OtherClientId] = OtherApiKey,
			}
		);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, ClientId);
		client.DefaultRequestHeaders.Add("x-cdp-user-nation", "ENGLAND");
		client.DefaultRequestHeaders.Add("x-cdp-auth-key", OtherApiKey);

		// Act
		var response = await client.GetAsync("/cases", cancellationToken);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task Request_with_unrecognized_client_id_is_401()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = new BareFactory(
			apiKeys: new Dictionary<string, string> { [ClientId] = ApiKey }
		);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, OtherClientId);
		client.DefaultRequestHeaders.Add("x-cdp-user-nation", "ENGLAND");
		client.DefaultRequestHeaders.Add("x-cdp-auth-key", ApiKey);

		// Act
		var response = await client.GetAsync("/cases", cancellationToken);

		// Assert
		using var scope = new AssertionScope();
		response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		response.Headers.WwwAuthenticate.ToString().Should().Contain("Invalid x-cdp-auth-key header");
	}

	[Fact]
	public async Task Colliding_caller_client_ids_fail_loudly_on_first_options_resolution()
	{
		// RA-345: Auth:ManagementFeClientId and Auth:CaseIntakeClientId must
		// resolve to distinct clientIds — a misconfiguration that points
		// both at the same value would let one caller's key silently
		// overwrite the other's in the resolved ApiKeys map
		// (Program.cs::BuildApiKeys/AddCallerApiKey). NOT a startup check:
		// ApiKeys is bound lazily, so this throws on first options
		// resolution — the first request that hits the auth handler.
		// /health is anonymous and never resolves auth options, so this
		// misconfiguration passes health checks. Config KEYS (colon form)
		// are used here; ClientIdAuthenticationEnvVarTests covers the real
		// "__" -> ":" environment variable rewrite.

		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = new BareFactory(
			configOverrides: new Dictionary<string, string?>
			{
				["AUTH_API_KEY:MANAGEMENT_FE"] = ApiKey,
				["AUTH_API_KEY:CASE_INTAKE"] = OtherApiKey,
				["Auth:CaseIntakeClientId"] = "frontend", // collides with ManagementFe's default clientId
			}
		);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, "frontend");

		// Act
		var response = await client.GetAsync("/cases", cancellationToken);

		// Assert
		// Options resolution throws InvalidOperationException, which the
		// global exception handler turns into a 500 — distinct from an
		// ordinary auth failure.
		response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
	}

	[Fact]
	public async Task Each_caller_authenticates_with_its_own_api_key()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = new BareFactory(
			apiKeys: new Dictionary<string, string>
			{
				[ClientId] = ApiKey,
				[OtherClientId] = OtherApiKey,
			}
		);
		factory
			.MockPersistence.QueryAsync(Arg.Any<CaseQuery>(), Arg.Any<CancellationToken>())
			.Returns(new CasePage(Array.Empty<Case>(), 0, 1, 20));

		using var clientA = factory.CreateClient();
		clientA.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, ClientId);
		clientA.DefaultRequestHeaders.Add("x-cdp-user-nation", "ENGLAND");
		clientA.DefaultRequestHeaders.Add("x-cdp-auth-key", ApiKey);

		using var clientB = factory.CreateClient();
		clientB.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, OtherClientId);
		clientB.DefaultRequestHeaders.Add("x-cdp-user-nation", "ENGLAND");
		clientB.DefaultRequestHeaders.Add("x-cdp-auth-key", OtherApiKey);

		// Act
		var responseA = await clientA.GetAsync("/cases", cancellationToken);
		var responseB = await clientB.GetAsync("/cases", cancellationToken);

		// Assert
		using var scope = new AssertionScope();
		responseA.StatusCode.Should().Be(HttpStatusCode.OK);
		responseB.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task Production_environment_without_api_keys_returns_401()
	{
		// Fail CLOSED: in any non-Development environment having no API
		// keys configured means the contract with the callers is broken.
		// Header-trust mode must NOT be used.
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = new BareFactory(environment: "Production");
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, ClientId);

		var response = await client.GetAsync("/cases", cancellationToken);

		response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task Development_environment_without_api_keys_allows_header_only_request()
	{
		// Development ergonomics: BFF stub mode runs without API keys.
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = new BareFactory(environment: "Development");
		factory
			.MockPersistence.QueryAsync(Arg.Any<CaseQuery>(), Arg.Any<CancellationToken>())
			.Returns(new CasePage(Array.Empty<Case>(), 0, 1, 20));

		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, ClientId);
		client.DefaultRequestHeaders.Add("x-cdp-user-nation", "ENGLAND");

		var response = await client.GetAsync("/cases", cancellationToken);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Theory]
	[InlineData(ClientIdDefaults.DefaultHeaderName, 256)]
	[InlineData("x-cdp-user-id", 128)]
	public async Task Identity_header_exceeding_cap_is_401_with_descriptive_reason(
		string header,
		int cap
	)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = new BareFactory();
		using var client = factory.CreateClient();

		var oversize = new string('a', cap + 1);
		if (header == ClientIdDefaults.DefaultHeaderName)
		{
			client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, oversize);
		}
		else
		{
			client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, ClientId);
			client.DefaultRequestHeaders.Add(header, oversize);
		}

		var response = await client.GetAsync("/cases", cancellationToken);

		response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		var challenge = response.Headers.WwwAuthenticate.ToString();
		challenge.Should().Contain($"{header} exceeds {cap} chars");
	}

	[Fact]
	public async Task Oversize_api_key_is_401_with_descriptive_reason()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = new BareFactory(
			apiKeys: new Dictionary<string, string> { [ClientId] = ApiKey }
		);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, ClientId);
		client.DefaultRequestHeaders.Add("x-cdp-auth-key", new string('a', 257));

		// Act
		var response = await client.GetAsync("/cases", cancellationToken);

		// Assert
		using var scope = new AssertionScope();
		response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		response.Headers.WwwAuthenticate.ToString().Should().Contain("x-cdp-auth-key exceeds 256 chars");
	}

	[Theory]
	[InlineData(ClientIdDefaults.DefaultHeaderName, 256)]
	[InlineData("x-cdp-user-id", 128)]
	public async Task Identity_header_at_exactly_cap_is_accepted(string header, int cap)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = new BareFactory();
		factory
			.MockPersistence.QueryAsync(Arg.Any<CaseQuery>(), Arg.Any<CancellationToken>())
			.Returns(new CasePage(Array.Empty<Case>(), 0, 1, 20));

		using var client = factory.CreateClient();
		var atCap = new string('a', cap);
		if (header == ClientIdDefaults.DefaultHeaderName)
		{
			client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, atCap);
		}
		else
		{
			client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, ClientId);
			client.DefaultRequestHeaders.Add(header, atCap);
		}
		client.DefaultRequestHeaders.Add("x-cdp-user-nation", "ENGLAND");

		var response = await client.GetAsync("/cases", cancellationToken);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Theory]
	[InlineData("x-cdp-user-role", 64)]
	[InlineData("x-cdp-user-nation", 32)]
	public async Task Trust_header_exceeding_cap_is_401_with_descriptive_reason(
		string header,
		int cap
	)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = new BareFactory();
		using var client = factory.CreateClient();

		var oversize = new string('a', cap + 1);
		client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, ClientId);
		client.DefaultRequestHeaders.Add(header, oversize);

		var response = await client.GetAsync("/cases", cancellationToken);

		response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		var challenge = response.Headers.WwwAuthenticate.ToString();
		challenge.Should().Contain($"{header} exceeds {cap} chars");
	}

	[Theory]
	[InlineData("x-cdp-user-role", 64)]
	[InlineData("x-cdp-user-nation", 32)]
	public async Task Trust_header_at_exactly_cap_is_accepted(string header, int cap)
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = new BareFactory();
		factory
			.MockPersistence.QueryAsync(Arg.Any<CaseQuery>(), Arg.Any<CancellationToken>())
			.Returns(new CasePage(Array.Empty<Case>(), 0, 1, 20));

		using var client = factory.CreateClient();
		var atCap = new string('a', cap);
		client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, ClientId);
		client.DefaultRequestHeaders.Add(header, atCap);
		if (header != "x-cdp-user-nation")
		{
			client.DefaultRequestHeaders.Add("x-cdp-user-nation", "ENGLAND");
		}

		var response = await client.GetAsync("/cases", cancellationToken);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task Role_and_nation_trust_headers_present_do_not_break_auth()
	{
		// RA-469 AC17 groundwork: x-cdp-user-role/x-cdp-user-nation become
		// "user:role"/"user:nation" claims on the authenticated principal.
		// The claims actually landing correctly is proven end-to-end by the
		// role/nation-gated recycling-operations endpoint (which 403s when
		// either claim is absent or doesn't match) — the meaningful
		// black-box assertion of "claim set". This test covers the
		// auth-handler-level regression surface: presence of both new
		// trust headers must not prevent authentication succeeding.
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = new BareFactory();
		factory
			.MockPersistence.QueryAsync(Arg.Any<CaseQuery>(), Arg.Any<CancellationToken>())
			.Returns(new CasePage(Array.Empty<Case>(), 0, 1, 20));

		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, ClientId);
		client.DefaultRequestHeaders.Add("x-cdp-user-role", "standard");
		client.DefaultRequestHeaders.Add("x-cdp-user-nation", "Wales");

		var response = await client.GetAsync("/cases", cancellationToken);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task Role_and_nation_trust_headers_absent_still_authenticates()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var factory = new BareFactory();
		factory
			.MockPersistence.QueryAsync(Arg.Any<CaseQuery>(), Arg.Any<CancellationToken>())
			.Returns(new CasePage(Array.Empty<Case>(), 0, 1, 20));

		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, ClientId);

		var response = await client.GetAsync("/cases", cancellationToken);

		// Authenticated (not 401); case reads then need a nation claim.
		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}
}

/// <summary>
/// Proves AUTH_API_KEY__MANAGEMENT_FE / AUTH_API_KEY__CASE_INTAKE actually
/// bind through the REAL EnvironmentVariablesConfigurationProvider (which
/// rewrites "__" to ":" while loading), not just through a hand-built
/// config-key dictionary. Reading them with the literal double-underscore
/// string as the GetValue key silently never matches — ApiKeys would
/// resolve empty and the service would 401 every authenticated request in
/// any real (env-var-driven) deployment. The
/// other tests in ClientIdAuthenticationTests use
/// PostConfigure/config-key overrides that bypass this provider entirely,
/// so none of them would have caught that. Mutates process-global env
/// vars, hence the disabled-parallelization collection (same reasoning as
/// NotifyApiKeyEnvVarRegistrationTests).
/// </summary>
[Collection(WasteRegulatorCaseManagement.Test.Notifications.EnvVarMutationCollection.Name)]
public class ClientIdAuthenticationEnvVarTests
{
	private static readonly string ManagementFeApiKey = Guid.NewGuid().ToString();
	private static readonly string CaseIntakeApiKey = Guid.NewGuid().ToString();

	[Fact]
	public async Task ApiKeys_bind_from_real_double_underscore_environment_variables()
	{
		var previousFe = Environment.GetEnvironmentVariable("AUTH_API_KEY__MANAGEMENT_FE");
		var previousCaseIntake = Environment.GetEnvironmentVariable("AUTH_API_KEY__CASE_INTAKE");
		try
		{
			Environment.SetEnvironmentVariable(
				"AUTH_API_KEY__MANAGEMENT_FE",
				ManagementFeApiKey
			);
			Environment.SetEnvironmentVariable("AUTH_API_KEY__CASE_INTAKE", CaseIntakeApiKey);

			// No configOverrides/apiKeys bypass here — this factory boots
			// exactly as a real deployment would. environment MUST be
			// non-Development: if BuildApiKeys silently resolved ApiKeys
			// empty, a Development host would fall back to header-trust
			// mode and return 200 regardless. Production forces the
			// fail-closed branch, so 200 here is only possible if the key
			// genuinely bound.
			await using var factory = new BareFactory(environment: "Production");
			factory
				.MockPersistence.QueryAsync(Arg.Any<CaseQuery>(), Arg.Any<CancellationToken>())
				.Returns(new CasePage(Array.Empty<Case>(), 0, 1, 20));

			using var client = factory.CreateClient();
			// Default expected clientId for the ManagementFe slot (Program.cs).
			client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, "frontend");
			client.DefaultRequestHeaders.Add("x-cdp-user-nation", "ENGLAND");
			client.DefaultRequestHeaders.Add("x-cdp-auth-key", ManagementFeApiKey);

			var response = await client.GetAsync(
				"/cases",
				TestContext.Current.CancellationToken
			);

			response.StatusCode.Should().Be(HttpStatusCode.OK);
		}
		finally
		{
			Environment.SetEnvironmentVariable("AUTH_API_KEY__MANAGEMENT_FE", previousFe);
			Environment.SetEnvironmentVariable("AUTH_API_KEY__CASE_INTAKE", previousCaseIntake);
		}
	}
}

internal sealed class BareFactory(
	IReadOnlyDictionary<string, string>? apiKeys = null,
	string? environment = null,
	IReadOnlyDictionary<string, string?>? configOverrides = null
) : WebApplicationFactory<Program>
{
	public readonly ICasePersistence MockPersistence = Substitute.For<ICasePersistence>();

	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		if (environment is not null)
		{
			builder.UseEnvironment(environment);
		}
		if (configOverrides is not null)
		{
			// Config-KEY overrides (colon form) — exercises
			// BuildApiKeys' binding logic, but NOT the real
			// EnvironmentVariablesConfigurationProvider "__" -> ":"
			// rewrite itself. See
			// ClientIdAuthenticationEnvVarTests for that.
			builder.ConfigureAppConfiguration(
				(_, config) => config.AddInMemoryCollection(configOverrides)
			);
		}
		builder.ConfigureServices(services =>
		{
			services.UseCaseModuleConfigs();
			services.RemoveAll<ICasePersistence>();
			services.AddSingleton(MockPersistence);

			if (apiKeys is not null)
			{
				// Set the resolved option directly (after Program.cs's
				// own env-var-driven Configure<> runs) — the test
				// client ids don't necessarily match the real
				// management-fe/case intake clientId defaults, so we
				// bypass env-var binding entirely here.
				services.PostConfigure<ClientIdAuthenticationOptions>(
					ClientIdDefaults.AuthenticationScheme,
					o => o.ApiKeys = apiKeys
				);
			}
		});
	}
}
