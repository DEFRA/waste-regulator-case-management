using MongoDB.Bson.Serialization.Attributes;
using NUlid;

namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// A persisted case. The framework owns the envelope (id, type, state,
/// timestamps, submitted-by, payload); modules describe what their payload
/// means via their <see cref="ICaseType"/> and operate on it via their
/// own service objects.
///
/// RA-410: ignores extra BSON elements. The task framework's
/// <c>completedTaskIdsByState</c> / <c>taskStatusesByState</c> fields were
/// removed from this model but remain on every document persisted before that
/// change; without this attribute each of those documents would throw a
/// <see cref="System.FormatException"/> on read and take the whole worklist
/// batch down with it. Stale fields are simply ignored and disappear from a
/// document the next time it is written, so no backfill is needed. This also
/// makes a rolling deploy safe in both directions.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class Case
{
    [BsonId]
    public string Id { get; init; } = Ulid.NewUlid().ToString();

    /// <summary>The <see cref="ICaseType.CaseType"/> this item is an instance of.</summary>
    public required string CaseType { get; init; }

    /// <summary>Current <see cref="CaseState.Id"/>. Set to the type's initial state on creation.</summary>
    public required string StateId { get; set; }

    /// <summary>
    /// Whether <see cref="StateId"/> is a terminal state of this case's
    /// template. Written with every state change so completed cases can be
    /// queried without knowing which state ids are terminal for each type.
    /// </summary>
    public bool IsTerminal { get; set; }

    /// <summary>
    /// UTC timestamp the case was first accepted into the system. Has no
    /// default initializer — the engine must always stamp this from the
    /// injected <see cref="TimeProvider"/> at submission so tests with a
    /// <c>FakeTimeProvider</c> are not silently undermined by a wallclock
    /// fallback. An unset value (<see cref="DateTime.MinValue"/>) signals a
    /// construction-site bug.
    /// </summary>
    public DateTime SubmittedAt { get; init; }

    /// <summary>
    /// UTC timestamp of the last engine-driven mutation (state
    /// transition). Equal to <see cref="SubmittedAt"/> for a freshly-submitted
    /// item. Has no default initializer for the same reason as
    /// <see cref="SubmittedAt"/>.
    /// </summary>
    public DateTime LastModifiedAt { get; set; }

    /// <summary>Identifier of the upstream caller that submitted the item (CDP client id).</summary>
    public string? SubmittedBy { get; init; }

    /// <summary>
    /// Identifier of the user the case is currently assigned to, or
    /// <c>null</c> when no one is assigned. Set via the assignment endpoints
    /// rather than directly by modules so the engine can enforce role-based
    /// rules consistently.
    /// </summary>
    public string? AssignedToId { get; set; }

    /// <summary>
    /// Human-readable name of the assignee (snapshotted at assignment time so
    /// list views do not need a separate user lookup). <c>null</c> when no one
    /// is assigned.
    /// </summary>
    public string? AssignedToName { get; set; }

    /// <summary>UTC timestamp the current assignment was made; <c>null</c> when unassigned.</summary>
    public DateTime? AssignedAt { get; set; }

    /// <summary>Identifier of the user who made the current assignment; <c>null</c> when unassigned.</summary>
    public string? AssignedBy { get; set; }

    /// <summary>
    /// Frozen copy of the type's template (states,
    /// transitions and version) captured at submission time. Used by the
    /// engine in preference to the live <see cref="ICaseType"/> so that
    /// the case — and its audit history — keep rendering as they did at
    /// the time they were assessed, even when the live module's template
    /// changes later. Optional only to support legacy items submitted before
    /// versioning existed.
    /// </summary>
    public CaseTemplateSnapshot? TemplateSnapshot { get; set; }

    /// <summary>
    /// Convenience copy of <see cref="CaseTemplateSnapshot.TemplateVersion"/>
    /// so it can be queried/indexed without deserialising the whole snapshot.
    /// </summary>
    public string? TemplateVersion { get; set; }

    /// <summary>
    /// Immutable reference to the submission in the source system. A source
    /// system creates at most one case per reference, so it is unique.
    /// </summary>
    public required string SourceSystemId { get; init; }

    /// <summary>
    /// Where this service reports the case's outcome to the source system
    /// when it reaches a terminal state. Supplied at intake and stored
    /// unchanged.
    /// </summary>
    public required string CallbackUrl { get; init; }

    /// <summary>
    /// The UK nation the case belongs to, one of <see cref="CaseNations.All"/>.
    /// Scopes which regulators can see and act on it.
    /// </summary>
    public required string Nation { get; init; }

    /// <summary>The submitting organisation's id in the source system.</summary>
    public required string OrgId { get; init; }

    /// <summary>
    /// The source system's classification of the submitter (for CSoC,
    /// <c>DIRECT_PRODUCER</c> or <c>COMPLIANCE_SCHEME</c>). Stored and
    /// returned uninterpreted.
    /// </summary>
    public string? ProducerType { get; init; }

    /// <summary>
    /// Fields the caseworker dashboard renders in the case list, supplied by
    /// the source system at intake so listing never joins back to it. Stored
    /// and returned without interpretation.
    /// </summary>
    public Dictionary<string, CaseDisplayField> DisplayData { get; init; } = new();

    /// <summary>
    /// Append-only system timeline (RA-97). The framework writes one entry
    /// here for every successful state-changing engine call (case created,
    /// state changed, assigned / unassigned, note added — a note's text lives
    /// on its note-added entry). Entries are
    /// stored in chronological (insertion) order and projected
    /// reverse-chronologically (newest-first, RA-568) on the wire so a UI
    /// renders the most recent activity at the top of the timeline.
    /// Framework-owned so every case type inherits the same audit
    /// behaviour without writing any audit code.
    /// </summary>
    public List<CaseTimelineEntry> Timeline { get; init; } = new();

    /// <summary>
    /// Optimistic concurrency token. Incremented by
    /// <see cref="ICasePersistence.ReplaceAsync"/> on every successful
    /// save and used as a filter so two concurrent writers cannot silently
    /// overwrite one another's changes.
    /// </summary>
    public int Version { get; set; }
}