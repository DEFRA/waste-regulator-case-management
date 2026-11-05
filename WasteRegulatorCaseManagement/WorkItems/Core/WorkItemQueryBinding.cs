using Microsoft.AspNetCore.Http;

namespace WasteRegulatorCaseManagement.WorkItems.Core;

/// <summary>
/// Translates the query string of <c>GET /work-items</c> into a
/// <see cref="WorkItemQuery"/>. Lifted to a static helper so the endpoint
/// stays tight and can be unit-tested in isolation.
/// </summary>
internal static class WorkItemQueryBinding
{
    internal const string TypeIdParam = "typeId";
    internal const string StateIdParam = "stateId";
    internal const string SearchParam = "search";
    internal const string AssigneeIdParam = "assigneeId";
    internal const string UnassignedOnlyParam = "unassigned";
    internal const string PageParam = "page";
    internal const string PageSizeParam = "pageSize";

    // RA-324 sort params.
    internal const string SortParam = "sort";
    internal const string SortDirectionParam = "dir";

    internal const string SubmittedByParam = "submittedBy";

    public static WorkItemQuery FromQueryString(IQueryCollection query)
    {
        ArgumentNullException.ThrowIfNull(query);

        return new WorkItemQuery(
            TypeIds: ReadStrings(query, TypeIdParam),
            StateIds: ReadStrings(query, StateIdParam),
            Search: ReadString(query, SearchParam),
            AssigneeId: ReadString(query, AssigneeIdParam),
            UnassignedOnly: ReadBool(query, UnassignedOnlyParam),
            Page: ReadInt(query, PageParam, defaultValue: 1),
            PageSize: ReadInt(query, PageSizeParam, defaultValue: WorkItemQuery.DefaultPageSize),
            SubmittedBy: ReadString(query, SubmittedByParam),
            Sort: ReadString(query, SortParam),
            SortDescending: ReadSortDirection(query, SortDirectionParam));
    }

    /// <summary>
    /// Reads the optional <c>?dir=</c> value: <c>desc</c>/<c>descending</c> →
    /// <c>true</c>, <c>asc</c>/<c>ascending</c> → <c>false</c>, anything else
    /// (absent, blank, unrecognised) → <c>null</c> so the requested sort column
    /// applies its own natural default direction.
    /// </summary>
    private static bool? ReadSortDirection(IQueryCollection query, string key)
    {
        var value = ReadString(query, key)?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }
        if (value.Equals("desc", StringComparison.OrdinalIgnoreCase)
            || value.Equals("descending", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (value.Equals("asc", StringComparison.OrdinalIgnoreCase)
            || value.Equals("ascending", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return null;
    }

    private static IReadOnlyCollection<string>? ReadStrings(IQueryCollection query, string key)
    {
        if (!query.TryGetValue(key, out var values) || values.Count == 0)
        {
            return null;
        }

        var trimmed = values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return trimmed.Count == 0 ? null : trimmed;
    }

    private static string? ReadString(IQueryCollection query, string key)
    {
        if (!query.TryGetValue(key, out var values) || values.Count == 0)
        {
            return null;
        }

        var first = values[0];
        return string.IsNullOrWhiteSpace(first) ? null : first;
    }

    private static int ReadInt(IQueryCollection query, string key, int defaultValue)
    {
        if (!query.TryGetValue(key, out var values) || values.Count == 0)
        {
            return defaultValue;
        }

        return int.TryParse(values[0], out var parsed) ? parsed : defaultValue;
    }

    private static bool ReadBool(IQueryCollection query, string key)
    {
        if (!query.TryGetValue(key, out var values) || values.Count == 0)
        {
            return false;
        }

        var first = values[0];
        if (string.IsNullOrWhiteSpace(first))
        {
            return false;
        }

        // Accept the usual truthy spellings used in HTML forms / query
        // strings: "true", "1", "on", "yes" (any case).
        var trimmed = first.Trim();
        return trimmed.Equals("true", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("1", StringComparison.Ordinal)
            || trimmed.Equals("on", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
}
