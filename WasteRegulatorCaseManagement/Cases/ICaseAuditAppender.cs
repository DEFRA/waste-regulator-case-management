using System.Security.Claims;

namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// Out-of-band audit-log appender used by post-action hooks to record
/// their own outcome on the persisted <see cref="Case"/>. Loads the latest copy,
/// appends an entry, and saves with one optimistic-concurrency retry
/// so a race with another writer does not silently drop the entry.
/// </summary>
public interface ICaseAuditAppender
{
    /// <summary>
    /// Append a single <see cref="CaseAuditEntry"/> to the work
    /// item identified by <paramref name="caseId"/>. Returns
    /// <c>true</c> when the entry was persisted; <c>false</c> when the
    /// case could not be found or the write was abandoned after
    /// retries.
    /// </summary>
    Task<bool> AppendAsync(
        Guid caseId,
        string action,
        string actionDisplayName,
        Dictionary<string, string?> details,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default);
}
