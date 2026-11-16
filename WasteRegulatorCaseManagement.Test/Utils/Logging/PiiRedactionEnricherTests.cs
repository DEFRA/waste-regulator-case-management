using AwesomeAssertions.Execution;
using Elastic.CommonSchema;
using Elastic.CommonSchema.Serilog;
using WasteRegulatorCaseManagement.Utils.Logging;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace WasteRegulatorCaseManagement.Test.Utils.Logging;

public class PiiRedactionEnricherTests
{
	private static readonly MessageTemplateParser TemplateParser = new();

	[Fact]
	public void Enrich_RedactsHttpContextClientIpAndAddress_WhenPresent()
	{
		var enrichments = new SpecialProperties.HttpContextEnrichments
		{
			Client = new Client { Ip = "203.0.113.5", Address = "203.0.113.5" },
		};
		var logEvent = CreateLogEvent(
			new LogEventProperty(
				SpecialProperties.SpecialKeys.HttpContext,
				new ScalarValue(enrichments)
			)
		);

		new PiiRedactionEnricher().Enrich(logEvent, new TestPropertyFactory());

		enrichments.Client.Should().BeEquivalentTo(new
		{
			Ip = PiiRedactionEnricher.RedactedValue,
			Address = PiiRedactionEnricher.RedactedValue,
		});
	}

	[Fact]
	public void Enrich_RedactsNameAndEmail_OnClientUserAndTopLevelUser()
	{
		var clientUser = new User
		{
			Id = "user-1",
			Name = "jdoe",
			FullName = "Jane Doe",
			Email = "jane.doe@example.com",
			Hash = "abc123",
		};
		var topLevelUser = new User
		{
			Id = "user-2",
			Name = "jsmith",
			Email = "john.smith@example.com",
		};
		var enrichments = new SpecialProperties.HttpContextEnrichments
		{
			Client = new Client { Ip = "203.0.113.5", User = clientUser },
			User = topLevelUser,
		};
		var logEvent = CreateLogEvent(
			new LogEventProperty(
				SpecialProperties.SpecialKeys.HttpContext,
				new ScalarValue(enrichments)
			)
		);

		new PiiRedactionEnricher().Enrich(logEvent, new TestPropertyFactory());

		using var scope = new AssertionScope();
		clientUser.Should().BeEquivalentTo(new
		{
			Id = "user-1",
			Name = (string?)null,
			FullName = (string?)null,
			Email = (string?)null,
			Hash = (string?)null,
		});
		topLevelUser.Should().BeEquivalentTo(new
		{
			Id = "user-2",
			Name = (string?)null,
			Email = (string?)null,
		});
	}

	[Fact]
	public void Enrich_DoesNotThrow_WhenHttpContextPropertyIsAbsent()
	{
		var logEvent = CreateLogEvent();

		var exception = Record.Exception(() =>
			new PiiRedactionEnricher().Enrich(logEvent, new TestPropertyFactory())
		);

		exception.Should().BeNull();
	}

	[Fact]
	public void Enrich_DoesNotThrow_WhenHttpContextHasNoClientOrUser()
	{
		var enrichments = new SpecialProperties.HttpContextEnrichments { Client = null };
		var logEvent = CreateLogEvent(
			new LogEventProperty(
				SpecialProperties.SpecialKeys.HttpContext,
				new ScalarValue(enrichments)
			)
		);

		var exception = Record.Exception(() =>
			new PiiRedactionEnricher().Enrich(logEvent, new TestPropertyFactory())
		);

		exception.Should().BeNull();
	}

	[Fact]
	public void Enrich_RedactsTopLevelEmailProperty_WhenPresent()
	{
		var logEvent = CreateLogEvent(
			new LogEventProperty("Email", new ScalarValue("person@example.com"))
		);

		new PiiRedactionEnricher().Enrich(logEvent, new TestPropertyFactory());

		logEvent.Properties["Email"].Should().Be(
			new ScalarValue(PiiRedactionEnricher.RedactedValue)
		);
	}

	[Fact]
	public void Enrich_RedactsEmailEmbeddedInAnyStringProperty_RegardlessOfPropertyName()
	{
		// An HTTP adapter logging an upstream response `{Body}` can echo a
		// submitted email back inside the upstream's raw error text.
		var logEvent = CreateLogEvent(
			new LogEventProperty(
				"Body",
				new ScalarValue("{\"error\":\"Invalid contact email: jane.doe@example.com\"}")
			)
		);

		new PiiRedactionEnricher().Enrich(logEvent, new TestPropertyFactory());

		var redacted = (ScalarValue)logEvent.Properties["Body"];
		redacted.Value.Should().Be("{\"error\":\"Invalid contact email: [REDACTED]\"}");
	}

	[Fact]
	public void Enrich_LeavesNonPiiPropertiesUntouched()
	{
		var logEvent = CreateLogEvent(
			new LogEventProperty("CorrelationId", new ScalarValue("abc-123")),
			new LogEventProperty("CaseId", new ScalarValue("wi-456")),
			new LogEventProperty("event.category", new ScalarValue("notify"))
		);

		new PiiRedactionEnricher().Enrich(logEvent, new TestPropertyFactory());

		using var scope = new AssertionScope();
		logEvent.Properties["CorrelationId"].Should().Be(new ScalarValue("abc-123"));
		logEvent.Properties["CaseId"].Should().Be(new ScalarValue("wi-456"));
		logEvent.Properties["event.category"].Should().Be(new ScalarValue("notify"));
	}

	private static LogEvent CreateLogEvent(params LogEventProperty[] properties) =>
		new(
			DateTimeOffset.UtcNow,
			LogEventLevel.Information,
			null,
			TemplateParser.Parse("Test message"),
			properties
		);

	private sealed class TestPropertyFactory : ILogEventPropertyFactory
	{
		public LogEventProperty CreateProperty(
			string name,
			object? value,
			bool destructureObjects = false
		) => new(name, new ScalarValue(value));
	}
}
