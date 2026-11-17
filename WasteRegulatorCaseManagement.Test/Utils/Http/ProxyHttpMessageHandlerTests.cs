using System.Net;
using AwesomeAssertions.Execution;
using WasteRegulatorCaseManagement.Utils.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace WasteRegulatorCaseManagement.Test.Utils.Http;

/// <summary>
/// HTTP_PROXY env vars routinely embed user:pass@ credentials.
/// The handler's WebProxy.Address must never carry that user-info or it
/// will surface in any ToString() / serialization / accidental log of
/// the handler state. These tests pin the strip-and-relocate contract
/// of <see cref="ProxyHttpMessageHandler.ParseProxyUri"/>.
/// </summary>
public class ProxyHttpMessageHandlerTests
{
	[Fact]
	public void Credentialed_uri_strips_user_info_off_address()
	{
		var (address, credentials) = ProxyHttpMessageHandler.ParseProxyUri(
			"http://alice:s3cret@proxy.local:3128");

		credentials.Should().NotBeNull();

		using var scope = new AssertionScope();
		address.Should().BeEquivalentTo(new
		{
			UserInfo = "",
			Host = "proxy.local",
			Port = 3128,
		});
		address.ToString().Should().NotContain("alice").And.NotContain("s3cret");

		credentials!.UserName.Should().Be("alice");
		credentials.Password.Should().Be("s3cret");
	}

	[Fact]
	public void Credentialed_uri_unescapes_percent_encoded_special_chars()
	{
		// Real-world: "p@ss/w:rd" needs encoding when stuffed into a URI.
		// The NetworkCredential consumer needs the decoded form.
		var (address, credentials) = ProxyHttpMessageHandler.ParseProxyUri(
			"http://alice%40corp:p%40ss%2Fw%3Ard@proxy.local:3128");

		credentials.Should().NotBeNull();

		using var scope = new AssertionScope();
		address.UserInfo.Should().BeEmpty();
		credentials!.UserName.Should().Be("alice@corp");
		credentials.Password.Should().Be("p@ss/w:rd");
	}

	[Fact]
	public void Uri_without_credentials_round_trips_without_a_NetworkCredential()
	{
		var (address, credentials) = ProxyHttpMessageHandler.ParseProxyUri(
			"http://proxy.local:3128");

		using var scope = new AssertionScope();
		address.Should().BeEquivalentTo(new
		{
			UserInfo = "",
			Host = "proxy.local",
			Port = 3128,
		});
		credentials.Should().BeNull();
	}

	[Fact]
	public void Constructor_with_credentialed_uri_sets_address_without_user_info_and_keeps_credentials_separate()
	{
		// Drives the production constructor path through the internal
		// test seam so we exercise WebProxy assignment, not just the
		// parser. WebProxy is not a sealed seam — the assertion is that
		// the resulting Proxy.Address has no UserInfo, regardless of
		// what was passed in.
		using var handler = new ProxyHttpMessageHandler(
			Microsoft.Extensions.Logging.Abstractions.NullLogger<ProxyHttpMessageHandler>.Instance,
			"http://alice:s3cret@proxy.local:3128");

		var webProxy = handler.Proxy.Should().BeOfType<WebProxy>().Subject;
		webProxy.Address.Should().NotBeNull();
		var credentials = webProxy.Credentials.Should().BeOfType<NetworkCredential>().Subject;

		using var scope = new AssertionScope();
		webProxy.Address!.UserInfo.Should().BeEmpty();
		webProxy.Address.ToString().Should().NotContain("s3cret");

		credentials.UserName.Should().Be("alice");
		credentials.Password.Should().Be("s3cret");
		handler.UseProxy.Should().BeTrue();
	}

	[Fact]
	public void Constructor_with_no_proxy_uri_disables_proxy()
	{
		using var handler = new ProxyHttpMessageHandler(
			Microsoft.Extensions.Logging.Abstractions.NullLogger<ProxyHttpMessageHandler>.Instance,
			proxyUri: null);

		handler.UseProxy.Should().BeFalse();
	}

	// ---------------------- HTTPS_PROXY / NO_PROXY ----------------------

	[Fact]
	public void ResolveProxyEnvironmentValue_prefers_HTTPS_PROXY_over_HTTP_PROXY()
	{
		// Drive the resolver via process env vars set inside the test
		// and torn down on exit. Outbound traffic is HTTPS, so when
		// both are set HTTPS_PROXY must win.
		// On Windows env vars are case-insensitive, so null-clearings must
		// come before value-settings so the last write wins.
		using var _h2 = new ScopedEnvironmentVariable("https_proxy", null);
		using var _l2 = new ScopedEnvironmentVariable("http_proxy", null);
		using var _h = new ScopedEnvironmentVariable("HTTPS_PROXY", "http://https-proxy.local:8443");
		using var _l = new ScopedEnvironmentVariable("HTTP_PROXY", "http://http-proxy.local:3128");

		ProxyHttpMessageHandler.ResolveProxyEnvironmentValue().Should().Be("http://https-proxy.local:8443");
	}

	[Fact]
	public void ResolveProxyEnvironmentValue_falls_back_to_HTTP_PROXY_when_HTTPS_PROXY_absent()
	{
		using var _h = new ScopedEnvironmentVariable("HTTPS_PROXY", null);
		using var _h2 = new ScopedEnvironmentVariable("https_proxy", null);
		using var _l2 = new ScopedEnvironmentVariable("http_proxy", null);
		using var _l = new ScopedEnvironmentVariable("HTTP_PROXY", "http://http-proxy.local:3128");

		ProxyHttpMessageHandler.ResolveProxyEnvironmentValue().Should().Be("http://http-proxy.local:3128");
	}

	[Fact]
	public void ResolveProxyEnvironmentValue_honours_lower_case_variants()
	{
		// Several CDP base images / shells set the lower-case form. The
		// resolver must accept either case.
		using var _h = new ScopedEnvironmentVariable("HTTPS_PROXY", null);
		using var _h2 = new ScopedEnvironmentVariable("https_proxy", "http://lower-https.local:8443");
		using var _l = new ScopedEnvironmentVariable("HTTP_PROXY", null);
		using var _l2 = new ScopedEnvironmentVariable("http_proxy", null);

		ProxyHttpMessageHandler.ResolveProxyEnvironmentValue().Should().Be("http://lower-https.local:8443");
	}

	[Fact]
	public void ResolveProxyEnvironmentValue_returns_null_when_nothing_set()
	{
		using var _h = new ScopedEnvironmentVariable("HTTPS_PROXY", null);
		using var _h2 = new ScopedEnvironmentVariable("https_proxy", null);
		using var _l = new ScopedEnvironmentVariable("HTTP_PROXY", null);
		using var _l2 = new ScopedEnvironmentVariable("http_proxy", null);

		ProxyHttpMessageHandler.ResolveProxyEnvironmentValue().Should().BeNull();
	}

	[Fact]
	public void ResolveProxyEnvironmentValue_treats_empty_value_as_unset()
	{
		// A bare `export HTTPS_PROXY=` shouldn't override a real
		// HTTP_PROXY value or wedge UseProxy=true with an empty URI.
		using var _h2 = new ScopedEnvironmentVariable("https_proxy", null);
		using var _l2 = new ScopedEnvironmentVariable("http_proxy", null);
		using var _h = new ScopedEnvironmentVariable("HTTPS_PROXY", "");
		using var _l = new ScopedEnvironmentVariable("HTTP_PROXY", "http://http-proxy.local:3128");

		ProxyHttpMessageHandler.ResolveProxyEnvironmentValue().Should().Be("http://http-proxy.local:3128");
	}

	[Fact]
	public void NoProxy_entries_are_translated_into_a_bypass_list()
	{
		using var handler = new ProxyHttpMessageHandler(
			Microsoft.Extensions.Logging.Abstractions.NullLogger<ProxyHttpMessageHandler>.Instance,
			"http://proxy.local:3128",
			noProxy: "internal.example, .corp.example, localhost");

		var webProxy = handler.Proxy.Should().BeOfType<WebProxy>().Subject;
		// BypassList is built; concrete pattern strings are an
		// implementation detail, but the bypass behaviour is observable
		// via WebProxy.IsBypassed.
		using var scope = new AssertionScope();
		webProxy.IsBypassed(new Uri("https://api.internal.example/v1")).Should().BeTrue();
		webProxy.IsBypassed(new Uri("https://corp.example/")).Should().BeTrue();
		webProxy.IsBypassed(new Uri("https://anything.corp.example/")).Should().BeTrue();
		webProxy.IsBypassed(new Uri("https://api.elsewhere.example/")).Should().BeFalse();
	}

	[Fact]
	public void NoProxy_wildcard_bypasses_everything()
	{
		using var handler = new ProxyHttpMessageHandler(
			Microsoft.Extensions.Logging.Abstractions.NullLogger<ProxyHttpMessageHandler>.Instance,
			"http://proxy.local:3128",
			noProxy: "*");

		var webProxy = handler.Proxy.Should().BeOfType<WebProxy>().Subject;
		using var scope = new AssertionScope();
		webProxy.IsBypassed(new Uri("https://api.elsewhere.example/")).Should().BeTrue();
		webProxy.IsBypassed(new Uri("https://internal.corp/")).Should().BeTrue();
	}

	[Fact]
	public void NoProxy_unset_leaves_default_bypass_behaviour_intact()
	{
		using var handler = new ProxyHttpMessageHandler(
			Microsoft.Extensions.Logging.Abstractions.NullLogger<ProxyHttpMessageHandler>.Instance,
			"http://proxy.local:3128",
			noProxy: null);

		var webProxy = handler.Proxy.Should().BeOfType<WebProxy>().Subject;
		webProxy.IsBypassed(new Uri("https://api.elsewhere.example/")).Should().BeFalse();
	}

	// ---------------------- fail-closed in non-Development ----------------------
	//
	// docs/cdp-deployment.md requires HTTP(S)_PROXY for any external HTTP
	// call outside Development. The historical behaviour was warn-and-
	// continue with UseProxy=false; that meant a misconfigured deployment
	// would silently bypass the Squid proxy and egress outside the
	// controlled network the first time a real downstream HTTP client
	// was registered. Production / Test / Staging must now refuse to
	// construct the handler without a proxy URI; Development keeps the
	// warn-only path so local runs work without proxy infrastructure.

	[Fact]
	public void Production_constructor_throws_when_no_proxy_env_vars_are_set()
	{
		using var _h = new ScopedEnvironmentVariable("HTTPS_PROXY", null);
		using var _h2 = new ScopedEnvironmentVariable("https_proxy", null);
		using var _l = new ScopedEnvironmentVariable("HTTP_PROXY", null);
		using var _l2 = new ScopedEnvironmentVariable("http_proxy", null);
		var env = HostEnvironment("Production");

		var act = () =>
			new ProxyHttpMessageHandler(NullLogger<ProxyHttpMessageHandler>.Instance, env);
		var ex = act.Should().ThrowExactly<InvalidOperationException>().Which;

		// Message must name the missing env vars and the offending
		// environment so the failure is actionable in CDP logs.
		using var scope = new AssertionScope();
		ex.Message.Should().Contain("HTTPS_PROXY");
		ex.Message.Should().Contain("HTTP_PROXY");
		ex.Message.Should().Contain("Production");
	}

	[Theory]
	[InlineData("Staging")]
	[InlineData("Test")]
	[InlineData("Performance")]
	public void Non_development_environments_all_fail_closed(string envName)
	{
		// Only Development is whitelisted. Any other environment name —
		// including custom CDP environments — must fail closed so a
		// typo (e.g. "Develop", "develpoment") doesn't accidentally
		// unlock direct egress.
		using var _h = new ScopedEnvironmentVariable("HTTPS_PROXY", null);
		using var _h2 = new ScopedEnvironmentVariable("https_proxy", null);
		using var _l = new ScopedEnvironmentVariable("HTTP_PROXY", null);
		using var _l2 = new ScopedEnvironmentVariable("http_proxy", null);
		var env = HostEnvironment(envName);

		var act = () =>
			new ProxyHttpMessageHandler(NullLogger<ProxyHttpMessageHandler>.Instance, env);
		act.Should().ThrowExactly<InvalidOperationException>();
	}

	[Fact]
	public void Development_constructor_warns_and_disables_proxy_when_env_vars_unset()
	{
		// Local-dev contract: no proxy infrastructure required.
		using var _h = new ScopedEnvironmentVariable("HTTPS_PROXY", null);
		using var _h2 = new ScopedEnvironmentVariable("https_proxy", null);
		using var _l = new ScopedEnvironmentVariable("HTTP_PROXY", null);
		using var _l2 = new ScopedEnvironmentVariable("http_proxy", null);
		var env = HostEnvironment("Development");

		using var handler = new ProxyHttpMessageHandler(
			NullLogger<ProxyHttpMessageHandler>.Instance, env);

		handler.UseProxy.Should().BeFalse();
	}

	[Fact]
	public void Production_constructor_succeeds_when_HTTPS_PROXY_is_set()
	{
		using var _h2 = new ScopedEnvironmentVariable("https_proxy", null);
		using var _l = new ScopedEnvironmentVariable("HTTP_PROXY", null);
		using var _l2 = new ScopedEnvironmentVariable("http_proxy", null);
		using var _h = new ScopedEnvironmentVariable("HTTPS_PROXY", "http://proxy.local:3128");
		var env = HostEnvironment("Production");

		using var handler = new ProxyHttpMessageHandler(
			NullLogger<ProxyHttpMessageHandler>.Instance, env);

		handler.UseProxy.Should().BeTrue();
		var webProxy = handler.Proxy.Should().BeOfType<WebProxy>().Subject;
		webProxy.Address!.Host.Should().Be("proxy.local");
	}

	private static IHostEnvironment HostEnvironment(string envName)
	{
		var env = Substitute.For<IHostEnvironment>();
		env.EnvironmentName.Returns(envName);
		return env;
	}

	// ---------------------- malformed proxy URI redaction ----------------------

	[Fact]
	public void ParseProxyUri_throws_redacted_when_uri_is_malformed_with_credentials()
	{
		// Driving the parser with a value that has user:pass@ but is
		// syntactically malformed (illegal port). The baseline
		// behaviour propagated UriBuilder's UriFormatException whose
		// Message embedded the original input verbatim — putting the
		// password into Serilog's startup-failure log. The contract
		// under test: neither the username nor the password appears
		// anywhere in the resulting exception chain.
		const string user = "alice";
		const string secret = "s3cret";
		var malformed = $"http://{user}:{secret}@proxy.local:port-not-a-number";

		var act = () => ProxyHttpMessageHandler.ParseProxyUri(malformed);
		var ex = act.Should().ThrowExactly<InvalidOperationException>().Which;

		AssertNoCredentialLeak(ex, user, secret);
	}

	[Fact]
	public void RedactProxyUri_replaces_user_info_with_asterisks()
	{
		ProxyHttpMessageHandler.RedactProxyUri("http://alice:s3cret@proxy.local:3128/path")
			.Should().Be("http://***:***@proxy.local:3128/path");
	}

	[Fact]
	public void RedactProxyUri_leaves_uri_without_credentials_unchanged()
	{
		ProxyHttpMessageHandler.RedactProxyUri("http://proxy.local:3128/")
			.Should().Be("http://proxy.local:3128/");
	}

	[Fact]
	public void RedactProxyUri_does_not_treat_at_in_path_as_user_info()
	{
		// The '@' here is in the path, not in the authority. The
		// redactor must not over-redact and turn this into "***:***@".
		ProxyHttpMessageHandler.RedactProxyUri("http://proxy.local:3128/dir@thing")
			.Should().Be("http://proxy.local:3128/dir@thing");
	}

	private static void AssertNoCredentialLeak(Exception ex, string user, string secret)
	{
		for (var current = ex; current is not null; current = current.InnerException)
		{
			current.Message.Should().NotContain(secret);
			current.Message.Should().NotContain(user);
			if (current.StackTrace is { } stack)
			{
				stack.Should().NotContain(secret);
			}
		}
	}

	// ---------------------- BuildDefaultProxy: HttpClient.DefaultProxy wiring ----------------------
	//
	// The GovukNotify SDK constructs its own bare HttpClient and so
	// depends on HttpClient.DefaultProxy. These tests pin the contract
	// of the BuildDefaultProxy builder Program.cs assigns to that
	// static at startup. The test seam takes explicit (proxyUri,
	// noProxy) arguments so we don't have to mutate process env vars
	// and risk leaking into HttpClient.DefaultProxy itself.

	[Fact]
	public void BuildDefaultProxy_returns_null_when_no_proxy_uri_provided()
	{
		using var scope = new AssertionScope();
		ProxyHttpMessageHandler.BuildDefaultProxy(proxyUri: null, noProxy: null).Should().BeNull();
		ProxyHttpMessageHandler.BuildDefaultProxy(proxyUri: "", noProxy: null).Should().BeNull();
		ProxyHttpMessageHandler.BuildDefaultProxy(proxyUri: "   ", noProxy: null).Should().BeNull();
	}

	[Fact]
	public void BuildDefaultProxy_returns_WebProxy_pointing_at_the_supplied_address()
	{
		var proxy = ProxyHttpMessageHandler.BuildDefaultProxy(
			proxyUri: "http://proxy.local:3128", noProxy: null);

		var webProxy = proxy.Should().BeOfType<WebProxy>().Subject;
		webProxy.Address.Should().NotBeNull();

		using var scope = new AssertionScope();
		webProxy.Address!.Should().BeEquivalentTo(new
		{
			Host = "proxy.local",
			Port = 3128,
		});
		webProxy.BypassProxyOnLocal.Should().BeTrue();
	}

	[Fact]
	public void BuildDefaultProxy_strips_credentials_off_address_but_keeps_them_separate()
	{
		// Same contract as the per-client handler: a stray ToString() /
		// log of HttpClient.DefaultProxy.Address must not leak the
		// password baked into the env-var.
		var proxy = ProxyHttpMessageHandler.BuildDefaultProxy(
			proxyUri: "http://alice:s3cret@proxy.local:3128", noProxy: null);

		var webProxy = proxy.Should().BeOfType<WebProxy>().Subject;
		webProxy.Address.Should().NotBeNull();
		var credentials = webProxy.Credentials.Should().BeOfType<NetworkCredential>().Subject;

		using var scope = new AssertionScope();
		webProxy.Address!.UserInfo.Should().BeEmpty();
		webProxy.Address.ToString().Should().NotContain("s3cret");

		credentials.UserName.Should().Be("alice");
		credentials.Password.Should().Be("s3cret");
	}

	[Fact]
	public void BuildDefaultProxy_applies_no_proxy_bypass_list()
	{
		var proxy = ProxyHttpMessageHandler.BuildDefaultProxy(
			proxyUri: "http://proxy.local:3128",
			noProxy: "internal.example, .corp.example");

		var webProxy = proxy.Should().BeOfType<WebProxy>().Subject;
		using var scope = new AssertionScope();
		webProxy.IsBypassed(new Uri("https://api.internal.example/v1")).Should().BeTrue();
		webProxy.IsBypassed(new Uri("https://anything.corp.example/")).Should().BeTrue();
		webProxy.IsBypassed(new Uri("https://api.notifications.service.gov.uk/")).Should().BeFalse();
	}

	[Fact]
	public void BuildDefaultProxy_with_no_no_proxy_leaves_default_bypass_intact()
	{
		var proxy = ProxyHttpMessageHandler.BuildDefaultProxy(
			proxyUri: "http://proxy.local:3128", noProxy: null);

		var webProxy = proxy.Should().BeOfType<WebProxy>().Subject;
		webProxy.IsBypassed(new Uri("https://api.notifications.service.gov.uk/")).Should().BeFalse();
	}

	[Fact]
	public void BuildDefaultProxy_from_environment_reads_HTTPS_PROXY_and_NO_PROXY()
	{
		using var _h2 = new ScopedEnvironmentVariable("https_proxy", null);
		using var _l = new ScopedEnvironmentVariable("HTTP_PROXY", null);
		using var _l2 = new ScopedEnvironmentVariable("http_proxy", null);
		using var _np2 = new ScopedEnvironmentVariable("no_proxy", null);
		using var _h = new ScopedEnvironmentVariable("HTTPS_PROXY", "http://proxy.local:3128");
		using var _np = new ScopedEnvironmentVariable("NO_PROXY", "internal.example");

		var proxy = ProxyHttpMessageHandler.BuildDefaultProxy();

		var webProxy = proxy.Should().BeOfType<WebProxy>().Subject;
		webProxy.Address.Should().NotBeNull();

		using var scope = new AssertionScope();
		webProxy.Address!.Host.Should().Be("proxy.local");
		webProxy.IsBypassed(new Uri("https://internal.example/")).Should().BeTrue();
	}

	[Fact]
	public void BuildDefaultProxy_from_environment_returns_null_when_nothing_set()
	{
		using var _h = new ScopedEnvironmentVariable("HTTPS_PROXY", null);
		using var _h2 = new ScopedEnvironmentVariable("https_proxy", null);
		using var _l = new ScopedEnvironmentVariable("HTTP_PROXY", null);
		using var _l2 = new ScopedEnvironmentVariable("http_proxy", null);

		ProxyHttpMessageHandler.BuildDefaultProxy().Should().BeNull();
	}
}

/// <summary>
/// Test helper: scope an environment-variable change to a `using` block
/// so concurrent / subsequent tests don't observe leaked state.
/// </summary>
internal sealed class ScopedEnvironmentVariable : IDisposable
{
	private readonly string _name;
	private readonly string? _previous;

	public ScopedEnvironmentVariable(string name, string? value)
	{
		_name = name;
		_previous = Environment.GetEnvironmentVariable(name);
		Environment.SetEnvironmentVariable(name, value);
	}

	public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
}
