using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using WasteRegulatorCaseManagement.Utils.Mongo;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;

namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// Persistence for <see cref="Case"/>s. Owned by the framework so every
/// type shares a single envelope/index strategy; modules read/write their own
/// payload shape on top of it.
/// </summary>
public interface ICasePersistence
{
    Task CreateAsync(Case caseRecord, CancellationToken cancellationToken = default);

    /// <summary>
    /// Insert <paramref name="caseRecord"/> only if no document with the
    /// same <see cref="Case.Id"/> exists. Returns <c>true</c> when
    /// the document was inserted and <c>false</c> when an item with
    /// that id already existed (the on-disk document is left
    /// untouched). The check is atomic — it relies on the unique
    /// <c>_id</c> index, so two callers racing with the same id
    /// produce exactly one insert and one <c>false</c> regardless of
    /// timing (epr-33c).
    /// </summary>
    Task<bool> CreateIfAbsentAsync(
        Case caseRecord,
        CancellationToken cancellationToken = default
    );

    Task<Case?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Return a single page of cases matching <paramref name="query"/>,
    /// most-recently-submitted first, together with the total number of
    /// matches across every page.
    ///
    /// The per-item <see cref="Case.Notes"/> and
    /// <see cref="Case.AuditLog"/> collections are excluded server-side
    /// (epr-4pf). The returned <see cref="Case"/> instances therefore
    /// carry empty <c>Notes</c> / <c>AuditLog</c> lists regardless of
    /// what is on disk; callers that need the full timeline must
    /// <see cref="GetByIdAsync"/> the item individually. This keeps the
    /// list path's bandwidth bounded by the document envelope rather
    /// than by accumulated assessor activity.
    /// </summary>
    Task<CasePage> QueryAsync(
        CaseQuery query,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Persist updates made by the engine (state transitions, assignment, notes).
    /// Implementations replace the document in its entirety so callers can
    /// mutate any field on the supplied <see cref="Case"/> before saving.
    /// </summary>
    Task ReplaceAsync(Case caseRecord, CancellationToken cancellationToken = default);
}

public sealed class CasePersistence
    : MongoService<Case>,
        ICasePersistence
{
    // Computed once: state id → workflow rank (RA-324), used by the Status sort.
    private readonly IReadOnlyDictionary<string, int> _statusRank;

    public CasePersistence(
        IMongoDbClientFactory connectionFactory,
        ILoggerFactory loggerFactory,
        ICaseRegistry registry
    )
        : base(connectionFactory, "cases", loggerFactory)
    {
        _statusRank = CaseSort.StatusRank(registry);
    }

    [ExcludeFromCodeCoverage]
    public async Task CreateAsync(Case caseRecord, CancellationToken cancellationToken = default)
    {
        await Collection.InsertOneAsync(caseRecord, cancellationToken: cancellationToken);
        Logger.LogInformation(
            "Submitted case {CaseId} of type {CaseTypeId} by {SubmittedBy}",
            caseRecord.Id,
            caseRecord.TypeId,
            caseRecord.SubmittedBy ?? "unknown"
        );
    }

    public async Task<bool> CreateIfAbsentAsync(
        Case caseRecord,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(caseRecord);

        try
        {
            await Collection.InsertOneAsync(caseRecord, cancellationToken: cancellationToken);
            return true;
        }
        catch (MongoWriteException ex)
            when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // _id already in the collection — another instance won the
            // race or the seeder has already run on this database.
            // Either way the caller treats this as a successful no-op.
            return false;
        }
    }

    public async Task<Case?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        return await Collection.Find(w => w.Id == id).FirstOrDefaultAsync(cancellationToken);
    }

    [ExcludeFromCodeCoverage]
    public async Task<CasePage> QueryAsync(
        CaseQuery query,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(query);

        var filter = BuildFilter(query);

        var totalCount = await Collection.CountDocumentsAsync(
            filter,
            cancellationToken: cancellationToken
        );

        var page = query.NormalisedPage;
        var pageSize = query.NormalisedPageSize;
        var skip = (page - 1) * pageSize;

        var sortStages = CaseSort.BuildStages(query.Sort, query.SortDescending, _statusRank);

        List<Case> items;
        if (sortStages is null)
        {
            // Default path (RA-324: unchanged from the original behaviour) —
            // newest submitted first, with the per-item Notes / AuditLog
            // collections projected away (epr-4pf): the list endpoint never
            // renders them and they dominate document size on chatty items.
            var projection = Builders<Case>
                .Projection.Exclude(w => w.Notes)
                .Exclude(w => w.AuditLog);

            items = await Collection
                .Find(filter)
                .Project<Case>(projection)
                .SortByDescending(w => w.SubmittedAt)
                .Skip(skip)
                .Limit(pageSize)
                .ToListAsync(cancellationToken);
        }
        else
        {
            // Explicit RA-324 sort (organisation / status / due-date). Status
            // and due-date can't be expressed as a plain field sort, so an
            // aggregation computes the sort key. Two $unset stages: the first
            // drops the fat Notes / AuditLog collections (the same ones the
            // default projection excludes) BEFORE the in-memory $sort so the
            // sort buffers slim documents; the second drops the computed sort
            // scratch fields AFTER the $sort that reads them, so the result
            // still deserialises to Case (which does not ignore extra BSON
            // elements).
            var (addFields, sort) = sortStages.Value;
            var serializer =
                MongoDB.Bson.Serialization.BsonSerializer.SerializerRegistry.GetSerializer<Case>();
            var matchDoc = filter.Render(
                new RenderArgs<Case>(
                    serializer,
                    MongoDB.Bson.Serialization.BsonSerializer.SerializerRegistry
                )
            );

            var stages = new List<BsonDocument>
            {
                new("$match", matchDoc),
                new("$unset", new BsonArray { "notes", "auditLog" }),
            };
            if (addFields is not null)
            {
                stages.Add(new BsonDocument("$addFields", addFields));
            }
            stages.Add(new BsonDocument("$sort", sort));
            stages.Add(new BsonDocument("$unset", new BsonArray(CaseSort.ComputedFields)));
            stages.Add(new BsonDocument("$skip", skip));
            stages.Add(new BsonDocument("$limit", pageSize));

            PipelineDefinition<Case, Case> pipeline = stages;
            using var cursor = await Collection.AggregateAsync(
                pipeline,
                cancellationToken: cancellationToken
            );
            items = await cursor.ToListAsync(cancellationToken);
        }

        return new CasePage(items, totalCount, page, pageSize);
    }

    internal static FilterDefinition<Case> BuildFilter(CaseQuery query)
    {
        var builder = Builders<Case>.Filter;
        var clauses = new List<FilterDefinition<Case>>();

        if (query.TypeIds is { Count: > 0 } typeIds)
        {
            clauses.Add(builder.In(w => w.TypeId, typeIds));
        }

        if (query.StateIds is { Count: > 0 } stateIds)
        {
            clauses.Add(builder.In(w => w.StateId, stateIds));
        }

        var search = query.NormalisedSearch;
        if (!string.IsNullOrEmpty(search))
        {
            // Case-insensitive substring on submitter, plus prefix match on the
            // string-serialised id (which lets a user paste a full or partial
            // id into the search box).
            var escaped = System.Text.RegularExpressions.Regex.Escape(search);
            var pattern = new MongoDB.Bson.BsonRegularExpression(escaped, "i");

            clauses.Add(
                builder.Or(
                    builder.Regex("_id", pattern),
                    builder.Regex(nameof(Case.SubmittedBy), pattern)
                )
            );
        }

        var assigneeId = query.NormalisedAssigneeId;
        if (assigneeId is not null && query.UnassignedOnly)
        {
            // "Show me my work and anything still up for grabs" — assigned to
            // the user OR unassigned.
            clauses.Add(
                builder.Or(
                    builder.Eq(w => w.AssignedToId, assigneeId),
                    builder.Eq(w => w.AssignedToId, null)
                )
            );
        }
        else if (assigneeId is not null)
        {
            clauses.Add(builder.Eq(w => w.AssignedToId, assigneeId));
        }
        else if (query.UnassignedOnly)
        {
            clauses.Add(builder.Eq(w => w.AssignedToId, null));
        }

        var submittedBy = query.NormalisedSubmittedBy;
        if (submittedBy is not null)
        {
            clauses.Add(builder.Eq(w => w.SubmittedBy, submittedBy));
        }

        // RA-313: there is deliberately NO terminal-state exclusion here. A
        // regulator looking for a decided item should find it where every
        // other item is.

        return clauses.Count == 0 ? builder.Empty : builder.And(clauses);
    }

    [ExcludeFromCodeCoverage]
    public async Task ReplaceAsync(Case caseRecord, CancellationToken cancellationToken = default)
    {
        var expectedVersion = caseRecord.Version;
        caseRecord.Version = expectedVersion + 1;

        var result = await Collection.ReplaceOneAsync(
            w => w.Id == caseRecord.Id && w.Version == expectedVersion,
            caseRecord,
            cancellationToken: cancellationToken
        );

        if (result.MatchedCount != 1)
        {
            // Roll the in-memory version back so a caller that catches and
            // retries does not double-increment.
            caseRecord.Version = expectedVersion;
            throw new CaseConcurrencyException(caseRecord.Id, expectedVersion);
        }

        Logger.LogInformation(
            "Updated case {CaseId} of type {CaseTypeId} now in state {CaseState} (version {Version})",
            caseRecord.Id,
            caseRecord.TypeId,
            caseRecord.StateId,
            caseRecord.Version
        );
    }

    [ExcludeFromCodeCoverage]
    protected override List<CreateIndexModel<Case>> DefineIndexes(
        IndexKeysDefinitionBuilder<Case> builder
    )
    {
        var typeAndSubmitted = new CreateIndexModel<Case>(
            builder.Combine(
                builder.Ascending(w => w.TypeId),
                builder.Descending(w => w.SubmittedAt)
            )
        );
        var stateAndSubmitted = new CreateIndexModel<Case>(
            builder.Combine(
                builder.Ascending(w => w.StateId),
                builder.Descending(w => w.SubmittedAt)
            )
        );
        var submittedDescending = new CreateIndexModel<Case>(
            builder.Descending(w => w.SubmittedAt)
        );
        var assigneeAndSubmitted = new CreateIndexModel<Case>(
            builder.Combine(
                builder.Ascending(w => w.AssignedToId),
                builder.Descending(w => w.SubmittedAt)
            )
        );
        return
        [
            typeAndSubmitted,
            stateAndSubmitted,
            submittedDescending,
            assigneeAndSubmitted,
        ];
    }
}

/// <summary>
/// Conversions between API-facing <see cref="JsonElement"/> payloads and the
/// <see cref="BsonDocument"/> form persisted in MongoDB. Lifted to a static
/// helper so endpoints, tests and future modules share one implementation.
/// </summary>
public static class CasePayloadConverter
{
    private static readonly BsonDocument s_emptyDocument = new();

    /// <summary>
    /// Pinned BSON-to-JSON output mode for every payload we hand to API
    /// consumers. Relaxed extended JSON keeps int/long/double/decimal as
    /// plain JSON numbers and emits dates as <c>{ "$date": "ISO-8601" }</c>,
    /// so frontends see a stable shape regardless of driver version
    /// defaults (epr-b0x).
    /// </summary>
    private static readonly JsonWriterSettings s_jsonWriterSettings = new()
    {
        OutputMode = JsonOutputMode.RelaxedExtendedJson,
    };

    public static BsonDocument ToBson(JsonElement? payload)
    {
        if (
            !payload.HasValue
            || payload.Value.ValueKind == JsonValueKind.Undefined
            || payload.Value.ValueKind == JsonValueKind.Null
        )
        {
            return new BsonDocument();
        }

        if (payload.Value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidCasePayloadException(
                $"Case payload must be a JSON object, got {payload.Value.ValueKind}."
            );
        }

        var json = payload.Value.GetRawText();
        return BsonDocument.Parse(json);
    }

    public static JsonElement ToJson(BsonDocument? document)
    {
        var bson = document ?? s_emptyDocument;
        var json = bson.ToJson(s_jsonWriterSettings);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}

public sealed class InvalidCasePayloadException(string message) : Exception(message);
