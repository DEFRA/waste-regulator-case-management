namespace WasteRegulatorCaseManagement.Auth;

public static class ClientIdDefaults
{
	public const string AuthenticationScheme = "ClientId";

	/// <summary>
	/// Header carrying the calling service's self-asserted client ID.
	/// Not verified by CDP itself — trust comes from the API key
	/// (see <see cref="DefaultApiKeyHeaderName"/>), not from this header
	/// alone.
	/// </summary>
	public const string DefaultHeaderName = "x-cdp-client-id";

	/// <summary>
	/// Optional header carrying the end user's identifier as the BFF sees it
	/// (typically the session user's <c>id</c>). Lets the backend distinguish
	/// the human acting via the BFF from the BFF service identity supplied in
	/// <see cref="DefaultHeaderName"/>.
	/// </summary>
	public const string DefaultUserIdHeaderName = "x-cdp-user-id";

	/// <summary>
	/// Optional header carrying the caller's role as the BFF sees it (e.g.
	/// <c>"standard"</c>/<c>"support-readonly"</c>) — becomes claim
	/// <c>"user:role"</c>.
	/// </summary>
	public const string DefaultRoleHeaderName = "x-cdp-user-role";

	/// <summary>
	/// Header carrying the caller's nation scope, one of
	/// <c>ENGLAND</c>/<c>WALES</c>/<c>SCOTLAND</c>/<c>N_IRELAND</c> — becomes
	/// claim <c>"user:nation"</c>, which scopes every case read and mutation.
	/// </summary>
	public const string DefaultNationHeaderName = "x-cdp-user-nation";

	/// <summary>
	/// Header carrying the caller's API key: the key registered for the
	/// client id it asserts in <see cref="DefaultHeaderName"/>. Distinct
	/// from <c>x-api-key</c>, which the CDP API gateway uses for developer
	/// API keys.
	/// </summary>
	public const string DefaultApiKeyHeaderName = "x-cdp-auth-key";
}
