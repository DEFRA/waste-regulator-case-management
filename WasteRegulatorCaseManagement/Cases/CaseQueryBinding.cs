namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// Turns a caseworker's <see cref="CaseListRequest"/> into a
/// <see cref="CaseQuery"/>, ignoring blank values.
/// </summary>
internal static class CaseQueryBinding
{
	/// <exception cref="InvalidCaseQueryException">
	/// A <c>displayData</c> entry is not a valid filter.
	/// </exception>
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
			DisplayData: DisplayDataFilters(request.DisplayData),
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

	/// <summary>
	/// The non-blank <c>displayData</c> entries grouped by name, or
	/// <c>null</c> when there are none. The value is kept exactly as given,
	/// because it is matched exactly against the stored value.
	/// </summary>
	/// <exception cref="InvalidCaseQueryException">
	/// An entry is not <c>name:value</c> with a
	/// <see cref="CaseDisplayField.IsValidName">valid name</see> and a
	/// non-empty value.
	/// </exception>
	private static IReadOnlyDictionary<string, IReadOnlyCollection<string>>? DisplayDataFilters(string[]? entries)
	{
		var filters = (entries ?? [])
			.Where(e => !string.IsNullOrWhiteSpace(e))
			.Select(ParseDisplayDataFilter)
			.GroupBy(f => f.Name, StringComparer.Ordinal)
			.ToDictionary(
				g => g.Key,
				g => (IReadOnlyCollection<string>)g.Select(f => f.Value).Distinct(StringComparer.Ordinal).ToList(),
				StringComparer.Ordinal
			);

		return filters.Count == 0 ? null : filters;
	}

	private static (string Name, string Value) ParseDisplayDataFilter(string entry)
	{
		var separator = entry.IndexOf(':');
		var name = separator < 0 ? entry : entry[..separator];
		var value = separator < 0 ? "" : entry[(separator + 1)..];
		if (!CaseDisplayField.IsValidName(name) || value.Length == 0)
		{
			throw new InvalidCaseQueryException(
				$"'displayData' must be name:value, with a name of letters, digits, '_' or '-', but was '{CaseDisplayField.Shortened(entry)}'."
			);
		}

		return (name, value);
	}

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
