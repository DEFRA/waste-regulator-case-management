using AwesomeAssertions.Execution;
using WasteRegulatorCaseManagement.Utils.Mongo;

namespace WasteRegulatorCaseManagement.Test.Utils.Mongo;

/// <summary>
/// Pin the redaction contract that protects the Mongo
/// password from leaking through exception messages and any other
/// place a connection string might end up rendered.
/// </summary>
public class MongoUriRedactorTests
{
	[Theory]
	[InlineData(
		"mongodb://alice:s3cret@db.local:27017/mydb",
		"mongodb://***:***@db.local:27017/mydb")]
	[InlineData(
		"mongodb+srv://alice:s3cret@cluster.mongodb.net/mydb?retryWrites=true",
		"mongodb+srv://***:***@cluster.mongodb.net/mydb?retryWrites=true")]
	[InlineData(
		"mongodb://alice:s3cret@host1:27017,host2:27017/mydb?replicaSet=rs0",
		"mongodb://***:***@host1:27017,host2:27017/mydb?replicaSet=rs0")]
	public void Redact_replaces_credential_pair_and_preserves_uri_shape(string input, string expected)
	{
		var redacted = MongoUriRedactor.Redact(input);

		using var scope = new AssertionScope();
		redacted.Should().Be(expected);
		redacted.Should().NotContain("alice");
		redacted.Should().NotContain("s3cret");
	}

	[Theory]
	[InlineData("mongodb://db.local:27017/mydb")]
	[InlineData("mongodb+srv://cluster.mongodb.net/mydb")]
	[InlineData("")]
	public void Redact_passes_through_uris_without_user_info(string input)
	{
		MongoUriRedactor.Redact(input).Should().Be(input);
	}

	[Fact]
	public void Redact_handles_null()
	{
		MongoUriRedactor.Redact(null).Should().Be(string.Empty);
	}

	[Fact]
	public void Redact_does_not_touch_query_string_at_signs()
	{
		// '@' inside a query string must not be confused for the
		// user-info separator.
		const string input = "mongodb://db.local:27017/mydb?authMechanism=PLAIN&user=foo@bar";
		MongoUriRedactor.Redact(input).Should().Be(input);
	}
}
