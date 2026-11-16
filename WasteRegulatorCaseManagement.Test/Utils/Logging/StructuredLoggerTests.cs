using AwesomeAssertions.Execution;
using WasteRegulatorCaseManagement.Test.TestSupport;
using WasteRegulatorCaseManagement.Utils.Logging;
using Microsoft.Extensions.Logging;

namespace WasteRegulatorCaseManagement.Test.Utils.Logging;

public class StructuredLoggerTests
{
	private sealed class Anchor { }

	[Fact]
	public void Log_emits_record_with_caller_supplied_properties()
	{
		var inner = new ListLogger<Anchor>();
		var sut = new StructuredLogger<Anchor>(inner);

		sut.Log(
			LogLevel.Information,
			"hello {who}",
			new Dictionary<string, object?>
			{
				["event.category"] = "notify",
				["event.action"] = "send_email",
				["who"] = "world"
			});

		var record = inner.Records.Should().ContainSingle().Subject;
		using var scope = new AssertionScope();
		record.Level.Should().Be(LogLevel.Information);
		record.Exception.Should().BeNull();
		// Properties land via BeginScope-style capture and are visible
		// to consumers of ILogger (Serilog flattens scope into
		// LogEvent.Properties).
		record.Properties.Should().Contain("event.category", "notify");
		record.Properties.Should().Contain("event.action", "send_email");
		record.Properties.Should().Contain("who", "world");
	}

	[Fact]
	public void Log_forwards_exception_so_ecs_error_fields_are_populated()
	{
		var inner = new ListLogger<Anchor>();
		var sut = new StructuredLogger<Anchor>(inner);
		var boom = new InvalidOperationException("boom");

		sut.Log(
			LogLevel.Error,
			"failure",
			new Dictionary<string, object?> { ["event.outcome"] = "failure" },
			exception: boom);

		var record = inner.Records.Should().ContainSingle().Subject;
		using var scope = new AssertionScope();
		record.Level.Should().Be(LogLevel.Error);
		record.Exception.Should().BeSameAs(boom);
		record.Properties.Should().Contain("event.outcome", "failure");
	}

	[Fact]
	public void Log_skips_when_underlying_logger_disables_the_level()
	{
		var inner = new DisabledLogger<Anchor>();
		var sut = new StructuredLogger<Anchor>(inner);

		sut.Log(
			LogLevel.Information,
			"hello",
			new Dictionary<string, object?> { ["k"] = "v" });

		inner.Should().BeEquivalentTo(new { LogCalls = 0, ScopesOpened = 0 });
	}

	[Fact]
	public void Log_throws_for_null_message_or_properties()
	{
		var sut = new StructuredLogger<Anchor>(new ListLogger<Anchor>());

		var nullMessage = () =>
			sut.Log(LogLevel.Information, null!, new Dictionary<string, object?>());
		var nullProperties = () =>
			sut.Log(LogLevel.Information, "hi", null!);
		using var scope = new AssertionScope();
		nullMessage.Should().ThrowExactly<ArgumentNullException>();
		nullProperties.Should().ThrowExactly<ArgumentNullException>();
	}

	private sealed class DisabledLogger<T> : ILogger<T>
	{
		public int LogCalls { get; private set; }
		public int ScopesOpened { get; private set; }
		public IDisposable BeginScope<TState>(TState state) where TState : notnull
		{
			ScopesOpened++;
			return NoopScope.Instance;
		}
		public bool IsEnabled(LogLevel logLevel) => false;
		public void Log<TState>(
			LogLevel logLevel,
			EventId eventId,
			TState state,
			Exception? exception,
			Func<TState, Exception?, string> formatter) => LogCalls++;

		private sealed class NoopScope : IDisposable
		{
			public static readonly NoopScope Instance = new();
			public void Dispose() { }
		}
	}
}

