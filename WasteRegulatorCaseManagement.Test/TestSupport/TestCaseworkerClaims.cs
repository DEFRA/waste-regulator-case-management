using System.Security.Claims;
using WasteRegulatorCaseManagement.Auth;

namespace WasteRegulatorCaseManagement.Test.TestSupport;

/// <summary>Standard caseworker identity used across unit and integration tests.</summary>
public static class TestCaseworkerClaims
{
	public const string ClientId = "test-client";
	public const string UserId = "test-user";
	public const string UserName = "Test Caseworker";
	public const string UserEmail = "caseworker@regulator.example";
	public const string Nation = "ENGLAND";

	public static Claim[] StandardClaims { get; } =
	[
		new("client_id", ClientId),
		new("user:nation", Nation),
		new("user:id", UserId),
		new("user:email", UserEmail),
		new("user:name", UserName),
	];

	public static ClaimsPrincipal Principal(string? userIdOverride = null)
	{
		var claims = new List<Claim>(StandardClaims);
		if (userIdOverride is not null)
		{
			claims.RemoveAll(c => c.Type == "user:id");
			claims.Add(new Claim("user:id", userIdOverride));
		}

		return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
	}

	public static void ApplyCaseworkerHeaders(HttpClient client)
	{
		client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, ClientId);
		client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultUserIdHeaderName, UserId);
		client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultUserNameHeaderName, UserName);
		client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultEmailHeaderName, UserEmail);
		client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultNationHeaderName, Nation);
	}
}