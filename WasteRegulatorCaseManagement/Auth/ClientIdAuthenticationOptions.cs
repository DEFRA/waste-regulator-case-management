using Microsoft.AspNetCore.Authentication;

namespace WasteRegulatorCaseManagement.Auth;

public class ClientIdAuthenticationOptions : AuthenticationSchemeOptions
{
	/// <summary>
	/// Name of the request header carrying the client ID.
	/// </summary>
	public string HeaderName { get; set; } = ClientIdDefaults.DefaultHeaderName;

	/// <summary>Optional header carrying the end user's identifier.</summary>
	public string UserIdHeaderName { get; set; } = ClientIdDefaults.DefaultUserIdHeaderName;

	/// <summary>Optional header carrying the end user's display name.</summary>
	public string UserNameHeaderName { get; set; } = ClientIdDefaults.DefaultUserNameHeaderName;

	/// <summary>Optional header carrying the caller's role. See <see cref="ClientIdDefaults.DefaultRoleHeaderName"/>.</summary>
	public string RoleHeaderName { get; set; } = ClientIdDefaults.DefaultRoleHeaderName;

	/// <summary>Optional header carrying the caller's nation scope. See <see cref="ClientIdDefaults.DefaultNationHeaderName"/>.</summary>
	public string NationHeaderName { get; set; } = ClientIdDefaults.DefaultNationHeaderName;

	/// <summary>Optional header carrying the end user's email address. See <see cref="ClientIdDefaults.DefaultEmailHeaderName"/>.</summary>
	public string EmailHeaderName { get; set; } = ClientIdDefaults.DefaultEmailHeaderName;

	/// <summary>
	/// Name of the request header carrying the caller's API key. See
	/// <see cref="ClientIdDefaults.DefaultApiKeyHeaderName"/>.
	/// </summary>
	public string ApiKeyHeaderName { get; set; } = ClientIdDefaults.DefaultApiKeyHeaderName;

	/// <summary>
	/// Per-caller API keys, keyed by the <c>clientId</c> each caller is
	/// expected to assert. A request is accepted only when its
	/// <see cref="ApiKeyHeaderName"/> matches the key registered for the
	/// clientId it asserts; an unrecognized clientId is rejected exactly
	/// like a wrong key. When empty the handler falls back to header-trust
	/// mode (development/local only). See ADR-0006 and ADR-0012.
	/// </summary>
	public IReadOnlyDictionary<string, string> ApiKeys { get; set; } =
		new Dictionary<string, string>(StringComparer.Ordinal);

	/// <summary>
	/// Maximum permitted length (in UTF-16 chars) of the client id
	/// header. Oversize values are rejected with 401 BEFORE any further
	/// processing — avoids attacker-controlled header bytes feeding into
	/// downstream allocations.
	/// </summary>
	public int MaxClientIdLength { get; set; } = 256;

	/// <summary>
	/// Maximum permitted length of the user id header. Persisted into
	/// <c>Case.Timeline.PerformedById</c>
	/// — capping defends Mongo document size against a misbehaving BFF or
	/// a caller that holds an API key.
	/// </summary>
	public int MaxUserIdLength { get; set; } = 128;

	/// <summary>
	/// Maximum permitted length of the user display name header. Persisted
	/// into <c>Case.Timeline.PerformedByName</c>.
	/// </summary>
	public int MaxUserNameLength { get; set; } = 256;

	/// <summary>
	/// Maximum permitted length of the role header. Persisted into the
	/// <c>user:role</c> claim.
	/// </summary>
	public int MaxUserRoleLength { get; set; } = 64;

	/// <summary>
	/// Maximum permitted length of the nation header. Persisted into the
	/// <c>user:nation</c> claim.
	/// </summary>
	public int MaxUserNationLength { get; set; } = 32;

	/// <summary>
	/// Maximum permitted length of the email header, the longest address
	/// RFC 5321 allows. Persisted into the <c>user:email</c> claim.
	/// </summary>
	public int MaxUserEmailLength { get; set; } = 320;

	/// <summary>
	/// Maximum permitted length of the API key header, so an oversize
	/// value never reaches the constant-time comparator.
	/// </summary>
	public int MaxApiKeyLength { get; set; } = 256;
}
