using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text.Json;
using WasteRegulatorCaseManagement.Utils.Logging;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace WasteRegulatorCaseManagement.WorkItems.Core;

// Marker type for IStructuredLogger category — WorkItemEndpoints is a
// static class and therefore cannot itself be used as a type argument.
internal sealed class WorkItemEndpointsLogger;

/// <summary>
/// Framework-level HTTP endpoints for ingesting and listing work items. The
/// envelope (id, type, state, submitted-by, payload) is owned by the
/// framework; type-specific behaviour and routes are added by modules under
/// <c>/work-items/&lt;type-id&gt;/...</c>.
/// </summary>
public static class WorkItemEndpoints
{
    private const string AbsentValue = "(absent)";
    private const string CallerClientIdField = "caller.client_id";
    private const string ErrorMessageField = "error.message";
    private const string InvalidRequestTitle = "Invalid request";
    private const string WorkItemTypeIdField = "work_item.type_id";

    // Request body size caps (epr-rvz). The work item endpoints all parse
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
    public static IEndpointRouteBuilder MapWorkItemFrameworkEndpoints(
        this IEndpointRouteBuilder app
    )
    {
        var group = app.MapGroup("/work-items").WithTags("WorkItems");

        group
            .MapPost(string.Empty, Submit)
            .WithName("SubmitWorkItem")
            .DisableValidation()
            .WithMetadata(new RequestSizeLimitAttribute(MaxSubmitBodyBytes))
            .RequireAuthorization();

        group.MapGet("/{id:guid}", GetById).WithName("GetWorkItemById").RequireAuthorization();

        group.MapGet(string.Empty, GetAll).WithName("ListWorkItems").RequireAuthorization();

        group
            .MapPost("/{id:guid}/actions/{actionId}", ApplyAction)
            .WithName("ApplyWorkItemAction")
            .RequireAuthorization();

        group
            .MapPost("/{id:guid}/assign", Assign)
            .WithName("AssignWorkItem")
            .DisableValidation()
            .WithMetadata(new RequestSizeLimitAttribute(MaxAssignBodyBytes))
            .RequireAuthorization();

        group
            .MapPost("/{id:guid}/unassign", Unassign)
            .WithName("UnassignWorkItem")
            .RequireAuthorization();

        group
            .MapPost("/{id:guid}/notes", AddNote)
            .WithName("AddWorkItemNote")
            .DisableValidation()
            .WithMetadata(new RequestSizeLimitAttribute(MaxNoteBodyBytes))
            .RequireAuthorization();

        return app;
    }

    internal static async Task<Results<CreatedAtRoute<WorkItemResponse>, ProblemHttpResult>> Submit(
        JsonElement body,
        HttpContext httpContext,
        [FromServices] IWorkItemRegistry registry,
        [FromServices] IWorkItemService engine,
        [FromServices] IStructuredLogger<WorkItemEndpointsLogger> log,
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
            "Work item submission received",
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
                "Work item submission rejected: body is not a JSON object",
                new Dictionary<string, object?>
                {
                    [ErrorMessageField] = $"ValueKind was {body.ValueKind}",
                }
            );
            return BadRequest(InvalidRequestTitle, "Request body must be a JSON object.");
        }

        if (
            !body.TryGetProperty("typeId", out var typeIdElement)
            || typeIdElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(typeIdElement.GetString())
        )
        {
            log.Log(
                LogLevel.Warning,
                "Work item submission rejected: missing or empty typeId",
                new Dictionary<string, object?> { ["http.request.body"] = loggedBody }
            );
            return BadRequest(
                InvalidRequestTitle,
                "'typeId' is required and must be a non-empty string."
            );
        }

        var typeId = typeIdElement.GetString()!;
        var type = registry.Find(typeId);
        if (type is null)
        {
            log.Log(
                LogLevel.Warning,
                "Work item submission rejected: unknown typeId",
                new Dictionary<string, object?> { [WorkItemTypeIdField] = typeId }
            );
            return BadRequest(
                "Unknown work item type",
                $"No work item type is registered with id '{typeId}'."
            );
        }

        JsonElement? payload = body.TryGetProperty("payload", out var payloadElement)
            ? payloadElement
            : null;

        MongoDB.Bson.BsonDocument payloadDocument;
        try
        {
            payloadDocument = WorkItemPayloadConverter.ToBson(payload);
        }
        catch (InvalidWorkItemPayloadException ex)
        {
            log.Log(
                LogLevel.Warning,
                "Work item submission rejected: invalid payload",
                new Dictionary<string, object?>
                {
                    [WorkItemTypeIdField] = typeId,
                    [ErrorMessageField] = ex.Message,
                },
                ex
            );
            return BadRequest("Invalid work item payload", ex.Message);
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
                    "Work item submission rejected: 'source' is not a string",
                    new Dictionary<string, object?>
                    {
                        [WorkItemTypeIdField] = typeId,
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
        // its first 'work-item-submitted' audit entry in a single
        // CreateAsync call.
        WorkItemActionResult result;
        try
        {
            result = await engine.SubmitAsync(
                type,
                payloadDocument,
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
                "Work item submission threw an unhandled exception",
                new Dictionary<string, object?>
                {
                    [WorkItemTypeIdField] = typeId,
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
                "Work item submission failed",
                new Dictionary<string, object?>
                {
                    [WorkItemTypeIdField] = typeId,
                    [CallerClientIdField] = submittedBy ?? "(unknown)",
                    ["error.code"] = result.FailureCode.ToString(),
                    [ErrorMessageField] = result.Message,
                }
            );
            return result.FailureCode switch
            {
                WorkItemActionFailureCode.MissingActorIdentity => TypedResults.Problem(
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

        var workItem = result.WorkItem!;
        log.Log(
            LogLevel.Information,
            "Work item submission succeeded",
            new Dictionary<string, object?>
            {
                ["work_item.id"] = workItem.Id.ToString(),
                [WorkItemTypeIdField] = typeId,
                [CallerClientIdField] = submittedBy ?? "(unknown)",
            }
        );
        var response = ToResponse(engine.Project(workItem));
        return TypedResults.CreatedAtRoute(response, "GetWorkItemById", new { id = workItem.Id });
    }

    private static ProblemHttpResult BadRequest(string title, string detail) =>
        TypedResults.Problem(
            title: title,
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest
        );

    internal static async Task<Results<Ok<WorkItemResponse>, NotFound>> GetById(
        [FromRoute] Guid id,
        HttpContext httpContext,
        [FromServices] IWorkItemPersistence persistence,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken
    )
    {
        var workItem = await persistence.GetByIdAsync(id, cancellationToken);
        if (workItem is null)
        {
            return TypedResults.NotFound();
        }
        return TypedResults.Ok(ToResponse(engine.Project(workItem)));
    }

    internal static async Task<Results<Ok<WorkItemListResponse>, ProblemHttpResult>> GetAll(
        HttpContext httpContext,
        [FromServices] IWorkItemPersistence persistence,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken
    )
    {
        var query = WorkItemQueryBinding.FromQueryString(httpContext.Request.Query);

        if (query.ExceedsPageCap)
        {
            return TypedResults.Problem(
                title: "Page out of range",
                detail: $"'page' must be <= {WorkItemQuery.MaxPage}.",
                statusCode: StatusCodes.Status400BadRequest
            );
        }

        var page = await persistence.QueryAsync(query, cancellationToken);

        var items = page
            .Items.Select(w => ToListItemResponse(engine.Project(w)))
            .ToList();

        return TypedResults.Ok(
            new WorkItemListResponse(items, page.TotalCount, page.Page, page.PageSize)
        );
    }

    /// <summary>
    /// Header name set on a response whose operation was a no-op because the
    /// work item was already in the requested condition. Lets clients
    /// distinguish "first hit" from "replay" without needing to introspect the
    /// audit log.
    /// </summary>
    public const string IdempotentReplayHeader = "X-Idempotent-Replay";

    internal static async Task<
        Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult>
    > ApplyAction(
        [FromRoute] Guid id,
        [FromRoute] string actionId,
        HttpContext httpContext,
        [FromServices] IWorkItemPersistence persistence,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken
    )
    {
        // Security boundary (RA-311/MBE-1 review): a transition is declared
        // CallerInvocable: false when several transitions share the same
        // FromStateId — the engine's normal "is the item in the right
        // state" guard cannot tell them apart, so a caller who could invoke
        // one directly here would pick the target state themselves instead
        // of the module's bespoke service resolving it server-side from
        // audit history. Checked against the work item's own frozen
        // TemplateSnapshot (the same source ApplyActionAsync itself
        // resolves the transition from) so this rejects using the exact
        // rules the item was submitted under.
        var workItem = await persistence.GetByIdAsync(id, cancellationToken);
        // RA-351: an action id can be declared on more than one state (e.g.
        // a self-loop available from two states). Resolve the CallerInvocable check against the transition
        // for the item's CURRENT state — matching on action id alone would
        // test the first-declared transition, which can differ in
        // CallerInvocable from the one that actually applies here.
        var actionTransitions = workItem
            ?.TemplateSnapshot?.Transitions.Where(t =>
                string.Equals(t.ActionId, actionId, StringComparison.OrdinalIgnoreCase)
            )
            .ToList();
        var transition =
            actionTransitions?.FirstOrDefault(t =>
                string.Equals(
                    t.FromStateId,
                    workItem!.StateId,
                    StringComparison.OrdinalIgnoreCase
                )
            ) ?? actionTransitions?.FirstOrDefault();
        if (transition is { CallerInvocable: false })
        {
            return ToHttpResult(
                WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.UnknownAction,
                    $"Action '{actionId}' is not declared by work item type '{workItem!.TypeId}'."),
                engine);
        }

        var result = await engine.ApplyActionAsync(
            id,
            actionId,
            httpContext.User,
            cancellationToken
        );
        return ToHttpResult(result, engine);
    }

    internal static async Task<Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult>> Assign(
        [FromRoute] Guid id,
        JsonElement body,
        HttpContext httpContext,
        [FromServices] IWorkItemService engine,
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

    internal static async Task<Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult>> Unassign(
        [FromRoute] Guid id,
        HttpContext httpContext,
        [FromServices] IWorkItemService engine,
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

    internal static async Task<Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult>> AddNote(
        [FromRoute] Guid id,
        JsonElement body,
        HttpContext httpContext,
        [FromServices] IWorkItemService engine,
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

    private static Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult> ToHttpResult(
        WorkItemActionResult result,
        IWorkItemService engine
    )
    {
        if (result.IsSuccess)
        {
            return TypedResults.Ok(ToResponse(engine.Project(result.WorkItem!)));
        }

        return result.FailureCode switch
        {
            WorkItemActionFailureCode.WorkItemNotFound => TypedResults.NotFound(),
            WorkItemActionFailureCode.UnknownAction
            or WorkItemActionFailureCode.InvalidTransition
            or WorkItemActionFailureCode.InvalidAssignment
            or WorkItemActionFailureCode.InvalidNote => TypedResults.Problem(
                title: "Invalid action",
                detail: result.Message,
                statusCode: StatusCodes.Status400BadRequest
            ),
            WorkItemActionFailureCode.NotAuthorized => TypedResults.Problem(
                title: "Not authorised",
                detail: result.Message,
                statusCode: StatusCodes.Status403Forbidden
            ),
            WorkItemActionFailureCode.MissingActorIdentity => TypedResults.Problem(
                title: "Authentication required",
                detail: result.Message,
                statusCode: StatusCodes.Status401Unauthorized
            ),
            WorkItemActionFailureCode.TerminalState
            or WorkItemActionFailureCode.ConcurrencyConflict => TypedResults.Problem(
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

    internal static WorkItemResponse ToResponse(WorkItemEngineProjection projection)
    {
        var w = projection.WorkItem;
        return new WorkItemResponse(
            w.Id,
            w.TypeId,
            w.StateId,
            w.SubmittedAt,
            w.LastModifiedAt,
            w.SubmittedBy,
            projection.TemplateVersion,
            WorkItemPayloadConverter.ToJson(w.Payload),
            projection.AvailableActions,
            w.AssignedToId,
            w.AssignedToName,
            w.AssignedAt,
            w.AssignedBy,
            // Notes are stored append-only but rendered newest-first so the
            // most relevant context is at the top of an assessor's screen.
            w.Notes.OrderByDescending(n => n.CreatedAt)
                .Select(n => new WorkItemNoteResponse(
                    n.Id,
                    n.Text,
                    n.CreatedAt,
                    n.CreatedBy,
                    n.CreatedByName
                ))
                .ToList(),
            // Audit log (RA-97) is projected in reverse-chronological
            // (newest-first) order so a UI renders the most recent activity
            // at the top of the timeline (RA-568). Insertion index is the
            // secondary key so entries written within the same tick (common
            // under FakeTimeProvider, and possible in production when a
            // single engine call appends two entries back-to-back) keep
            // their append order reversed on the wire instead of relying on
            // undefined behaviour from a tied OrderBy (epr-s4y).
            w.AuditLog.Select((e, i) => (Entry: e, Index: i))
                .OrderByDescending(x => x.Entry.CreatedAt)
                .ThenByDescending(x => x.Index)
                .Select(x => new WorkItemAuditEntryResponse(
                    x.Entry.Id,
                    x.Entry.Action,
                    x.Entry.ActionDisplayName,
                    x.Entry.Details,
                    x.Entry.CreatedAt,
                    x.Entry.CreatedBy,
                    x.Entry.CreatedByName,
                    // epr-rr9s: per-event state snapshot (null for pre-change entries).
                    x.Entry.StateId
                ))
                .ToList()
        );
    }

    /// <summary>
    /// Slim per-item projection used by the list endpoint (epr-4pf).
    /// Identical to <see cref="ToResponse"/>
    /// except the per-item <c>Notes</c> and <c>AuditLog</c> collections
    /// are omitted entirely from the wire shape — they would otherwise
    /// dominate the payload of a 100-row page even though no list view
    /// renders them.
    /// </summary>
    internal static WorkItemListItemResponse ToListItemResponse(WorkItemEngineProjection projection)
    {
        var w = projection.WorkItem;
        return new WorkItemListItemResponse(
            w.Id,
            w.TypeId,
            w.StateId,
            w.SubmittedAt,
            w.LastModifiedAt,
            w.SubmittedBy,
            projection.TemplateVersion,
            WorkItemPayloadConverter.ToJson(w.Payload),
            projection.AvailableActions,
            w.AssignedToId,
            w.AssignedToName,
            w.AssignedAt,
            w.AssignedBy
        );
    }
}
