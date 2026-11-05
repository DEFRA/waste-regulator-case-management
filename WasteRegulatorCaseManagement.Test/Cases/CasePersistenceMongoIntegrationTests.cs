using WasteRegulatorCaseManagement.Test.TestSupport;
using WasteRegulatorCaseManagement.Utils.Mongo;
using WasteRegulatorCaseManagement.Cases;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;

namespace WasteRegulatorCaseManagement.Test.Cases;

/// <summary>
/// epr-bqe: real Mongo end-to-end coverage for <see cref="CasePersistence"/>.
/// Uses Ephemeral MongoDB (real <c>mongod</c> on a random port, in a
/// temp data directory, torn down with the fixture) so the BSON
/// serializers registered by <see cref="MongoConventions"/>, the index definitions in
/// <see cref="CasePersistence.DefineIndexes"/>, the projection that
/// strips <see cref="Case.Notes"/> / <see cref="Case.AuditLog"/>
/// from <see cref="CasePersistence.QueryAsync"/>, and the
/// optimistic-concurrency path in <see cref="CasePersistence.ReplaceAsync"/>
/// are exercised against the real driver — not faked. None of these
/// were previously covered by a Mongo-driven test (the suite mocked
/// <see cref="ICasePersistence"/> wholesale), which let any
/// regression in BSON conventions, indexes or projections pass CI.
/// </summary>
public sealed class CasePersistenceMongoIntegrationTests : IAsyncDisposable, IDisposable
{
    private static readonly string[] s_expectedFirstPage = ["a", "b"];
    private static readonly string[] s_expectedNewestSubmittedFirst = ["newer", "older"];
    private static readonly string[] s_expectedSecondPage = ["c", "d"];
    private static readonly string[] s_expectedStatusRankAscending =
    [
        "submitted",
        "duly-made",
        "assessment-in-progress",
        "awaiting-decision",
        "queried",
    ];
    private static readonly string[] s_expectedStatusRankDescending = ["q", "a", "s-new", "s-old"];
    private static readonly string[] s_expectedThirdPage = ["e"];

    /// <summary>
    /// Deterministic timestamp seed used for every <see cref="Case"/>
    /// constructed in this fixture (epr-6e5). Inlined values were
    /// previously <c>DateTime.UtcNow</c>, which is wallclock-coupled
    /// and obscures which value the contract under test actually
    /// cares about.
    /// </summary>
    private static readonly DateTimeOffset s_seedNow = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly MongoIntegrationFixture _fixture;
    private readonly string _databaseName;
    private readonly TestMongoDbClientFactory _clientFactory;
    private readonly CasePersistence _persistence;
    private readonly FakeTimeProvider _time = new(s_seedNow);

    public CasePersistenceMongoIntegrationTests(MongoIntegrationFixture fixture)
    {
        _fixture = fixture;
        // Each test class instance (xUnit creates one per [Fact]) gets a
        // fresh database name so collections / indexes from one test do
        // not leak into another.
        _databaseName = MongoIntegrationFixture.NewDatabaseName("cases");
        _clientFactory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _persistence = new CasePersistence(
            _clientFactory,
            NullLoggerFactory.Instance,
            new CaseRegistry([
                new TestCaseType(
                    "test",
                    "Test type",
                    states: s_expectedStatusRankAscending
                        .Select(id => new CaseState(id, id))
                        .ToArray()
                ),
            ])
        );
    }

    /// <summary>
    /// epr-6e5: <see cref="MongoClient"/> exposes async DB management
    /// APIs and the test runner already drives every other resource
    /// asynchronously. Prefer the async path; the synchronous
    /// <see cref="Dispose"/> remains as a safety net for the rare
    /// xUnit code path that disposes via the synchronous interface.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _clientFactory.GetClient().DropDatabaseAsync(_databaseName);
    }

    public void Dispose()
    {
        // Drop the per-test database so we don't accumulate state in the
        // pooled mongod across the test session.
        _clientFactory.GetClient().DropDatabase(_databaseName);
    }

    [Fact]
    public async Task Submit_then_get_then_query_round_trips_a_case_through_real_mongo()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var caseRecord = new Case
        {
            TypeId = "re-accreditation",
            StateId = "submitted",
            SubmittedAt = now,
            LastModifiedAt = now,
            SubmittedBy = "client-1",
            Payload = new BsonDocument { ["key"] = "value" },
        };

        await _persistence.CreateAsync(caseRecord, TestContext.Current.CancellationToken);

        var fetched = await _persistence.GetByIdAsync(
            caseRecord.Id,
            TestContext.Current.CancellationToken
        );
        Assert.NotNull(fetched);
        Assert.Equal("re-accreditation", fetched!.TypeId);
        Assert.Equal("submitted", fetched.StateId);
        Assert.Equal("client-1", fetched.SubmittedBy);
        Assert.Equal("value", fetched.Payload["key"].AsString);

        var page = await _persistence.QueryAsync(
            new CaseQuery(),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(1, page.TotalCount);
        Assert.Single(page.Items);
        Assert.Equal(caseRecord.Id, page.Items[0].Id);
    }

    [Fact]
    public async Task DefineIndexes_creates_the_four_documented_indexes_on_startup()
    {
        // Constructor of CasePersistence calls EnsureIndexes; we
        // assert what the driver actually wrote (not what we asked for)
        // so a future change to the index set is caught here.
        var collection = _clientFactory.GetCollection<Case>("cases");
        var indexes = await (
            await collection.Indexes.ListAsync(TestContext.Current.CancellationToken)
        ).ToListAsync(TestContext.Current.CancellationToken);

        // Mongo always writes a default _id_ index in addition to ours.
        var keyDocs = indexes
            .Where(i => i["name"].AsString != "_id_")
            .Select(i => i["key"].AsBsonDocument.ToString())
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(4, keyDocs.Count);
        Assert.Contains(
            keyDocs,
            k => k.Contains("\"typeId\" : 1") && k.Contains("\"submittedAt\" : -1")
        );
        Assert.Contains(
            keyDocs,
            k => k.Contains("\"stateId\" : 1") && k.Contains("\"submittedAt\" : -1")
        );
        Assert.Contains(
            keyDocs,
            k => k.Contains("\"assignedToId\" : 1") && k.Contains("\"submittedAt\" : -1")
        );
        Assert.Contains(
            keyDocs,
            k =>
                k.Contains("\"submittedAt\" : -1")
                && !k.Contains("typeId")
                && !k.Contains("stateId")
                && !k.Contains("assignedToId")
        );
    }

    [Fact]
    public async Task EnsureIndexes_is_idempotent_when_the_indexes_already_match()
    {
        // The reconcile must not drop/recreate when nothing changed: running
        // the persistence ctor twice against the same database is a no-op the
        // second time (CreateMany succeeds, no conflict path taken).
        var freshDb = MongoIntegrationFixture.NewDatabaseName("cases_idempotent");
        var factory = new TestMongoDbClientFactory(_fixture.ConnectionString, freshDb);
        try
        {
            _ = new CasePersistence(factory, NullLoggerFactory.Instance, new CaseRegistry([]));
            var ex = Record.Exception(() =>
                new CasePersistence(factory, NullLoggerFactory.Instance, new CaseRegistry([]))
            );
            Assert.Null(ex);

            var collection = factory.GetCollection<Case>("cases");
            var indexes = await (
                await collection.Indexes.ListAsync(TestContext.Current.CancellationToken)
            ).ToListAsync(TestContext.Current.CancellationToken);
            // Mongo's default _id_ index plus our four.
            Assert.Equal(5, indexes.Count);
        }
        finally
        {
            await factory.GetClient().DropDatabaseAsync(freshDb, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task QueryAsync_excludes_notes_and_audit_log_at_the_wire_level()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var caseRecord = new Case
        {
            TypeId = "re-accreditation",
            StateId = "submitted",
            SubmittedAt = now,
            LastModifiedAt = now,
            Notes =
            {
                new CaseNote
                {
                    Text = "secret note body",
                    CreatedAt = now,
                    CreatedBy = "user-1",
                    CreatedByName = "Alice",
                },
            },
            AuditLog =
            {
                new CaseAuditEntry
                {
                    Action = "submitted",
                    ActionDisplayName = "Submitted",
                    CreatedAt = now,
                    CreatedBy = "user-1",
                    CreatedByName = "Alice",
                },
            },
        };
        await _persistence.CreateAsync(caseRecord, TestContext.Current.CancellationToken);

        // Sanity: GetByIdAsync still returns the full document.
        var full = await _persistence.GetByIdAsync(
            caseRecord.Id,
            TestContext.Current.CancellationToken
        );
        Assert.Single(full!.Notes);
        Assert.Single(full.AuditLog);

        // QueryAsync is the projected path; both collections must be empty.
        var page = await _persistence.QueryAsync(
            new CaseQuery(),
            TestContext.Current.CancellationToken
        );
        var item = Assert.Single(page.Items);
        Assert.Empty(item.Notes);
        Assert.Empty(item.AuditLog);
    }

    // ─────────────────────── RA-324 filter + sort (real Mongo) ───────────────────────

    private async Task SeedAsync(
        string organisationName,
        string stateId = "submitted",
        int submittedMinutesAgo = 0
    )
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var payload = new BsonDocument { ["organisationName"] = organisationName };
        var item = new Case
        {
            TypeId = "test",
            StateId = stateId,
            SubmittedAt = now.AddMinutes(-submittedMinutesAgo),
            LastModifiedAt = now,
            Payload = payload,
        };
        await _persistence.CreateAsync(item, TestContext.Current.CancellationToken);
    }

    private static string[] OrgOrder(CasePage page) =>
        page.Items.Select(i => i.Payload["organisationName"].AsString).ToArray();

    [Fact]
    public async Task QueryAsync_default_sort_is_newest_submitted_first()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync("older", submittedMinutesAgo: 60);
        await SeedAsync("newer", submittedMinutesAgo: 0);

        var page = await _persistence.QueryAsync(new CaseQuery(), ct);

        Assert.Equal(s_expectedNewestSubmittedFirst, OrgOrder(page));
    }

    [Fact]
    public async Task QueryAsync_sort_status_orders_by_workflow_rank()
    {
        var ct = TestContext.Current.CancellationToken;
        // Seeded out of workflow order. All non-terminal, but since RA-313 that
        // is incidental — a bare query no longer filters terminal states out.
        await SeedAsync("q", stateId: "queried");
        await SeedAsync("aw", stateId: "awaiting-decision");
        await SeedAsync("s", stateId: "submitted");
        await SeedAsync("a", stateId: "assessment-in-progress");
        await SeedAsync("d", stateId: "duly-made");

        var page = await _persistence.QueryAsync(new CaseQuery(Sort: "status"), ct);

        Assert.Equal(
            s_expectedStatusRankAscending,
            page.Items.Select(i => i.StateId).ToArray()
        );
    }

    [Fact]
    public async Task QueryAsync_sort_status_descending_reverses_rank_with_submitted_tiebreak()
    {
        var ct = TestContext.Current.CancellationToken;
        // Two items share the 'submitted' rank so the submittedAt-desc tiebreak
        // is exercised end-to-end, not just at the BSON level.
        await SeedAsync("q", stateId: "queried");
        await SeedAsync("a", stateId: "assessment-in-progress");
        await SeedAsync("s-old", stateId: "submitted", submittedMinutesAgo: 60);
        await SeedAsync("s-new", stateId: "submitted", submittedMinutesAgo: 0);

        var page = await _persistence.QueryAsync(
            new CaseQuery(Sort: "status", SortDescending: true),
            ct
        );

        // Descending workflow rank: queried(4), assessment-in-progress(2), then
        // the two submitted(0) items broken newest-first by the tiebreak.
        Assert.Equal(s_expectedStatusRankDescending, OrgOrder(page));
    }

    [Fact]
    public async Task QueryAsync_sorted_path_paginates_and_reports_full_total()
    {
        var ct = TestContext.Current.CancellationToken;
        // Five items, page size 2, all sharing a status rank so the
        // newest-submitted tiebreak fixes the order: exercises the aggregation
        // $sort → $unset → $skip → $limit across more than one page and checks
        // TotalCount reflects the full filtered count, not the page size — so a
        // swapped $sort/$limit order or off-by-one $skip cannot stay green.
        await SeedAsync("e", submittedMinutesAgo: 4);
        await SeedAsync("a", submittedMinutesAgo: 0);
        await SeedAsync("d", submittedMinutesAgo: 3);
        await SeedAsync("b", submittedMinutesAgo: 1);
        await SeedAsync("c", submittedMinutesAgo: 2);

        var page1 = await _persistence.QueryAsync(
            new CaseQuery(Sort: "status", Page: 1, PageSize: 2),
            ct
        );
        var page2 = await _persistence.QueryAsync(
            new CaseQuery(Sort: "status", Page: 2, PageSize: 2),
            ct
        );
        var page3 = await _persistence.QueryAsync(
            new CaseQuery(Sort: "status", Page: 3, PageSize: 2),
            ct
        );

        Assert.Equal(s_expectedFirstPage, OrgOrder(page1));
        Assert.Equal(s_expectedSecondPage, OrgOrder(page2));
        Assert.Equal(s_expectedThirdPage, OrgOrder(page3));
        Assert.Equal(5, page1.TotalCount);
        Assert.Equal(5, page2.TotalCount);
        Assert.Equal(2, page1.PageSize);
        Assert.Equal(2, page2.Page);
    }

    [Fact]
    public async Task QueryAsync_sorted_path_still_strips_notes_and_audit_log()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = _time.GetUtcNow().UtcDateTime;
        var item = new Case
        {
            TypeId = "test",
            StateId = "submitted",
            SubmittedAt = now,
            LastModifiedAt = now,
            Payload = new BsonDocument { ["organisationName"] = "Acme" },
            Notes =
            {
                new CaseNote
                {
                    Text = "secret",
                    CreatedAt = now,
                    CreatedBy = "u",
                    CreatedByName = "U",
                },
            },
            AuditLog =
            {
                new CaseAuditEntry
                {
                    Action = "submitted",
                    ActionDisplayName = "Submitted",
                    CreatedAt = now,
                    CreatedBy = "u",
                    CreatedByName = "U",
                },
            },
        };
        await _persistence.CreateAsync(item, ct);

        var page = await _persistence.QueryAsync(new CaseQuery(Sort: "status"), ct);

        var got = Assert.Single(page.Items);
        // Projection preserved on the aggregation path, and the computed sort
        // fields were $unset so the document still deserialises cleanly.
        Assert.Empty(got.Notes);
        Assert.Empty(got.AuditLog);
        Assert.Equal("Acme", got.Payload["organisationName"].AsString);
    }

    [Fact]
    public async Task QueryAsync_sort_status_succeeds_when_no_case_types_are_registered()
    {
        var ct = TestContext.Current.CancellationToken;
        var persistence = new CasePersistence(
            _clientFactory,
            NullLoggerFactory.Instance,
            new CaseRegistry([])
        );
        await SeedAsync("only", stateId: "submitted");

        var page = await persistence.QueryAsync(new CaseQuery(Sort: "status"), ct);

        Assert.Equal("only", Assert.Single(page.Items).Payload["organisationName"].AsString);
    }

    // RA-412 (self-review): a real-Mongo round trip for the applicant-type
    // filter, specifically to prove the "absent field means Reprocessor"
    // semantics that a render-only test (CasePersistenceBuildFilterTests)
    // cannot — $regex/$not behaviour against a genuinely missing field is
    // exactly what the original version of this filter got wrong.

    [Fact]
    public async Task ReplaceAsync_throws_concurrency_exception_when_version_does_not_match()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var caseRecord = new Case
        {
            TypeId = "re-accreditation",
            StateId = "submitted",
            SubmittedAt = now,
            LastModifiedAt = now,
        };
        await _persistence.CreateAsync(caseRecord, TestContext.Current.CancellationToken);

        // Simulate two readers racing: load the doc twice, persist one,
        // then try to persist the stale copy. The stale copy's version
        // matches what the document had at read time but no longer
        // matches what is on disk, so the filter should not match and
        // ReplaceAsync should throw.
        var stale = await _persistence.GetByIdAsync(
            caseRecord.Id,
            TestContext.Current.CancellationToken
        );
        var fresh = await _persistence.GetByIdAsync(
            caseRecord.Id,
            TestContext.Current.CancellationToken
        );

        fresh!.StateId = "in-progress";
        await _persistence.ReplaceAsync(fresh, TestContext.Current.CancellationToken);

        stale!.StateId = "approved";
        var ex = await Assert.ThrowsAsync<CaseConcurrencyException>(() =>
            _persistence.ReplaceAsync(stale, TestContext.Current.CancellationToken)
        );
        Assert.Equal(caseRecord.Id, ex.CaseId);

        // The in-memory version on the failed attempt must roll back so
        // a caller-side retry does not double-increment.
        Assert.Equal(0, stale.Version);

        // The on-disk document is the winner's value, not the loser's.
        var final = await _persistence.GetByIdAsync(
            caseRecord.Id,
            TestContext.Current.CancellationToken
        );
        Assert.Equal("in-progress", final!.StateId);
    }

    // ---------------------- RA-291 SetPayloadFieldAsync ----------------------

    // ------------------- RA-342 legacy snapshot tolerance -------------------

    /// <summary>
    /// RA-342: a case whose frozen <c>templateSnapshot</c> was persisted
    /// under an older template carries a since-removed <c>requiredRoles</c>
    /// element on a transition (role tiering was dropped in RA-323). Before the
    /// fix, the default <c>BsonClassMapSerializer</c> rejected that unknown
    /// element with a <see cref="FormatException"/>, failing the whole worklist
    /// batch with a 500. Both the projected worklist query and the single-item
    /// fetch must now silently ignore the stray elements and return the item.
    ///
    /// The raw BSON is inserted directly (bypassing the typed serializer) so
    /// the stray elements land exactly as a legacy document holds them. Stray
    /// elements are seeded on every annotated snapshot type — the transition
    /// (the class in the reported stack trace), a state, a task and the
    /// snapshot root — so this single case exercises all four
    /// <c>[BsonIgnoreExtraElements]</c> annotations at once: if any were
    /// missing, deserialization would still throw.
    /// </summary>
    [Fact]
    public async Task QueryAsync_and_GetById_tolerate_a_legacy_snapshot_with_removed_fields()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = _time.GetUtcNow().UtcDateTime;
        var id = Guid.NewGuid();

        // Camel-cased element names to match the CamelCaseElementNameConvention
        // the production serializers register (MongoConversions).
        var legacyDoc = new BsonDocument
        {
            ["_id"] = id.ToString(),
            ["typeId"] = "re-accreditation",
            ["stateId"] = "submitted",
            ["submittedAt"] = new BsonDateTime(now),
            ["lastModifiedAt"] = new BsonDateTime(now),
            ["templateVersion"] = "1",
            ["version"] = 0,
            ["templateSnapshot"] = new BsonDocument
            {
                ["templateVersion"] = "1",
                // Snapshot-root stray element (CaseTemplateSnapshot).
                ["retiredSnapshotField"] = "legacy",
                ["states"] = new BsonArray
                {
                    // CaseState.Id maps to the "_id" element
                    // (default NamedIdMemberConvention), not "id".
                    new BsonDocument
                    {
                        ["_id"] = "submitted",
                        ["displayName"] = "Submitted",
                        ["isTerminal"] = false,
                        // State stray element (CaseState).
                        ["colour"] = "amber",
                    },
                    new BsonDocument
                    {
                        ["_id"] = "approved",
                        ["displayName"] = "Approved",
                        ["isTerminal"] = true,
                    },
                },
                ["transitions"] = new BsonArray
                {
                    new BsonDocument
                    {
                        ["actionId"] = "approve",
                        ["displayName"] = "Approve",
                        ["fromStateId"] = "submitted",
                        ["toStateId"] = "approved",
                        ["requiresAllTasksComplete"] = true,
                        // The exact element from the reported stack trace: an
                        // array of role names on a transition (CaseTransition).
                        ["requiredRoles"] = new BsonArray
                        {
                            "regulator-admin",
                            "regulator-approver",
                        },
                    },
                },
                // RA-410: tasksByState itself is now a retired, whole-sale-ignored
                // element on the snapshot root — the task framework (and the type
                // that modelled a single task) is gone entirely, not just one of
                // its fields. A snapshot frozen before RA-410 still carries this
                // sub-document, and it must keep deserialising rather than
                // taking the whole worklist batch down with it.
                ["tasksByState"] = new BsonDocument
                {
                    ["submitted"] = new BsonArray
                    {
                        new BsonDocument
                        {
                            ["_id"] = "task-one",
                            ["displayName"] = "Task One",
                            ["mandatory"] = true,
                        },
                    },
                },
            },
        };

        // Insert the raw document so the stray elements are stored verbatim,
        // exactly as a legacy case holds them.
        await _clientFactory
            .GetCollection<BsonDocument>("cases")
            .InsertOneAsync(legacyDoc, cancellationToken: ct);

        // Projected worklist path (CasePersistence.QueryAsync) — the code
        // path in the reported 500. Must not throw and must return the item.
        var page = await _persistence.QueryAsync(new CaseQuery(), ct);
        var listed = Assert.Single(page.Items);
        Assert.Equal(id, listed.Id);
        Assert.NotNull(listed.TemplateSnapshot);
        // Everything else deserialized normally, the stray fields simply ignored.
        var transition = Assert.Single(listed.TemplateSnapshot!.Transitions);
        Assert.Equal("approve", transition.ActionId);
        Assert.Equal("submitted", transition.FromStateId);

        // Single-item fetch path (CasePersistence.GetByIdAsync) must be
        // equally tolerant.
        var fetched = await _persistence.GetByIdAsync(id, ct);
        Assert.NotNull(fetched);
        Assert.Equal(id, fetched!.Id);
        Assert.NotNull(fetched.TemplateSnapshot);
        Assert.Equal(
            "Submitted",
            Assert.Single(fetched.TemplateSnapshot!.States, s => s.Id == "submitted").DisplayName
        );
    }

    // ------------------------- RA-551 payload.nation representation -------------------------

    // ----------------- RA-691 FindByPayloadFieldPrefixAsync / CorrectPayloadFieldAsync -----------------

    private async Task<Case> CreateWithAccreditationIdAsync(
        string accreditationId,
        string typeId = "re-accreditation",
        CancellationToken ct = default
    )
    {
        var now = s_seedNow.UtcDateTime;
        var caseRecord = new Case
        {
            TypeId = typeId,
            StateId = "approved",
            SubmittedAt = now,
            LastModifiedAt = now,
            SubmittedBy = "client-1",
        };
        caseRecord.ReplacePayload(new BsonDocument { ["accreditationId"] = accreditationId });
        caseRecord.AuditLog.Add(
            new CaseAuditEntry
            {
                Action = "accreditation-issued",
                ActionDisplayName = "Accreditation issued",
                CreatedAt = now,
            }
        );
        await _persistence.CreateAsync(caseRecord, ct);
        return caseRecord;
    }

}
