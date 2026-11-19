using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using WasteRegulatorCaseManagement.Utils.Http;

namespace WasteRegulatorCaseManagement.Utils.Logging;

/// <summary>
/// Logs unhandled exceptions as a single structured Serilog event so the
/// ECS formatter emits one JSON line with <c>error.message</c>,
/// <c>error.type</c>, and <c>error.stack_trace</c> fields — observable as
/// one entry in CDP OpenSearch. Returning <c>true</c> prevents
/// <see cref="ExceptionHandlerMiddleware"/> from logging a second,
/// non-structured entry that would split the stack trace across lines.
/// A <see cref="BadRequestException"/> is the caller's mistake, not a
/// fault, so it is answered with a 400 and logged as a warning.
/// </summary>
public sealed class ExceptionLoggingHandler(
	ILogger<ExceptionLoggingHandler> logger,
	IProblemDetailsService problemDetailsService) : IExceptionHandler
{
	public async ValueTask<bool> TryHandleAsync(
		HttpContext httpContext,
		Exception exception,
		CancellationToken cancellationToken)
	{
		if (exception is BadRequestException badRequestException)
		{
			logger.LogWarning(
				"Request rejected: {ErrorTitle}: {ErrorMessage}",
				badRequestException.Title,
				badRequestException.Message
			);
			httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
			return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
			{
				HttpContext = httpContext,
				ProblemDetails = new ProblemDetails
				{
					Status = StatusCodes.Status400BadRequest,
					Title = badRequestException.Title,
					Detail = badRequestException.Message,
				},
			});
		}

		logger.LogError(exception, "Unhandled exception");

		// Outside Production a request the framework cannot bind, such as a
		// malformed JSON body, throws instead of returning 400; it keeps its
		// status here so every environment answers it the same way.
		httpContext.Response.StatusCode = exception is BadHttpRequestException badRequest
			? badRequest.StatusCode
			: StatusCodes.Status500InternalServerError;

		return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
		{
			HttpContext = httpContext,
			Exception = exception,
		});
	}
}
