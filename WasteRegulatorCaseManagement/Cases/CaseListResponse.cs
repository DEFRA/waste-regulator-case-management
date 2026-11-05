using System.Text.Json;

namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// API representation of a single page of cases returned by
/// <c>GET /cases</c>. Mirrors <see cref="CasePage"/> but carries
/// API-shaped <see cref="CaseListItemResponse"/>s — a slimmer
/// per-item shape than <see cref="CaseResponse"/> that omits the
/// per-item <c>Timeline</c> (epr-4pf). A 100-row page of an item with
/// chatty caseworker activity would otherwise ship the entire history for
/// every row even though
/// the list view never displays them; the slim shape keeps the wire
/// payload bounded by the envelope size.
///
/// Callers that need the full timeline must hit
/// <c>GET /cases/{id}</c>, which still returns the unmodified
/// <see cref="CaseResponse"/>.
/// </summary>
public sealed record CaseListResponse(
    IReadOnlyList<CaseListItemResponse> Items,
    long TotalCount,
    int Page,
    int PageSize);

/// <summary>
/// Per-item shape returned by the list endpoint (epr-4pf). Mirrors
/// <see cref="CaseResponse"/> exactly except that the
/// <c>Timeline</c> is omitted entirely (the JSON property does not exist on the wire — they are not
/// emitted-as-null) so list responses stay small. Every other field —
/// available actions, assignment snapshot, template version — is kept so
/// a list view can render rich rows without a per-row round-trip.
/// </summary>
public sealed record CaseListItemResponse(
    string Id,
    string TypeId,
    string StateId,
    DateTime SubmittedAt,
    DateTime LastModifiedAt,
    string? SubmittedBy,
    string TemplateVersion,
    JsonElement Payload,
    IReadOnlyCollection<CaseTransition> AvailableActions,
    string? AssignedToId = null,
    string? AssignedToName = null,
    DateTime? AssignedAt = null,
    string? AssignedBy = null);