using System.Security.Claims;
using Microsoft.Extensions.Logging;

namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// Default <see cref="ICaseTimelineAppender"/>. Re-loads the case
/// from <see cref="ICasePersistence"/> before each save so it picks
/// up any audit entries written by the engine immediately before the
/// hook fires.
/// </summary>
internal sealed class CaseTimelineAppender(
    ICasePersistence persistence,
    ILogger<CaseTimelineAppender> logger,
    TimeProvider? timeProvider = null) : ICaseTimelineAppender
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
            caseRecord.Timeline.Add(new CaseTimelineEntry
            {
                Action = action,
                ActionDisplayName = actionDisplayName,
                Details = details,
                PerformedAt = now,
                PerformedById = user.FindFirstValue("user:id"),
                PerformedByName = user.FindFirstValue("user:name"),
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
