namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// Turns a caseworker's <see cref="CaseListRequest"/> into a
/// <see cref="CaseQuery"/>, ignoring blank values.
/// </summary>
internal static class CaseQueryBinding
{
	public static CaseQuery FromRequest(CaseListRequest request) =>
		new(
			CaseTypes: Values(request.CaseType),
			StateIds: Values(request.StateId),
			Search: Value(request.Search),
			AssigneeId: Value(request.AssigneeId),
			UnassignedOnly: request.Unassigned ?? false,
			Page: request.Page ?? 1,
			PageSize: request.PageSize ?? CaseQuery.DefaultPageSize,
			Sort: Value(request.Sort),
			SortDescending: SortDescending(request.Dir),
			OrgId: Value(request.OrgId),
			IsTerminal: request.IsTerminal
		);

	/// <summary>
	/// <c>desc</c>/<c>descending</c> → <c>true</c>, <c>asc</c>/<c>ascending</c>
	/// → <c>false</c>, anything else → <c>null</c> so the requested sort column
	/// applies its own natural default direction.
	/// </summary>
	private static bool? SortDescending(string? dir) =>
		dir?.Trim().ToLowerInvariant() switch
		{
			"desc" or "descending" => true,
			"asc" or "ascending" => false,
			_ => null,
		};

	private static IReadOnlyCollection<string>? Values(string[]? values)
	{
		var trimmed = (values ?? [])
			.Where(v => !string.IsNullOrWhiteSpace(v))
			.Select(v => v.Trim())
			.Distinct(StringComparer.Ordinal)
			.ToList();

		return trimmed.Count == 0 ? null : trimmed;
	}

	private static string? Value(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
