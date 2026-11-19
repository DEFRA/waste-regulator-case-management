using AwesomeAssertions.Execution;
using WasteRegulatorCaseManagement.Utils.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WasteRegulatorCaseManagement.Utils.Http;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace WasteRegulatorCaseManagement.Test.Utils.Logging;

/// <summary>
/// Pins the contract that <see cref="ExceptionLoggingHandler"/> emits a
/// single structured log event at Error level (so the ECS formatter can
/// place the full exception in one log entry) and delegates the
/// ProblemDetails response to <see cref="IProblemDetailsService"/>.
/// </summary>
public class ExceptionLoggingHandlerTests
{
	[Fact]
	public async Task TryHandleAsync_logs_exception_at_error_level()
	{
		var logger = new CapturingLogger<ExceptionLoggingHandler>();
		var pds = Substitute.For<IProblemDetailsService>();
		pds.TryWriteAsync(Arg.Any<ProblemDetailsContext>()).Returns(ValueTask.FromResult(true));

		var handler = new ExceptionLoggingHandler(logger, pds);
		var exception = new InvalidOperationException("boom");

		await handler.TryHandleAsync(new DefaultHttpContext(), exception, CancellationToken.None);

		var (level, logged) = logger.Entries.Should().ContainSingle().Subject;
		using var scope = new AssertionScope();
		level.Should().Be(LogLevel.Error);
		logged.Should().BeSameAs(exception);
	}

	[Fact]
	public async Task TryHandleAsync_sets_500_status_code()
	{
		var logger = new CapturingLogger<ExceptionLoggingHandler>();
		var pds = Substitute.For<IProblemDetailsService>();
		pds.TryWriteAsync(Arg.Any<ProblemDetailsContext>()).Returns(ValueTask.FromResult(true));

		var handler = new ExceptionLoggingHandler(logger, pds);
		var context = new DefaultHttpContext();

		await handler.TryHandleAsync(context, new Exception(), CancellationToken.None);

		context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
	}

	[Fact]
	public async Task TryHandleAsync_returns_result_from_problem_details_service()
	{
		var logger = new CapturingLogger<ExceptionLoggingHandler>();
		var pds = Substitute.For<IProblemDetailsService>();
		pds.TryWriteAsync(Arg.Any<ProblemDetailsContext>()).Returns(ValueTask.FromResult(false));

		var handler = new ExceptionLoggingHandler(logger, pds);

		var result = await handler.TryHandleAsync(new DefaultHttpContext(), new Exception(), CancellationToken.None);

		result.Should().BeFalse();
	}

	[Fact]
	public async Task TryHandleAsync_answers_a_bad_request_with_a_400_problem_and_a_warning()
	{
		var logger = new CapturingLogger<ExceptionLoggingHandler>();
		var pds = Substitute.For<IProblemDetailsService>();
		ProblemDetailsContext? written = null;
		pds.TryWriteAsync(Arg.Do<ProblemDetailsContext>(c => written = c)).Returns(ValueTask.FromResult(true));

		var handler = new ExceptionLoggingHandler(logger, pds);
		var context = new DefaultHttpContext();

		await handler.TryHandleAsync(context, new TestBadRequestException(), CancellationToken.None);

		using var scope = new AssertionScope();
		context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
		logger.Entries.Should().Equal((LogLevel.Warning, (Exception?)null));
		written!.ProblemDetails.Should().BeEquivalentTo(new ProblemDetails
		{
			Status = StatusCodes.Status400BadRequest,
			Title = "Bad thing",
			Detail = "The caller sent a bad thing.",
		});
		written.Exception.Should().BeNull("a caller's mistake has no stack trace worth showing them");
	}

	private sealed class TestBadRequestException() : BadRequestException("Bad thing", "The caller sent a bad thing.");

	private sealed class CapturingLogger<T> : ILogger<T>
	{
		public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(
			LogLevel logLevel,
			EventId eventId,
			TState state,
			Exception? exception,
			Func<TState, Exception?, string> formatter) =>
			Entries.Add((logLevel, exception));
	}
}
