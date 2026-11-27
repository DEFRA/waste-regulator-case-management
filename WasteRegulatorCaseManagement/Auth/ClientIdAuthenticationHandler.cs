using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WasteRegulatorCaseManagement.Auth;

/// <summary>
/// Authentication handler that accepts a client id supplied in a request
/// header. CDP does not verify this header; the caller (the regulator
/// frontend or the Case Intake Processor) asserts its own client id and
/// proves it with the API key registered for that client id in
/// <see cref="ClientIdAuthenticationOptions.ApiKeys"/> (ADR-0012).
///
/// When no API keys are configured the handler fails CLOSED in any
/// non-Development environment. In Development it falls back to
/// header-trust mode and logs a single warning per process to keep
/// local/BFF-stub workflows ergonomic.
/// </summary>
public class ClientIdAuthenticationHandler(
	IOptionsMonitor<ClientIdAuthenticationOptions> options,
	ILoggerFactory logger,
	UrlEncoder encoder,
	IHostEnvironment hostEnvironment
) : AuthenticationHandler<ClientIdAuthenticationOptions>(options, logger, encoder)
{
	private const string AbsentValue = "(absent)";

	// Tracks whether the Development header-trust downgrade warning has
	// already been emitted in this process. Atomic CAS keeps it to a single
	// log line even under concurrent first requests.
	private static int s_devDowngradeWarned;

	protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
		Task.FromResult(Authenticate());

	private AuthenticateResult Authenticate()
	{
		var headerName = Options.HeaderName;

		if (!Request.Headers.TryGetValue(headerName, out var values))
		{
			return AuthenticateResult.NoResult();
		}

		var clientId = values.ToString();
		if (string.IsNullOrWhiteSpace(clientId))
		{
			return AuthenticateResult.Fail($"Empty {headerName} header");
		}

		// Length caps run BEFORE any further processing. Each cap is
		// reported with a distinct fail reason so misbehaving clients can
		// be diagnosed in logs. We do NOT silently truncate — the user id
		// ends up in audit attribution, where corruption would be worse
		// than a 401.
		if (clientId.Length > Options.MaxClientIdLength)
		{
			return AuthenticateResult.Fail(
				$"{headerName} exceeds {Options.MaxClientIdLength} chars"
			);
		}

		if (
			TryValidateOptionalHeader(
				Options.UserIdHeaderName,
				Options.MaxUserIdLength,
				out var userId
			) is
			{ } userIdFailure
		)
			return userIdFailure;
		if (
			TryValidateOptionalHeader(
				Options.RoleHeaderName,
				Options.MaxUserRoleLength,
				out var role
			) is
			{ } roleFailure
		)
			return roleFailure;
		if (
			TryValidateOptionalHeader(
				Options.NationHeaderName,
				Options.MaxUserNationLength,
				out var nation
			) is
			{ } nationFailure
		)
			return nationFailure;

		if (Options.ApiKeys.Count > 0)
		{
			if (VerifyApiKey(clientId) is { } apiKeyFailure)
			{
				return apiKeyFailure;
			}
		}
		else if (!hostEnvironment.IsDevelopment())
		{
			// Fail CLOSED: no configured API keys outside Development means
			// the contract with the callers is broken (env var typo,
			// misconfigured prod). Trusting the headers in this state would
			// let any caller forge identity.
			Logger.LogCritical(
				"ClientIdAuthentication misconfigured: no ApiKeys are set in environment '{Environment}'. Rejecting request — refusing to fall back to header-trust mode outside Development.",
				hostEnvironment.EnvironmentName
			);
			return AuthenticateResult.Fail("Authentication misconfigured: no API keys set");
		}
		else if (Interlocked.CompareExchange(ref s_devDowngradeWarned, 1, 0) == 0)
		{
			// Development-only ergonomics: BFF stub mode runs without any
			// API keys configured. Emit a single warning per process
			// so the downgrade is visible without spamming the log.
			Logger.LogWarning(
				"ClientIdAuthentication: no ApiKeys configured — operating in header-trust mode. This is allowed only because the host environment is '{Environment}'. Set AUTH_API_KEY__MANAGEMENT_FE / AUTH_API_KEY__CASE_INTAKE in any non-Development deployment.",
				hostEnvironment.EnvironmentName
			);
		}

		var claims = BuildClaims(clientId, userId, role, nation);
		var identity = new ClaimsIdentity(claims, Scheme.Name);
		var principal = new ClaimsPrincipal(identity);
		var ticket = new AuthenticationTicket(principal, Scheme.Name);

		return AuthenticateResult.Success(ticket);
	}

	/// <summary>
	/// Reads one optional trust header (userId/role/nation), guards its
	/// length, and reports the outcome as either a fail result or the
	/// resolved value via <paramref name="value"/> — the shared shape behind
	/// all three optional-header checks in <see cref="Authenticate"/>.
	/// Returns <c>null</c> (with <paramref name="value"/> set) on success;
	/// the header being entirely absent is success with a <c>null</c> value,
	/// not a failure.
	/// </summary>
	private AuthenticateResult? TryValidateOptionalHeader(
		string headerName,
		int maxLength,
		out string? value
	)
	{
		value = null;
		if (!Request.Headers.TryGetValue(headerName, out var raw))
		{
			return null;
		}

		var v = raw.ToString();
		if (v.Length > maxLength)
		{
			return AuthenticateResult.Fail($"{headerName} exceeds {maxLength} chars");
		}

		value = string.IsNullOrWhiteSpace(v) ? null : v;
		return null;
	}

	/// <summary>
	/// Checks the request's API key against the key registered for the
	/// client id it asserts. Returns <c>null</c> on success or the
	/// <see cref="AuthenticateResult.Fail"/> result to return from
	/// <see cref="Authenticate"/>.
	/// </summary>
	private AuthenticateResult? VerifyApiKey(string clientId)
	{
		var apiKeyHeaderName = Options.ApiKeyHeaderName;
		if (!Request.Headers.TryGetValue(apiKeyHeaderName, out var apiKeyValues))
		{
			return AuthenticateResult.Fail($"Missing {apiKeyHeaderName} header");
		}

		var providedApiKey = apiKeyValues.ToString();
		if (providedApiKey.Length > Options.MaxApiKeyLength)
		{
			return AuthenticateResult.Fail(
				$"{apiKeyHeaderName} exceeds {Options.MaxApiKeyLength} chars"
			);
		}

		// An unrecognized client id gets the same failure message as a
		// wrong key, so the response alone can't be used to probe which
		// client ids are known, but is logged distinctly so operators can
		// tell the two apart. The only client ids that could be confirmed
		// this way are the documented defaults, so latency differences
		// between the two paths reveal nothing secret.
		if (!Options.ApiKeys.TryGetValue(clientId, out var expectedApiKey))
		{
			Logger.LogWarning(
				"ClientIdAuthentication: no API key registered for asserted client id {ClientId}",
				clientId
			);
			return AuthenticateResult.Fail($"Invalid {apiKeyHeaderName} header");
		}

		if (!FixedTimeEquals(providedApiKey, expectedApiKey))
		{
			Logger.LogWarning(
				"ClientIdAuthentication: API key mismatch for asserted client id {ClientId}",
				clientId
			);
			return AuthenticateResult.Fail($"Invalid {apiKeyHeaderName} header");
		}

		return null;
	}

	/// <summary>
	/// Builds the claim set for a successfully authenticated request. The
	/// BFF (frontend) forwards the acting user's identity in optional
	/// headers. They are not authenticators in their own right — the API
	/// key is what establishes trust in the primary client id header — but
	/// they let backend endpoints produce more useful timeline lines
	/// without a separate user lookup.
	/// </summary>
	private static List<Claim> BuildClaims(
		string clientId,
		string? userId,
		string? role,
		string? nation
	)
	{
		var claims = new List<Claim>
		{
			new(ClaimTypes.NameIdentifier, clientId),
			new("client_id", clientId),
		};

		if (userId is not null)
			claims.Add(new Claim("user:id", userId));
		if (role is not null)
			claims.Add(new Claim("user:role", role));
		if (nation is not null)
			claims.Add(new Claim("user:nation", nation));

		return claims;
	}

	/// <summary>
	/// Surface the <see cref="AuthenticateResult.Failure"/> message via the
	/// standard <c>WWW-Authenticate</c> challenge so operators (and tests)
	/// can diagnose misbehaving clients without needing log access. Only
	/// the failure message — which is authored entirely server-side — is
	/// echoed; no attacker-supplied bytes leak back into the response.
	/// </summary>
	protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
	{
		// Re-run authenticate to pick up the failure message stored on the
		// request feature; AuthenticationMiddleware does not pass it through.
		var authResult = await Context.AuthenticateAsync(Scheme.Name);
		var failureMessage = authResult.Failure?.Message;

		// Log the auth-relevant headers so operators can diagnose 401s
		// without needing to correlate with the caller's own logs. The API
		// key is a credential, so only its presence is logged.
		Logger.LogWarning(
			"ClientId auth challenge on {Method} {Path}: {FailureReason}. "
				+ "Headers — {ClientIdHeader}: {ClientId}, "
				+ "{ApiKeyHeader} present: {ApiKeyPresent}, "
				+ "Content-Type: {ContentType}, Content-Length: {ContentLength}",
			Request.Method,
			Request.Path,
			failureMessage ?? "(no failure message)",
			Options.HeaderName,
			Request.Headers.TryGetValue(Options.HeaderName, out var clientId)
				? clientId.ToString()
				: AbsentValue,
			Options.ApiKeyHeaderName,
			Request.Headers.ContainsKey(Options.ApiKeyHeaderName),
			Request.ContentType ?? AbsentValue,
			Request.ContentLength?.ToString() ?? AbsentValue
		);

		Response.StatusCode = 401;
		var challenge = string.IsNullOrEmpty(failureMessage)
			? Scheme.Name
			: $"{Scheme.Name} error=\"invalid_request\", error_description=\"{EscapeQuotedString(failureMessage)}\"";
		Response.Headers.Append("WWW-Authenticate", challenge);
	}

	private static string EscapeQuotedString(string value)
	{
		// RFC 7235 quoted-string escaping: backslash and double-quote.
		// Cap the surfaced length defensively even though the source is a
		// server-authored constant template.
		var trimmed = value.Length > 256 ? value[..256] : value;
		return trimmed.Replace("\\", "\\\\").Replace("\"", "\\\"");
	}

	// Length-independent constant-time equality. We SHA-256 both inputs to a
	// fixed 32-byte digest, then compare the digests. This avoids the
	// length-based short-circuit in CryptographicOperations.FixedTimeEquals
	// (which returns immediately when buffer lengths differ), so the time
	// taken does not reveal the length of the expected API key.
	private static bool FixedTimeEquals(string a, string b)
	{
		Span<byte> aHash = stackalloc byte[32];
		Span<byte> bHash = stackalloc byte[32];
		SHA256.HashData(Encoding.UTF8.GetBytes(a), aHash);
		SHA256.HashData(Encoding.UTF8.GetBytes(b), bHash);
		return CryptographicOperations.FixedTimeEquals(aHash, bHash);
	}
}
