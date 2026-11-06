using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
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
/// HTTP endpoints for ingesting, listing and progressing cases of every
/// case type.
/// </summary>
public static class CaseEndpoints
{
	private const string AbsentValue = "(absent)";
	private const string CallerClientIdField = "caller.client_id";
	private const string ErrorMessageField = "error.message";
	private const string InvalidRequestTitle = "Invalid request";
	private const string CaseTypeField = "case.case_type";

	// Request body size caps. The case endpoints all parse
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
			.WithSummary("Create a case for a submission")
			.WithDescription(
				"Called by the Case Intake Processor only; any other caller gets 403. Creates a case of `caseType` "
					+ "in the case type's initial state and returns it with 201. If a case already exists for "
					+ "`sourceSystemId`, returns that case with 200 and `X-Idempotent-Replay: true`, so a resent "
					+ "submission can be discarded. `initialState` and `createdAt` are for the bulk load of "
					+ "existing submissions."
			)
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.DisableValidation()
			.WithMetadata(new RequestSizeLimitAttribute(MaxSubmitBodyBytes))
			.RequireAuthorization(CaseIntakePolicy.Name);

		group
			.MapGet("/{id}", GetById)
			.WithName("GetCaseById")
			.WithSummary("Get a case")
			.WithDescription(
				"Returns the case with its timeline, newest entry first, and the actions its current state "
					+ "allows. A case in another nation returns 404, as if it did not exist."
			)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.RequireAuthorization();

		group
			.MapGet(string.Empty, GetAll)
			.WithName("ListCases")
			.WithSummary("List the cases in the caller's nation")
			.WithDescription(
				"Returns a page of the cases in the caller's nation, without their timelines, newest "
					+ "submission first unless `sort` is given. Filters combine: a case is listed only if it "
					+ "matches every filter supplied."
			)
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.RequireAuthorization();

		group
			.MapPost("/{id}/actions/{actionId}", ApplyAction)
			.WithName("ApplyCaseAction")
			.WithSummary("Apply an action to a case")
			.WithDescription(
				"Moves the case along the transition named `actionId`, such as `accept` or `cancel`, when its "
					+ "current state allows it. A transition that requires decision fields, such as CSoC "
					+ "`cancel` and its `reason`, needs them in `decision`. When the new state concludes the case, "
					+ "the decision is sent to the case's `callbackUrl` first: if the source system refuses it, "
					+ "the case keeps its state, a `callback-failed` timeline entry records why, and the response "
					+ "is 502. A concluded case returns 409."
			)
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status502BadGateway)
			.RequireAuthorization();

		group
			.MapPost("/{id}/assign", Assign)
			.WithName("AssignCase")
			.WithSummary("Assign a case to a caseworker")
			.WithDescription(
				"Assigns or reassigns the case. Assigning it to its current assignee changes nothing and "
					+ "returns `X-Idempotent-Replay: true`. A concluded case cannot be assigned and returns 409."
			)
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.DisableValidation()
			.WithMetadata(new RequestSizeLimitAttribute(MaxAssignBodyBytes))
			.RequireAuthorization();

		group
			.MapPost("/{id}/unassign", Unassign)
			.WithName("UnassignCase")
			.WithSummary("Unassign a case")
			.WithDescription(
				"Removes the case's assignee. Unassigning a case with no assignee changes nothing and returns "
					+ "`X-Idempotent-Replay: true`. A concluded case cannot be unassigned and returns 409."
			)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.RequireAuthorization();

		group
			.MapPost("/{id}/notes", AddNote)
			.WithName("AddCaseNote")
			.WithSummary("Add a note to a case")
			.WithDescription(
				"Adds the note to the case's timeline as a `note-added` entry. Notes cannot be edited or "
					+ "deleted."
			)
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.DisableValidation()
			.WithMetadata(new RequestSizeLimitAttribute(MaxNoteBodyBytes))
			.RequireAuthorization();

		return app;
	}

	internal static async Task<Results<CreatedAtRoute<CaseResponse>, Ok<CaseResponse>, ProblemHttpResult>> Submit(
		CaseIntakeRequest body,
		HttpContext httpContext,
		[FromServices] CaseRegistry registry,
		[FromServices] ICaseService engine,
		[FromServices] IStructuredLogger<CaseEndpointsLogger> log,
		CancellationToken cancellationToken
	)
	{
		var req = httpContext.Request;
		log.Log(
			LogLevel.Information,
			"Case submission received",
			new Dictionary<string, object?>
			{
				["http.request.method"] = req.Method,
				["url.path"] = req.Path.Value,
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

		if (string.IsNullOrWhiteSpace(body.CaseType))
		{
			log.Log(
				LogLevel.Warning,
				"Case submission rejected: missing or empty caseType",
				new Dictionary<string, object?>()
			);
			return BadRequest(
				InvalidRequestTitle,
				"'caseType' is required and must be a non-empty string."
			);
		}

		var caseType = body.CaseType;
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

		// RA-126: optional caller-supplied audit context.
		var submissionMetadata = body.Source is null
			? null
			: new Dictionary<string, string?>(StringComparer.Ordinal) { ["source"] = body.Source };

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
		[FromRoute, Description("Case id.")] string id,
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
		[FromQuery(Name = "caseType"), Description("Only cases of this case type. Repeat to match any of several.")]
		string[]? caseType,
		[FromQuery(Name = "stateId"), Description("Only cases in this state. Repeat to match any of several.")]
		string[]? stateId,
		[FromQuery(Name = "search"), Description("Only cases whose id starts with this text, ignoring case.")]
		string? search,
		[FromQuery(Name = "assigneeId"), Description("Only cases assigned to this user id. With `unassigned=true`, cases assigned to this user id or to no one.")]
		string? assigneeId,
		[FromQuery(Name = "unassigned"), Description("`true` for only the cases with no assignee.")]
		bool? unassigned,
		[FromQuery(Name = "orgId"), Description("Only cases of this organisation.")]
		string? orgId,
		[FromQuery(Name = "isTerminal"), Description("`true` for only concluded cases, `false` for only open ones.")]
		bool? isTerminal,
		[FromQuery(Name = "sort"), Description("`status` orders the cases by their case type's states, in the order the case type declares them.")]
		string? sort,
		[FromQuery(Name = "dir"), Description("`asc` or `desc`, the direction of `sort`.")]
		string? dir,
		[FromQuery(Name = "page"), Description("Page number, from 1 to 1000. Defaults to 1.")]
		int? page,
		[FromQuery(Name = "pageSize"), Description("Cases per page, up to 100. Defaults to 20.")]
		int? pageSize,
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

		var request = new CaseListRequest(
			caseType,
			stateId,
			search,
			assigneeId,
			unassigned,
			orgId,
			isTerminal,
			sort,
			dir,
			page,
			pageSize
		);
		var query = CaseQueryBinding.FromRequest(request) with
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

		var casePage = await persistence.QueryAsync(query, cancellationToken);

		var items = casePage
			.Items.Select(w => ToListItemResponse(engine.Project(w)))
			.ToList();

		return TypedResults.Ok(
			new CaseListResponse(items, casePage.TotalCount, casePage.Page, casePage.PageSize)
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
		[FromRoute, Description("Case id.")] string id,
		[FromRoute, Description("The action to apply, one of the case's `availableActions`, such as `accept`.")] string actionId,
		CaseActionRequest? body,
		HttpContext httpContext,
		[FromServices] ICaseService engine,
		CancellationToken cancellationToken
	)
	{
		var decision = body?.Decision is { } fields
			? new Dictionary<string, string>(fields, StringComparer.Ordinal)
			: null;

		var result = await engine.ApplyActionAsync(
			id,
			actionId,
			decision,
			httpContext.User,
			cancellationToken
		);
		return ToHttpResult(result, engine);
	}

	internal static async Task<Results<Ok<CaseResponse>, NotFound, ProblemHttpResult>> Assign(
		[FromRoute, Description("Case id.")] string id,
		AssignCaseRequest body,
		HttpContext httpContext,
		[FromServices] ICaseService engine,
		CancellationToken cancellationToken
	)
	{
		if (string.IsNullOrWhiteSpace(body.AssigneeId))
		{
			return BadRequest(
				InvalidRequestTitle,
				"'assigneeId' is required and must be a non-empty string."
			);
		}

		var result = await engine.AssignAsync(
			id,
			body.AssigneeId,
			body.AssigneeName,
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
		[FromRoute, Description("Case id.")] string id,
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
		[FromRoute, Description("Case id.")] string id,
		AddCaseNoteRequest body,
		HttpContext httpContext,
		[FromServices] ICaseService engine,
		CancellationToken cancellationToken
	)
	{
		if (string.IsNullOrWhiteSpace(body.Text))
		{
			return BadRequest(
				InvalidRequestTitle,
				"'text' is required and must be a non-empty string."
			);
		}

		var result = await engine.AddNoteAsync(
			id,
			body.Text,
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
			or CaseActionFailureCode.InvalidNote
			or CaseActionFailureCode.MissingDecisionField => TypedResults.Problem(
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
			CaseActionFailureCode.CallbackFailed => TypedResults.Problem(
				title: "Source system callback failed",
				detail: result.Message,
				statusCode: StatusCodes.Status502BadGateway
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
			w.Decision,
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
			// undefined behaviour from a tied OrderBy.
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
					// Per-event state snapshot (null for pre-change entries).
					x.Entry.StateId
				))
				.ToList()
		);
	}

	/// <summary>
	/// Slim per-item projection used by the list endpoint.
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
