using System.Security.Claims;
using WasteRegulatorCaseManagement.Cases;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace WasteRegulatorCaseManagement.Test.Cases;

/// <summary>
/// Behaviour for template versioning and snapshot capture: the engine must
/// reason about a case against the snapshot stored on it (not the live
/// type) so that historical cases keep rendering as they did at the time
/// they were assessed, even after the live module's templates change.
/// </summary>
public class CaseTemplateVersioningTests
{
    private const string TypeId = "test-type";
    private static readonly DateTime Now = new(2026, 4, 27, 10, 0, 0, DateTimeKind.Utc);

    private readonly ICasePersistence _persistence = Substitute.For<ICasePersistence>();

    private static ClaimsPrincipal User() =>
        new(new ClaimsIdentity(
        [
            new Claim("client_id", "test-client"),
            new Claim("user:id", "test-user")
        ], "test"));

    [Fact]
    public void Capture_freezes_states_and_transitions()
    {
        var type = new VersionedTestType(
            templateVersion: "v3",
            states:
            [
                new CaseState("submitted", "Submitted"),
                new CaseState("approved", "Approved", IsTerminal: true)
            ],
            transitions: [new CaseTransition("approve", "Approve", "submitted", "approved")]);

        var snapshot = CaseTemplateSnapshot.Capture(type);

        Assert.Equal("v3", snapshot.TemplateVersion);
        Assert.Equal(2, snapshot.States.Count);
        Assert.Single(snapshot.Transitions);
    }

    [Fact]
    public void Project_uses_stored_snapshot_in_preference_to_live_type()
    {
        // The case was assessed against v1 (one task: "check"). The live
        // type has since been bumped to v2 and renamed the task to "review".
        var snapshot = CaseTemplateSnapshot.Capture(new VersionedTestType(
            templateVersion: "v1",
            states: [new CaseState("submitted", "Submitted")],
            transitions: []));

        var liveType = new VersionedTestType(
            templateVersion: "v2",
            states: [new CaseState("submitted", "Submitted")],
            transitions: []);

        var caseRecord = new Case
        {
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedAt = Now,
            LastModifiedAt = Now,
            TemplateSnapshot = snapshot,
            TemplateVersion = snapshot.TemplateVersion
        };

        var projection = BuildService(liveType).Project(caseRecord);

        Assert.Equal("v1", projection.TemplateVersion);
    }

    [Fact]
    public async Task ApplyAction_uses_snapshot_so_actions_removed_from_live_type_still_apply()
    {
        // Snapshot keeps an "approve" transition that the live type has dropped.
        var snapshot = CaseTemplateSnapshot.Capture(new VersionedTestType(
            templateVersion: "v1",
            states:
            [
                new CaseState("submitted", "Submitted"),
                new CaseState("approved", "Approved", IsTerminal: true)
            ],
            transitions: [new CaseTransition("approve", "Approve", "submitted", "approved")]));

        var liveType = new VersionedTestType(
            templateVersion: "v2",
            states: [new CaseState("submitted", "Submitted")],
            transitions: []);

        var caseRecord = new Case
        {
            Id = Guid.NewGuid(),
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedAt = Now,
            LastModifiedAt = Now,
            TemplateSnapshot = snapshot,
            TemplateVersion = snapshot.TemplateVersion
        };
        _persistence.GetByIdAsync(caseRecord.Id, Arg.Any<CancellationToken>()).Returns(caseRecord);

        var result = await BuildService(liveType).ApplyActionAsync(
            caseRecord.Id, "approve", User(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("approved", caseRecord.StateId);
    }

    [Fact]
    public void Project_falls_back_to_live_type_when_legacy_item_has_no_snapshot()
    {
        var liveType = new VersionedTestType(
            templateVersion: "v1",
            states: [new CaseState("submitted", "Submitted")],
            transitions: []);

        var legacy = new Case
        {
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedAt = Now,
            LastModifiedAt = Now
            // No TemplateSnapshot, no TemplateVersion.
        };

        var projection = BuildService(liveType).Project(legacy);

        Assert.Equal("v1", projection.TemplateVersion);
    }

    [Fact]
    public void Project_returns_unknown_version_when_legacy_item_has_no_module()
    {
        // No live type registered, no snapshot — engine still produces a
        // displayable projection so the audit UI can render the envelope.
        var liveType = new VersionedTestType(
            templateVersion: "v1",
            states: [new CaseState("submitted", "Submitted")],
            transitions: []);
        var registry = new CaseRegistry([liveType]);

        var orphan = new Case
        {
            TypeId = "different-type",
            StateId = "anything"
        };

        var projection = new CaseService(registry, _persistence, NullLogger<CaseService>.Instance)
            .Project(orphan);

        Assert.Equal("unknown", projection.TemplateVersion);
        Assert.Empty(projection.AvailableActions);
    }

    private CaseService BuildService(ICaseType type) =>
        new(new CaseRegistry([type]), _persistence, NullLogger<CaseService>.Instance);

    /// <summary>
    /// Test fixture exposing TemplateVersion override so we can register
    /// multiple incompatible template versions side by side.
    /// </summary>
    private sealed class VersionedTestType : ICaseType
    {
        public VersionedTestType(
            string templateVersion,
            IReadOnlyCollection<CaseState> states,
            IReadOnlyCollection<CaseTransition> transitions)
        {
            TemplateVersion = templateVersion;
            States = states;
            InitialState = states.First();
            Transitions = transitions;
        }

        public string TypeId => "test-type";
        public string DisplayName => "Versioned test type";
        public CaseState InitialState { get; }
        public IReadOnlyCollection<CaseState> States { get; }
        public IReadOnlyCollection<CaseTransition> Transitions { get; }
        public string TemplateVersion { get; }
    }
}