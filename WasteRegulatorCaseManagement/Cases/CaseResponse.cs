using System.Text.Json;

namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// API representation of a persisted case. Mirrors <see cref="Case"/>
/// but carries the payload as a JSON element so callers do not see BSON types,
/// and projects engine state (the actions the engine will currently allow) so
/// a UI can render without re-deriving it.
///
/// <see cref="TemplateVersion"/> exposes the version of the type's template
/// the case was assessed against, so a UI can pick a matching detail
/// template for faithful historical rendering.
/// </summary>
public sealed record CaseResponse(
    string Id,
    string TypeId,
    string StateId,
    bool IsTerminal,
    DateTime SubmittedAt,
    DateTime LastModifiedAt,
    string? SubmittedBy,
    string TemplateVersion,
    JsonElement Payload,
    IReadOnlyCollection<CaseTransition> AvailableActions,
    string? AssignedToId = null,
    string? AssignedToName = null,
    DateTime? AssignedAt = null,
    string? AssignedBy = null,
    IReadOnlyCollection<CaseTimelineEntryResponse>? Timeline = null
);

/// <summary>
/// Wire shape for a single timeline entry (RA-97). Returned in
/// reverse-chronological (newest-first) order as part of
/// <see cref="CaseResponse.Timeline"/> so a UI can render a top-to-
/// bottom timeline, most recent first, without re-sorting (RA-568).
/// </summary>
public sealed record CaseTimelineEntryResponse(
    string Id,
    string Action,
    string ActionDisplayName,
    IReadOnlyDictionary<string, string?> Details,
    DateTime CreatedAt,
    string? CreatedBy,
    string? CreatedByName,
    // epr-rr9s: case state as of this event (serialises to stateId).
    // Null for pre-epr-rr9s entries with no snapshot; the frontend omits the
    // State row in that case.
    string? StateId = null
);
