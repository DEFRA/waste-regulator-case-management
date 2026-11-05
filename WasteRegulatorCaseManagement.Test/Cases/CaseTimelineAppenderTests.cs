using System.Security.Claims;
using WasteRegulatorCaseManagement.Test.TestSupport;
using WasteRegulatorCaseManagement.Cases;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUlid;

namespace WasteRegulatorCaseManagement.Test.Cases;

/// <summary>
/// Uses real ephemeral MongoDB so concurrency / retry behaviour is
/// exercised against the actual optimistic-concurrency implementation.
/// </summary>
public class CaseTimelineAppenderTests
    : IAsyncDisposable
{
    private readonly TestMongoDbClientFactory _clientFactory;
    private readonly string _databaseName;
    private readonly CasePersistence _persistence;

    private static readonly ClaimsPrincipal s_user = new(new ClaimsIdentity(
    [
        new Claim("user:nation", "ENGLAND"), new Claim("user:id", "user-1"),
        new Claim("user:name", "Alice")
    ], "test"));

    public CaseTimelineAppenderTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("audit-appender");
        _clientFactory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _persistence = new CasePersistence(_clientFactory, NullLoggerFactory.Instance, new CaseRegistry([]));
    }

    public async ValueTask DisposeAsync() =>
        await _clientFactory.GetClient().DropDatabaseAsync(_databaseName);

    private CaseTimelineAppender BuildSut() =>
        new(_persistence, NullLogger<CaseTimelineAppender>.Instance);

    private async Task<Case> CreateCaseAsync(CancellationToken ct)
    {
        var caseRecord = new Case
        {
            SourceSystemId = Ulid.NewUlid().ToString(),
            CallbackUrl = "https://source-system.test/callback",
            Nation = "ENGLAND",
            OrgId = "test-org",
            CaseType = "test-type",
            StateId = "submitted",
        };
        await _persistence.CreateAsync(caseRecord, ct);
        return caseRecord;
    }

    [Fact]
    public async Task AppendAsync_adds_audit_entry_to_persisted_case()
    {
        var ct = TestContext.Current.CancellationToken;
        var caseRecord = await CreateCaseAsync(ct);
        var sut = BuildSut();

        var ok = await sut.AppendAsync(
            caseRecord.Id,
            action: "notification-sent",
            actionDisplayName: "Email sent",
            details: new Dictionary<string, string?> { ["templateKey"] = "DulyMade", ["recipient"] = "op@ex.com" },
            s_user,
            ct);

        Assert.True(ok);

        var persisted = await _persistence.GetByIdAsync(caseRecord.Id, ct);
        Assert.NotNull(persisted);
        var entry = Assert.Single(persisted!.Timeline);
        Assert.Equal("notification-sent", entry.Action);
        Assert.Equal("Email sent", entry.ActionDisplayName);
        Assert.Equal("DulyMade", entry.Details?["templateKey"]);
        Assert.Equal("user-1", entry.PerformedById);
        Assert.Equal("Alice", entry.PerformedByName);
        // epr-rr9s: out-of-band appends snapshot the case's current state.
        Assert.Equal("submitted", entry.StateId);
    }

    [Fact]
    public async Task AppendAsync_returns_false_when_case_not_found()
    {
        var sut = BuildSut();

        var ok = await sut.AppendAsync(
            Ulid.NewUlid().ToString(),
            action: "notification-sent",
            actionDisplayName: "Email sent",
            details: new Dictionary<string, string?>(),
            s_user,
            TestContext.Current.CancellationToken);

        Assert.False(ok);
    }

    [Fact]
    public async Task AppendAsync_appends_multiple_entries_sequentially()
    {
        var ct = TestContext.Current.CancellationToken;
        var caseRecord = await CreateCaseAsync(ct);
        var sut = BuildSut();

        await sut.AppendAsync(caseRecord.Id, "first", "First", new(), s_user, ct);
        await sut.AppendAsync(caseRecord.Id, "second", "Second", new(), s_user, ct);

        var persisted = await _persistence.GetByIdAsync(caseRecord.Id, ct);
        Assert.Equal(2, persisted!.Timeline.Count);
        Assert.Equal("first", persisted.Timeline[0].Action);
        Assert.Equal("second", persisted.Timeline[1].Action);
    }

    [Fact]
    public async Task AppendAsync_retries_and_succeeds_after_one_concurrency_exception()
    {
        // Use a mock persistence so we can inject a controlled
        // CaseConcurrencyException on the first ReplaceAsync call
        // and verify the appender retries and ultimately succeeds.
        var ct = TestContext.Current.CancellationToken;
        var caseId = Ulid.NewUlid().ToString();
        var storedCase = new Case
        {
            SourceSystemId = Ulid.NewUlid().ToString(),
            CallbackUrl = "https://source-system.test/callback",
            Nation = "ENGLAND",
            OrgId = "test-org",
            Id = caseId,
            CaseType = "test-type",
            StateId = "submitted",
        };

        var mockPersistence = Substitute.For<ICasePersistence>();
        mockPersistence.GetByIdAsync(caseId, ct).Returns(storedCase);

        var replaceCallCount = 0;
        mockPersistence.ReplaceAsync(Arg.Any<Case>(), ct)
            .Returns(_ =>
            {
                replaceCallCount++;
                if (replaceCallCount == 1)
                {
                    throw new CaseConcurrencyException(caseId, 0);
                }

                return Task.CompletedTask;
            });

        var sut = new CaseTimelineAppender(mockPersistence, NullLogger<CaseTimelineAppender>.Instance);

        var ok = await sut.AppendAsync(
            caseId,
            action: "notification-sent",
            actionDisplayName: "Email sent",
            details: new Dictionary<string, string?>(),
            s_user,
            ct);

        Assert.True(ok);
        Assert.Equal(2, replaceCallCount);
        await mockPersistence.Received(2).GetByIdAsync(caseId, ct);
    }
}
