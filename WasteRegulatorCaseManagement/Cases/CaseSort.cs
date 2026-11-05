using MongoDB.Bson;

namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// Translates the <c>?sort=</c> / <c>?dir=</c> query params into the MongoDB
/// aggregation stages that reorder the case list (RA-324). Kept as a
/// pure, side-effect-free helper so the tricky bit — the status workflow
/// ranking — can be unit-tested without a database.
///
/// <para>
/// <b>Status</b> cannot be expressed as a plain field sort: it sorts by
/// workflow progression, not alphabetically, so state ids are mapped to a rank
/// via <c>$switch</c>.
/// </para>
/// </summary>
internal static class CaseSort
{
    internal const string StatusToken = "status";

    // Computed fields injected by an $addFields stage. They must be removed
    // ($unset) before the aggregation result is deserialised because Case
    // does not ignore extra BSON elements.
    internal const string RankField = "_sortRank";

    /// <summary>Every computed field name this helper can inject; always $unset before deserialising.</summary>
    internal static readonly IReadOnlyList<string> ComputedFields =
        [RankField];

    // Rank assigned to any state id the registry does not know about, so
    // unexpected states sort last under a Status sort rather than colliding
    // with rank 0.
    internal const int UnknownStateRank = int.MaxValue;

    /// <summary>
    /// Map each state id to a workflow rank derived from the order states are
    /// declared in on their <see cref="ICaseType"/>: data-driven from the
    /// registry rather than hardcoded, so a module's declared state order IS its
    /// sort order.
    /// First declaration wins when types share an id.
    /// </summary>
    internal static IReadOnlyDictionary<string, int> StatusRank(ICaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var next = 0;
        foreach (var type in registry.Types)
        {
            foreach (var state in type.States)
            {
                if (!rank.ContainsKey(state.Id))
                {
                    rank[state.Id] = next++;
                }
            }
        }
        return rank;
    }

    /// <summary>
    /// Build the <c>$addFields</c> (may be <c>null</c>) and <c>$sort</c> stages
    /// for the requested <c>sort</c> token, or <c>null</c> when the token is
    /// blank or unrecognised — in which case the caller keeps the default
    /// newest-submitted-first Find path, so the default ordering can never
    /// regress. <paramref name="descendingOverride"/> is the optional
    /// <c>?dir=</c> value; when <c>null</c> each column applies its natural
    /// default direction.
    /// </summary>
    internal static (BsonDocument? AddFields, BsonDocument Sort)? BuildStages(
        string? sortToken,
        bool? descendingOverride,
        IReadOnlyDictionary<string, int> statusRank)
    {
        ArgumentNullException.ThrowIfNull(statusRank);

        return sortToken?.Trim().ToLowerInvariant() switch
        {
            StatusToken => BuildStatusStages(descendingOverride, statusRank),
            _ => null
        };
    }

    private static (BsonDocument?, BsonDocument) BuildStatusStages(
        bool? descendingOverride, IReadOnlyDictionary<string, int> statusRank)
    {
        // Default = workflow order (ascending rank).
        var dir = descendingOverride == true ? -1 : 1;
        var branches = new BsonArray(statusRank
            .OrderBy(kv => kv.Value)
            .Select(kv => new BsonDocument
            {
                { "case", new BsonDocument("$eq", new BsonArray { "$stateId", kv.Key }) },
                { "then", kv.Value }
            }));
        // $switch rejects an empty branches array, so with no registered
        // states every item simply takes the unknown-state rank.
        BsonValue rank = branches.Count == 0
            ? UnknownStateRank
            : new BsonDocument("$switch", new BsonDocument
            {
                { "branches", branches },
                { "default", UnknownStateRank }
            });
        var addFields = new BsonDocument(RankField, rank);
        var sort = StableSort(RankField, dir);
        return (addFields, sort);
    }

    /// <summary>
    /// A sort on <paramref name="primaryField"/> made deterministic by breaking
    /// ties on newest-submitted then <c>_id</c>, so paging is stable.
    /// </summary>
    private static BsonDocument StableSort(string primaryField, int direction) => new()
    {
        { primaryField, direction },
        { "submittedAt", -1 },
        { "_id", 1 }
    };
}
