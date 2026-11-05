using System.Security.Claims;

namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// Cases are scoped by nation: a caseworker sees and acts on only the cases
/// of the nation in their signed <c>user:nation</c> claim, whose values are
/// those of <see cref="CaseNations.All"/>.
/// </summary>
internal static class CaseNationScope
{
	public const string ClaimType = "user:nation";

	/// <summary>The caller's nation, or <c>null</c> when they carry none.</summary>
	public static string? CallerNation(ClaimsPrincipal? user)
	{
		var nation = user?.FindFirstValue(ClaimType);
		return string.IsNullOrWhiteSpace(nation) ? null : nation;
	}

	public static bool Includes(ClaimsPrincipal? user, Case caseRecord) =>
		string.Equals(CallerNation(user), caseRecord.Nation, StringComparison.Ordinal);
}
