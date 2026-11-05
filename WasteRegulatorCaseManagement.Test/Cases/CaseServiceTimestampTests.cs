using WasteRegulatorCaseManagement.AuditEvents;
using System.Security.Claims;
using WasteRegulatorCaseManagement.Test.TestSupport;
using WasteRegulatorCaseManagement.Cases;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using NUlid;

namespace WasteRegulatorCaseManagement.Test.Cases;

/// <summary>
/// Regression coverage for epr-bwk: every persisted timestamp on
/// <see cref="Case"/> and <see cref="CaseTimelineEntry"/> must come from the injected
/// <see cref="TimeProvider"/>. The model types must NOT default their
/// timestamp properties to a wallclock value, otherwise tests that wire
/// up a <see cref="FakeTimeProvider"/> can be silently undermined when
/// the engine reads a model field it forgot to assign.
///
/// epr-efp: backed by ephemeral MongoDB so the assertions reflect what
/// the real driver actually persisted, not what the in-memory mock
/// captured. The previous implementation substituted
/// <see cref="ICasePersistence"/> wholesale.
/// </summary>
public class CaseServiceTimestampTests
    : IAsyncDisposable
{
    private const string CaseType = "test-type";
    private static readonly DateTimeOffset T = new(2026, 4, 27, 10, 0, 0, TimeSpan.Zero);

    private readonly TestMongoDbClientFactory _clientFactory;
    private readonly string _databaseName;
    private readonly CasePersistence _persistence;
    private readonly FakeTimeProvider _time = new(T);

    public CaseServiceTimestampTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("svc_timestamps");
        _clientFactory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _persistence = new CasePersistence(_clientFactory, NullLoggerFactory.Instance, new CaseRegistry([]), new AuditEventService(_clientFactory, TimeProvider.System));
    }

    public async ValueTask DisposeAsync() =>
        await _clientFactory.GetClient().DropDatabaseAsync(_databaseName);

    private CaseService BuildService(CaseModuleConfig type) =>
        new(
            new CaseRegistry([type]),
            _persistence,
            NullLogger<CaseService>.Instance,
            _time);

    private static CaseModuleConfig BuildType() =>
        TestCaseType.Create(
            CaseType,
            "Test type",
            initialState: new CaseState("submitted", "Submitted"),
            states: [new CaseState("submitted", "Submitted")]);

    private static ClaimsPrincipal User() =>
        new(new ClaimsIdentity(
        [
            new Claim("client_id", "test-client"),
            new Claim("user:nation", "ENGLAND"), new Claim("user:id", "alice-1"),
            new Claim("user:name", "Alice Example")
        ], "test"));

    private async Task<Case> SeedAsync(Case caseRecord)
    {
        await _clientFactory.GetCollection<Case>("cases").InsertOneAsync(caseRecord, cancellationToken: TestContext.Current.CancellationToken);
        return caseRecord;
    }

    private async Task<Case> GetAsync(string id)
    {
        var fetched = await _persistence.GetByIdAsync(id, TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        return fetched!;
    }

    [Fact]
    public async Task Submit_stamps_SubmittedAt_LastModifiedAt_and_birth_audit_from_TimeProvider()
    {
        var result = await BuildService(BuildType()).SubmitAsync(
            BuildType(),
            new CaseIntake(
                SourceSystemId: Ulid.NewUlid().ToString(),
                CallbackUrl: "https://source-system.test/callback",
                Nation: "ENGLAND",
                OrgId: "test-org",
                ProducerType: null,
                DisplayData: new Dictionary<string, CaseDisplayField>()),
            submittedBy: "test-client",
            User(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Message);

        var fetched = await GetAsync(result.Case!.Id);
        var expected = T.UtcDateTime;
        Assert.Equal(expected, fetched.SubmittedAt);
        Assert.Equal(expected, fetched.LastModifiedAt);
        var birth = Assert.Single(fetched.Timeline);
        Assert.Equal("case-created", birth.Action);
        Assert.Equal(expected, birth.PerformedAt);
    }

    [Fact]
    public async Task AddNote_records_CreatedAt_and_LastModifiedAt_from_advanced_TimeProvider()
    {
        var caseRecord = await SeedAsync(new Case
        {
            SourceSystemId = Ulid.NewUlid().ToString(),
            CallbackUrl = "https://source-system.test/callback",
            Nation = "ENGLAND",
            OrgId = "test-org",
            Id = Ulid.NewUlid().ToString(),
            CaseType = CaseType,
            StateId = "submitted",
            SubmittedAt = T.UtcDateTime,
            LastModifiedAt = T.UtcDateTime,
            SubmittedBy = "test-client"
        });

        _time.Advance(TimeSpan.FromMinutes(1));
        var expected = _time.GetUtcNow().UtcDateTime;

        var result = await BuildService(BuildType()).AddNoteAsync(
            caseRecord.Id, "Reviewed evidence.", User(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Message);

        var fetched = await GetAsync(caseRecord.Id);
        Assert.Equal(expected, fetched.LastModifiedAt);
        var audit = Assert.Single(fetched.Timeline);
        Assert.Equal("note-added", audit.Action);
        Assert.Equal(expected, audit.PerformedAt);
    }

    [Fact]
    public void Default_construction_leaves_timestamps_at_MinValue_so_engine_bugs_are_loud()
    {
        var caseRecord = new Case
        {
            SourceSystemId = Ulid.NewUlid().ToString(),
            CallbackUrl = "https://source-system.test/callback",
            Nation = "ENGLAND",
            OrgId = "test-org",
            CaseType = CaseType,
            StateId = "submitted",
        };
        Assert.Equal(default, caseRecord.SubmittedAt);
        Assert.Equal(default, caseRecord.LastModifiedAt);

        var entry = new CaseTimelineEntry { Action = "x", ActionDisplayName = "X" };
        Assert.Equal(default, entry.PerformedAt);
    }
}
