namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// The filters, ordering and page a caseworker asks for when listing the
/// cases of their nation, as given in the query string of <c>GET /cases</c>.
/// </summary>
internal sealed record CaseListRequest(
	string[]? CaseType,
	string[]? StateId,
	string? Search,
	string? AssigneeId,
	bool? Unassigned,
	string? OrgId,
	bool? IsTerminal,
	string? Sort,
	string? Dir,
	int? Page,
	int? PageSize
);
