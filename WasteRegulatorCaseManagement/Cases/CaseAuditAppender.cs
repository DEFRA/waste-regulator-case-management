using System.Security.Claims;
using Microsoft.Extensions.Logging;

namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// Default <see cref="ICaseAuditAppender"/>. Re-loads the case
/// from <see cref="ICasePersistence"/> before each save so it picks
/// up any audit entries written by the engine immediately before the
/// hook fires.
/// </summary>
internal sealed class CaseAuditAppender(
    ICasePersistence persistence,
    ILogger<CaseAuditAppender> logger,
    TimeProvider? timeProvider = null) : ICaseAuditAppender
{
    private const int MaxAttempts = 3;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<bool> AppendAsync(
        string caseId,
        string action,
        string actionDisplayName,
        Dictionary<string, string?> details,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var caseRecord = await persistence.GetByIdAsync(caseId, cancellationToken);
            if (caseRecord is null)
            {
                logger.LogWarning(
                    "Out-of-band audit append skipped: case {CaseId} not found.", caseId);
                return false;
            }

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            caseRecord.AuditLog.Add(new CaseAuditEntry
            {
                Action = action,
                ActionDisplayName = actionDisplayName,
                Details = details,
                CreatedAt = now,
                CreatedBy = user.FindFirstValue("user:id"),
                CreatedByName = user.FindFirstValue("user:name"),
                // epr-rr9s: snapshot the case's current state at append time.
                StateId = caseRecord.StateId
            });

            try
            {
                await persistence.ReplaceAsync(caseRecord, cancellationToken);
                return true;
            }
            catch (CaseConcurrencyException)
            {
                if (attempt == MaxAttempts)
                {
                    logger.LogError(
                        "Out-of-band audit append for case {CaseId} abandoned after {Attempts} attempts.",
                        caseId, MaxAttempts);
                    return false;
                }
            }
        }

        return false;
    }
}
