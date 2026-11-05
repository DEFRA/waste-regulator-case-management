using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text.Json;
using WasteRegulatorCaseManagement.Auth;
using WasteRegulatorCaseManagement.Utils.Logging;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace WasteRegulatorCaseManagement.Cases;

// Marker type for IStructuredLogger category — CaseEndpoints is a
// static class and therefore cannot itself be used as a type argument.
internal sealed class CaseEndpointsLogger;

/// <summary>
/// Framework-level HTTP endpoints for ingesting and listing cases. The
/// envelope (id, type, state, submitted-by, payload) is owned by the
/// framework; type-specific behaviour and routes are added by modules under
/// <c>/cases/&lt;type-id&gt;/...</c>.
/// </summary>
public static class CaseEndpoints
{
    private const string AbsentValue = "(absent)";
    private const string CallerClientIdField = "caller.client_id";
    private const string ErrorMessageField = "error.message";
    private const string InvalidRequestTitle = "Invalid request";
    private const string CaseTypeField = "case.case_type";

    // Request body size caps (epr-rvz). The case endpoints all parse
    // their JSON body manually after .DisableValidation(), so without an
    // explicit cap an attacker can POST arbitrarily large payloads and
    // force in-memory JSON / BSON parsing before any size guard fires.
    // The caps are deliberately generous for the legitimate use cases
    // (a real submission payload is well under 1 MB; a note is well under
    // 100 KB; status / assign carry just a couple of small string fields).
    public const long MaxSubmitBodyBytes = 1 * 1024 * 1024; // 1 MB
    public const long MaxNoteBodyBytes = 100 * 1024; // 100 KB
    public const long MaxAssignBodyBytes = 10 * 1024; // 10 KB

    [ExcludeFromCodeCoverage]
    public static IEndpointRouteBuilder MapCaseFrameworkEndpoints(
        this IEndpointRouteBuilder app
    )
    {
        var group = app.MapGroup("/cases").WithTags("Cases");

        group
            .MapPost(string.Empty, Submit)
            .WithName("SubmitCase")
            .DisableValidation()
            .WithMetadata(new RequestSizeLimitAttribute(MaxSubmitBodyBytes))
            .RequireAuthorization(CaseIntakePolicy.Name);

        group.MapGet("/{id}", GetById).WithName("GetCaseById").RequireAuthorization();

        group.MapGet(string.Empty, GetAll).WithName("ListCases").RequireAuthorization();

        group
            .MapPost("/{id}/actions/{actionId}", ApplyAction)
            .WithName("ApplyCaseAction")
            .RequireAuthorization();

        group
            .MapPost("/{id}/assign", Assign)
            .WithName("AssignCase")
            .DisableValidation()
            .WithMetadata(new RequestSizeLimitAttribute(MaxAssignBodyBytes))
            .RequireAuthorization();

        group
            .MapPost("/{id}/unassign", Unassign)
            .WithName("UnassignCase")
            .RequireAuthorization();

        group
            .MapPost("/{id}/notes", AddNote)
            .WithName("AddCaseNote")
            .DisableValidation()
            .WithMetadata(new RequestSizeLimitAttribute(MaxNoteBodyBytes))
            .RequireAuthorization();

        return app;
    }

    internal static async Task<Results<CreatedAtRoute<CaseResponse>, Ok<CaseResponse>, ProblemHttpResult>> Submit(
        JsonElement body,
        HttpContext httpContext,
        [FromServices] ICaseRegistry registry,
        [FromServices] ICaseService engine,
        [FromServices] IStructuredLogger<CaseEndpointsLogger> log,
        CancellationToken cancellationToken
    )
    {
        var req = httpContext.Request;
        var bodyText = body.ValueKind != JsonValueKind.Undefined ? body.GetRawText() : "(empty)";
        // Truncate very large bodies in the log — the 1 MB cap still applies
        // at the framework level; this is just for readability.
        const int MaxLoggedBodyChars = 4096;
        var loggedBody =
            bodyText.Length > MaxLoggedBodyChars
                ? bodyText[..MaxLoggedBodyChars] + $"…(truncated, total {bodyText.Length} chars)"
                : bodyText;

        log.Log(
            LogLevel.Information,
            "Case submission received",
            new Dictionary<string, object?>
            {
                ["http.request.method"] = req.Method,
                ["url.path"] = req.Path.Value,
                ["http.request.body"] = loggedBody,
                [CallerClientIdField] = req.Headers.TryGetValue(
                    "x-cdp-client-id",
                    out var cid
                )
                    ? cid.ToString()
                    : AbsentValue,
                ["caller.user_id"] = req.Headers.TryGetValue("x-cdp-user-id", out var uid)
                    ? uid.ToString()
                    : AbsentValue,
                ["caller.user_name"] = req.Headers.TryGetValue("x-cdp-user-name", out var uname)
                    ? uname.ToString()
                    : AbsentValue,
                ["http.request.mime_type"] = req.ContentType ?? AbsentValue,
                ["http.request.body.bytes"] = req.ContentLength?.ToString() ?? AbsentValue,
            }
        );

        if (body.ValueKind != JsonValueKind.Object)
        {
            log.Log(
                LogLevel.Warning,
                "Case submission rejected: body is not a JSON object",
                new Dictionary<string, object?>
                {
                    [ErrorMessageField] = $"ValueKind was {body.ValueKind}",
                }
            );
            return BadRequest(InvalidRequestTitle, "Request body must be a JSON object.");
        }

        if (
            !body.TryGetProperty("caseType", out var caseTypeElement)
            || caseTypeElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(caseTypeElement.GetString())
        )
        {
            log.Log(
                LogLevel.Warning,
                "Case submission rejected: missing or empty caseType",
                new Dictionary<string, object?> { ["http.request.body"] = loggedBody }
            );
            return BadRequest(
                InvalidRequestTitle,
                "'caseType' is required and must be a non-empty string."
            );
        }

        var caseType = caseTypeElement.GetString()!;
        var type = registry.Find(caseType);
        if (type is null)
        {
            log.Log(
                LogLevel.Warning,
                "Case submission rejected: unknown caseType",
                new Dictionary<string, object?> { [CaseTypeField] = caseType }
            );
            return BadRequest(
                "Unknown case type",
                $"No case type is registered with id '{caseType}'."
            );
        }

        if (CaseIntakeBinding.TryRead(body, out var intake) is { } intakeError)
        {
            log.Log(
                LogLevel.Warning,
                "Case submission rejected: invalid intake fields",
                new Dictionary<string, object?>
                {
                    [CaseTypeField] = caseType,
                    [ErrorMessageField] = intakeError,
                }
            );
            return BadRequest(InvalidRequestTitle, intakeError);
        }

        if (
            intake!.InitialStateId is { } initialStateId
            && !type.States.Any(s => string.Equals(s.Id, initialStateId, StringComparison.Ordinal))
        )
        {
            return BadRequest(
                InvalidRequestTitle,
                $"'initialState' must be a state of case type '{caseType}'."
            );
        }

        var submittedBy =
            httpContext.User.FindFirstValue("client_id")
            ?? httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

        // RA-126: optional caller-supplied audit context. 'source' is a
        // string when present; reject other JSON types up front so a
        // malformed body cannot silently degrade the audit record.
        Dictionary<string, string?>? submissionMetadata = null;
        if (body.TryGetProperty("source", out var sourceElement))
        {
            if (sourceElement.ValueKind != JsonValueKind.String)
            {
                log.Log(
                    LogLevel.Warning,
                    "Case submission rejected: 'source' is not a string",
                    new Dictionary<string, object?>
                    {
                        [CaseTypeField] = caseType,
                        [ErrorMessageField] = $"'source' ValueKind was {sourceElement.ValueKind}",
                    }
                );
                return BadRequest("Invalid request body", "'source' must be a string.");
            }
            (submissionMetadata ??= new Dictionary<string, string?>(StringComparer.Ordinal))[
                "source"
            ] = sourceElement.GetString();
        }

        // Routed through the engine so the framework owns audit-log
        // composition for the birth event in the same place it owns every
        // other state-changing entry. The engine writes the document and
        // its first 'case-created' audit entry in a single
        // CreateAsync call.
        CaseActionResult result;
        try
        {
            result = await engine.SubmitAsync(
                type,
                intake!,
                submittedBy,
                httpContext.User,
                submissionMetadata,
                cancellationToken
            );
        }
        catch (Exception ex)
        {
            log.Log(
                LogLevel.Error,
                "Case submission threw an unhandled exception",
                new Dictionary<string, object?>
                {
                    [CaseTypeField] = caseType,
                    [CallerClientIdField] = submittedBy ?? "(unknown)",
                    ["error.type"] = ex.GetType().FullName,
                    [ErrorMessageField] = ex.Message,
                },
                ex
            );
            throw;
        }

        if (!result.IsSuccess)
        {
            log.Log(
                LogLevel.Warning,
                "Case submission failed",
                new Dictionary<string, object?>
                {
                    [CaseTypeField] = caseType,
                    [CallerClientIdField] = submittedBy ?? "(unknown)",
                    ["error.code"] = result.FailureCode.ToString(),
                    [ErrorMessageField] = result.Message,
                }
            );
            return result.FailureCode switch
            {
                CaseActionFailureCode.MissingActorIdentity => TypedResults.Problem(
                    title: "Authentication required",
                    detail: result.Message,
                    statusCode: StatusCodes.Status401Unauthorized
                ),
                _ => TypedResults.Problem(
                    title: InvalidRequestTitle,
                    detail: result.Message,
                    statusCode: StatusCodes.Status400BadRequest
                ),
            };
        }

        var caseRecord = result.Case!;
        log.Log(
            LogLevel.Information,
            result.IsIdempotentReplay
                ? "Case submission matched an existing case"
                : "Case submission succeeded",
            new Dictionary<string, object?>
            {
                ["case.id"] = caseRecord.Id,
                [CaseTypeField] = caseType,
                [CallerClientIdField] = submittedBy ?? "(unknown)",
            }
        );
        var response = ToResponse(engine.Project(caseRecord));
        // A resent submission gets the case it already created, flagged with
        // the same header the other idempotent mutations use, so the Case
        // Intake Processor can discard the event.
        if (result.IsIdempotentReplay)
        {
            httpContext.Response.Headers[IdempotentReplayHeader] = "true";
            return TypedResults.Ok(response);
        }
        return TypedResults.CreatedAtRoute(response, "GetCaseById", new { id = caseRecord.Id });
    }

    private static ProblemHttpResult NationRequired() =>
        TypedResults.Problem(
            title: "Not authorised",
            detail: "Reading cases requires a 'user:nation' claim scoping the caller to a nation.",
            statusCode: StatusCodes.Status403Forbidden
        );

    private static ProblemHttpResult BadRequest(string title, string detail) =>
        TypedResults.Problem(
            title: title,
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest
        );

    internal static async Task<Results<Ok<CaseResponse>, NotFound, ProblemHttpResult>> GetById(
        [FromRoute] string id,
        HttpContext httpContext,
        [FromServices] ICasePersistence persistence,
        [FromServices] ICaseService engine,
        CancellationToken cancellationToken
    )
    {
        if (CaseNationScope.CallerNation(httpContext.User) is null)
        {
            return NationRequired();
        }

        var caseRecord = await persistence.GetByIdAsync(id, cancellationToken);
        // A case in another nation is reported exactly like one that does
        // not exist, so its existence is not disclosed.
        if (caseRecord is null || !CaseNationScope.Includes(httpContext.User, caseRecord))
        {
            return TypedResults.NotFound();
        }
        return TypedResults.Ok(ToResponse(engine.Project(caseRecord)));
    }

    internal static async Task<Results<Ok<CaseListResponse>, ProblemHttpResult>> GetAll(
        HttpContext httpContext,
        [FromServices] ICasePersistence persistence,
        [FromServices] ICaseService engine,
        CancellationToken cancellationToken
    )
    {
        if (CaseNationScope.CallerNation(httpContext.User) is not { } nation)
        {
            return NationRequired();
        }

        var query = CaseQueryBinding.FromQueryString(httpContext.Request.Query) with
        {
            Nation = nation,
        };

        if (query.ExceedsPageCap)
        {
            return TypedResults.Problem(
                title: "Page out of range",
                detail: $"'page' must be <= {CaseQuery.MaxPage}.",
                statusCode: StatusCodes.Status400BadRequest
            );
        }

        var page = await persistence.QueryAsync(query, cancellationToken);

        var items = page
            .Items.Select(w => ToListItemResponse(engine.Project(w)))
            .ToList();

        return TypedResults.Ok(
            new CaseListResponse(items, page.TotalCount, page.Page, page.PageSize)
        );
    }

    /// <summary>
    /// Header name set on a response whose operation was a no-op because the
    /// case was already in the requested condition. Lets clients
    /// distinguish "first hit" from "replay" without needing to introspect the
    /// timeline.
    /// </summary>
    public const string IdempotentReplayHeader = "X-Idempotent-Replay";

    internal static async Task<
        Results<Ok<CaseResponse>, NotFound, ProblemHttpResult>
    > ApplyAction(
        [FromRoute] string id,
        [FromRoute] string actionId,
        HttpContext httpContext,
        [FromServices] ICaseService engine,
        CancellationToken cancellationToken
    )
    {
        var result = await engine.ApplyActionAsync(
            id,
            actionId,
            httpContext.User,
            cancellationToken
        );
        return ToHttpResult(result, engine);
    }

    internal static async Task<Results<Ok<CaseResponse>, NotFound, ProblemHttpResult>> Assign(
        [FromRoute] string id,
        JsonElement body,
        HttpContext httpContext,
        [FromServices] ICaseService engine,
        CancellationToken cancellationToken
    )
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return BadRequest(
                InvalidRequestTitle,
                "Request body must be a JSON object containing 'assigneeId'."
            );
        }

        if (
            !body.TryGetProperty("assigneeId", out var assigneeIdElement)
            || assigneeIdElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(assigneeIdElement.GetString())
        )
        {
            return BadRequest(
                InvalidRequestTitle,
                "'assigneeId' is required and must be a non-empty string."
            );
        }

        string? assigneeName = null;
        if (
            body.TryGetProperty("assigneeName", out var assigneeNameElement)
            && assigneeNameElement.ValueKind == JsonValueKind.String
        )
        {
            assigneeName = assigneeNameElement.GetString();
        }

        var result = await engine.AssignAsync(
            id,
            assigneeIdElement.GetString()!,
            assigneeName,
            httpContext.User,
            cancellationToken
        );
        if (result.IsIdempotentReplay)
        {
            httpContext.Response.Headers[IdempotentReplayHeader] = "true";
        }
        return ToHttpResult(result, engine);
    }

    internal static async Task<Results<Ok<CaseResponse>, NotFound, ProblemHttpResult>> Unassign(
        [FromRoute] string id,
        HttpContext httpContext,
        [FromServices] ICaseService engine,
        CancellationToken cancellationToken
    )
    {
        var result = await engine.UnassignAsync(id, httpContext.User, cancellationToken);
        if (result.IsIdempotentReplay)
        {
            httpContext.Response.Headers[IdempotentReplayHeader] = "true";
        }
        return ToHttpResult(result, engine);
    }

    internal static async Task<Results<Ok<CaseResponse>, NotFound, ProblemHttpResult>> AddNote(
        [FromRoute] string id,
        JsonElement body,
        HttpContext httpContext,
        [FromServices] ICaseService engine,
        CancellationToken cancellationToken
    )
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return BadRequest(
                InvalidRequestTitle,
                "Request body must be a JSON object containing 'text'."
            );
        }

        if (
            !body.TryGetProperty("text", out var textElement)
            || textElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(textElement.GetString())
        )
        {
            return BadRequest(
                InvalidRequestTitle,
                "'text' is required and must be a non-empty string."
            );
        }

        var result = await engine.AddNoteAsync(
            id,
            textElement.GetString()!,
            httpContext.User,
            cancellationToken
        );
        return ToHttpResult(result, engine);
    }

    private static Results<Ok<CaseResponse>, NotFound, ProblemHttpResult> ToHttpResult(
        CaseActionResult result,
        ICaseService engine
    )
    {
        if (result.IsSuccess)
        {
            return TypedResults.Ok(ToResponse(engine.Project(result.Case!)));
        }

        return result.FailureCode switch
        {
            CaseActionFailureCode.CaseNotFound => TypedResults.NotFound(),
            CaseActionFailureCode.UnknownAction
            or CaseActionFailureCode.InvalidTransition
            or CaseActionFailureCode.InvalidAssignment
            or CaseActionFailureCode.InvalidNote => TypedResults.Problem(
                title: "Invalid action",
                detail: result.Message,
                statusCode: StatusCodes.Status400BadRequest
            ),
            CaseActionFailureCode.NotAuthorized => TypedResults.Problem(
                title: "Not authorised",
                detail: result.Message,
                statusCode: StatusCodes.Status403Forbidden
            ),
            CaseActionFailureCode.MissingActorIdentity => TypedResults.Problem(
                title: "Authentication required",
                detail: result.Message,
                statusCode: StatusCodes.Status401Unauthorized
            ),
            CaseActionFailureCode.TerminalState
            or CaseActionFailureCode.ConcurrencyConflict => TypedResults.Problem(
                title: "Action not allowed",
                detail: result.Message,
                statusCode: StatusCodes.Status409Conflict
            ),
            _ => TypedResults.Problem(
                detail: result.Message,
                statusCode: StatusCodes.Status400BadRequest
            ),
        };
    }

    internal static CaseResponse ToResponse(CaseEngineProjection projection)
    {
        var w = projection.Case;
        return new CaseResponse(
            w.Id,
            w.CaseType,
            w.StateId,
            w.IsTerminal,
            w.SubmittedAt,
            w.LastModifiedAt,
            w.SubmittedBy,
            projection.TemplateVersion,
            w.SourceSystemId,
            w.Nation,
            w.OrgId,
            w.ProducerType,
            w.DisplayData,
            projection.AvailableActions,
            w.AssignedToId,
            w.AssignedToName,
            w.AssignedAt,
            w.AssignedBy,
            // Timeline (RA-97) is projected in reverse-chronological
            // (newest-first) order so a UI renders the most recent activity
            // at the top of the timeline (RA-568). Insertion index is the
            // secondary key so entries written within the same tick (common
            // under FakeTimeProvider, and possible in production when a
            // single engine call appends two entries back-to-back) keep
            // their append order reversed on the wire instead of relying on
            // undefined behaviour from a tied OrderBy (epr-s4y).
            w.Timeline.Select((e, i) => (Entry: e, Index: i))
                .OrderByDescending(x => x.Entry.PerformedAt)
                .ThenByDescending(x => x.Index)
                .Select(x => new CaseTimelineEntryResponse(
                    x.Entry.Id,
                    x.Entry.Action,
                    x.Entry.ActionDisplayName,
                    x.Entry.Details,
                    x.Entry.PerformedAt,
                    x.Entry.PerformedById,
                    x.Entry.PerformedByName,
                    // epr-rr9s: per-event state snapshot (null for pre-change entries).
                    x.Entry.StateId
                ))
                .ToList()
        );
    }

    /// <summary>
    /// Slim per-item projection used by the list endpoint (epr-4pf).
    /// Identical to <see cref="ToResponse"/>
    /// except the per-item <c>Timeline</c> is omitted entirely from the wire
    /// shape — it would otherwise dominate the payload of a 100-row page even
    /// though no list view renders it.
    /// </summary>
    internal static CaseListItemResponse ToListItemResponse(CaseEngineProjection projection)
    {
        var w = projection.Case;
        return new CaseListItemResponse(
            w.Id,
            w.CaseType,
            w.StateId,
            w.IsTerminal,
            w.SubmittedAt,
            w.LastModifiedAt,
            w.SubmittedBy,
            projection.TemplateVersion,
            w.SourceSystemId,
            w.Nation,
            w.OrgId,
            w.ProducerType,
            w.DisplayData,
            projection.AvailableActions,
            w.AssignedToId,
            w.AssignedToName,
            w.AssignedAt,
            w.AssignedBy
        );
    }
}
