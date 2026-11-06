using WasteRegulatorCaseManagement.Notifications;
using WasteRegulatorCaseManagement.Test.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace WasteRegulatorCaseManagement.Test.Notifications;

internal static class NotifyTestConstants
{
	// The Notify SDK's NotificationClient constructor extracts a service-id
	// and API-key via Substring(len - 73, 36) and Substring(len - 36), so
	// the key must be >= 73 chars. The value below is intentionally not in
	// real key format so secret scanners do not flag it.
	internal const string FakeApiKey =
		"not-a-real-notify-key-for-unit-tests-only-do-not-use-aaaaaaaaaaaaaaaaaaaaaaaaa";
}

/// <summary>
/// RA-422: the central <c>Notify:Enabled</c> flag (default false) gates the
/// outbound <see cref="INotifyClient"/>. These tests pin the wiring matrix
/// (flag × API-key) for the registered client, so disabling the flag can
/// never leak an email.
///
/// Config-key tests use UseSetting and are safe for parallel execution.
/// Env-var tests mutate the process environment and are placed in the
/// env-var-mutation collection (DisableParallelization = true) to prevent
/// races with other WebApplicationFactory spin-ups.
/// </summary>
public class NotifyApiKeyRegistrationTests
{
	private readonly MongoIntegrationFixture _fixture;

	public NotifyApiKeyRegistrationTests(MongoIntegrationFixture fixture) => _fixture = fixture;

	[Fact]
	public void Default_flag_absent_registers_noop_client()
	{
		// No Notify:Enabled override at all: the shipped appsettings default
		// (false) applies, so even a configured API key yields the no-op
		// client.
		using var factory = new EphemeralMongoTestFactory(
			_fixture,
			"notify-default",
			settings: new Dictionary<string, string?>
			{
				["NOTIFY_API_KEY"] = NotifyTestConstants.FakeApiKey,
			});

		Assert.IsType<NoOpNotifyClient>(factory.Services.GetRequiredService<INotifyClient>());
	}

	[Fact]
	public void Explicitly_disabled_registers_noop_client()
	{
		using var factory = NewFactory(NotifyTestConstants.FakeApiKey, enabled: false);

		Assert.IsType<NoOpNotifyClient>(factory.Services.GetRequiredService<INotifyClient>());
	}

	[Fact]
	public void Enabled_with_api_key_registers_govuk_client()
	{
		using var factory = NewFactory(NotifyTestConstants.FakeApiKey, enabled: true);

		Assert.IsType<GovukNotifyClient>(factory.Services.GetRequiredService<INotifyClient>());
	}

	[Fact]
	public void Enabled_without_api_key_registers_noop_client()
	{
		using var factory = NewFactory(apiKey: null, enabled: true);

		Assert.IsType<NoOpNotifyClient>(factory.Services.GetRequiredService<INotifyClient>());
	}

	// Always set NOTIFY_API_KEY and Notify:Enabled explicitly so an ambient
	// value in the test runner's environment cannot influence the outcome.
	//
	// NOTIFY_SENDEMAILS=true pins NotifySendingPolicy open. Without it the
	// enabled+API-key case would assert the dev/localhost environment gate
	// (the test host defaults to the Development environment) instead of the
	// flag×API-key matrix it is about — NotifyEnvironmentGatingTests covers
	// the gate itself.
	private EphemeralMongoTestFactory NewFactory(string? apiKey, bool enabled) =>
		new(_fixture, "notify-key", settings: new Dictionary<string, string?>
		{
			["NOTIFY_API_KEY"] = apiKey ?? string.Empty,
			["Notify:Enabled"] = enabled ? "true" : "false",
			[NotifySendingPolicy.SendEmailsKey] = "true",
		});
}

/// <summary>
/// Collection that disables parallelization for tests that mutate
/// process-global environment variables.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvVarMutationCollection
{
	public const string Name = "env-var-mutation";
}

/// <summary>
/// Verifies that the NOTIFY_API_KEY environment variable is picked up by
/// Program.cs and drives the same INotifyClient registration decision. With
/// the RA-422 flag on, the key dimension alone selects the client.
/// </summary>
[Collection(EnvVarMutationCollection.Name)]
public class NotifyApiKeyEnvVarRegistrationTests
{
	private readonly MongoIntegrationFixture _fixture;

	public NotifyApiKeyEnvVarRegistrationTests(MongoIntegrationFixture fixture) => _fixture = fixture;

	[Fact]
	public void GovukNotifyClient_is_registered_when_NOTIFY_API_KEY_env_var_is_set()
	{
		var previous = Environment.GetEnvironmentVariable("NOTIFY_API_KEY");
		try
		{
			Environment.SetEnvironmentVariable("NOTIFY_API_KEY", NotifyTestConstants.FakeApiKey);

			using var factory = EnabledFactory("notify-key-env");
			Assert.IsType<GovukNotifyClient>(factory.Services.GetRequiredService<INotifyClient>());
		}
		finally
		{
			Environment.SetEnvironmentVariable("NOTIFY_API_KEY", previous);
		}
	}

	[Fact]
	public void NoOpNotifyClient_is_registered_when_NOTIFY_API_KEY_env_var_is_absent()
	{
		var previous = Environment.GetEnvironmentVariable("NOTIFY_API_KEY");
		try
		{
			Environment.SetEnvironmentVariable("NOTIFY_API_KEY", null);

			using var factory = EnabledFactory("notify-key-env");
			Assert.IsType<NoOpNotifyClient>(factory.Services.GetRequiredService<INotifyClient>());
		}
		finally
		{
			Environment.SetEnvironmentVariable("NOTIFY_API_KEY", previous);
		}
	}

	// Notifications enabled so the key dimension alone drives the decision;
	// the RA-422 flag gate is covered by NotifyApiKeyRegistrationTests.
	// NOTIFY_SENDEMAILS=true pins NotifySendingPolicy open so the environment
	// gate (test host defaults to Development) does not shadow the API-key
	// branch under test — NotifyEnvironmentGatingTests covers the gate itself.
	private EphemeralMongoTestFactory EnabledFactory(string prefix) =>
		new(_fixture, prefix, settings: new Dictionary<string, string?>
		{
			["Notify:Enabled"] = "true",
			[NotifySendingPolicy.SendEmailsKey] = "true",
		});
}
