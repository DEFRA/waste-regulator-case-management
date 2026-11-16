using WasteRegulatorCaseManagement.Notifications;
using Microsoft.Extensions.Configuration;
using AwesomeAssertions.Execution;

namespace WasteRegulatorCaseManagement.Test.Notifications;

public class NotifyConfigTests
{
	[Fact]
	public void Binds_RegionToReplyToId_and_DefaultReplyToId_from_configuration()
	{
		var config = new ConfigurationBuilder()
			.AddInMemoryCollection(
				new Dictionary<string, string?>
				{
					["Notify:RegionToReplyToId:England"] = "reply-to-england",
					["Notify:RegionToReplyToId:Wales"] = "reply-to-wales",
					["Notify:DefaultReplyToId"] = "reply-to-default",
				}
			)
			.Build();

		var notifyConfig = new NotifyConfig();
		config.GetSection("Notify").Bind(notifyConfig);

		using var scope = new AssertionScope();
		notifyConfig.RegionToReplyToId.Should().Contain("England", "reply-to-england");
		notifyConfig.RegionToReplyToId.Should().Contain("Wales", "reply-to-wales");
		notifyConfig.DefaultReplyToId.Should().Be("reply-to-default");
	}

	// RA102-okg: proves the mechanism that lets a real Notify GUID be
	// dropped into config with zero code changes once the template exists.
	[Fact]
	public void Binds_a_Queried_template_key_when_configured()
	{
		var config = new ConfigurationBuilder()
			.AddInMemoryCollection(
				new Dictionary<string, string?>
				{
					["Notify:Templates:Queried"] = "11111111-1111-1111-1111-111111111111",
				}
			)
			.Build();

		var notifyConfig = new NotifyConfig();
		config.GetSection("Notify").Bind(notifyConfig);

		notifyConfig.Templates.Should().Contain("Queried", "11111111-1111-1111-1111-111111111111");
	}

	[Fact]
	public void GetReplyToId_resolves_a_configured_region_case_insensitively()
	{
		var cfg = new NotifyConfig();
		cfg.RegionToReplyToId["England"] = "reply-to-england";

		using var scope = new AssertionScope();
		cfg.GetReplyToId("england").Should().Be("reply-to-england");
		cfg.GetReplyToId("ENGLAND").Should().Be("reply-to-england");
	}

	[Fact]
	public void GetReplyToId_falls_back_to_default_for_an_unconfigured_region()
	{
		var cfg = new NotifyConfig { DefaultReplyToId = "fallback-id" };
		cfg.RegionToReplyToId["England"] = "reply-to-england";

		cfg.GetReplyToId("Wales").Should().Be("fallback-id");
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void GetReplyToId_falls_back_to_default_for_a_missing_or_blank_region(string? region)
	{
		var cfg = new NotifyConfig { DefaultReplyToId = "fallback-id" };

		cfg.GetReplyToId(region).Should().Be("fallback-id");
	}

	[Fact]
	public void GetReplyToId_returns_null_rather_than_throwing_when_nothing_is_configured()
	{
		var cfg = new NotifyConfig();

		var exception = Record.Exception(() => cfg.GetReplyToId("Wales"));

		using var scope = new AssertionScope();
		exception.Should().BeNull();
		cfg.GetReplyToId("Wales").Should().BeNull();
	}

	// Guards against a JSON key typo in the shipped config silently failing
	// to bind (Options binding does not throw on unknown/missing keys, so a
	// rename here would otherwise go unnoticed until a real send).
	[Fact]
	public void Shipped_appsettings_json_declares_the_new_config_keys_pending_Defra_decision()
	{
		var config = new ConfigurationBuilder()
			.AddJsonFile(LocateAppSettings(), optional: false)
			.Build();

		var notifyConfig = new NotifyConfig();
		config.GetSection("Notify").Bind(notifyConfig);

		// Deliberately empty until Defra confirms shared vs. per-region
		// sender addresses (RA-211) — see NotifyConfig.RegionToReplyToId doc.
		using var scope = new AssertionScope();
		notifyConfig.RegionToReplyToId.Should().BeEmpty();
		notifyConfig.DefaultReplyToId.Should().BeNull();
	}

	// Real regulator inboxes are set per environment in cdp-app-config (prod's
	// only in prod config). A non-blank default here would be the fallback
	// for any deployed environment that leaves a nation unset, so the shipped
	// file must resolve no mailbox for any nation.
	[Fact]
	public void Shipped_appsettings_json_resolves_no_regulator_mailbox_for_any_nation()
	{
		var config = new ConfigurationBuilder()
			.AddJsonFile(LocateAppSettings(), optional: false)
			.Build();

		var notifyConfig = new NotifyConfig();
		config.GetSection("Notify").Bind(notifyConfig);

		// Keys stay declared so the shape is discoverable in the file.
		using var scope = new AssertionScope();
		notifyConfig.RegulatorMailboxes.Should().NotBeEmpty();
		notifyConfig.RegulatorMailboxes.Should().AllSatisfy(mailbox =>
			string.IsNullOrWhiteSpace(mailbox.Value)
				.Should()
				.BeTrue($"Notify:RegulatorMailboxes:{mailbox.Key} must be blank in appsettings.json")
		);
	}

	private static string LocateAppSettings()
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir is not null && dir.GetFiles("WasteRegulatorCaseManagement.sln").Length == 0)
		{
			dir = dir.Parent;
		}

		if (dir is null)
		{
			throw new InvalidOperationException(
				"Could not locate WasteRegulatorCaseManagement.sln above "
					+ $"'{AppContext.BaseDirectory}' to resolve appsettings.json."
			);
		}

		return Path.Combine(dir.FullName, "WasteRegulatorCaseManagement", "appsettings.json");
	}
}
