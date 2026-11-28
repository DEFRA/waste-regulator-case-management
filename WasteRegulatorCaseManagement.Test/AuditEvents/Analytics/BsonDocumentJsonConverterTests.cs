using System.Text.Json;
using MongoDB.Bson;
using WasteRegulatorCaseManagement.AuditEvents.Analytics;

namespace WasteRegulatorCaseManagement.Test.AuditEvents.Analytics;

public sealed class BsonDocumentJsonConverterTests
{
	private static readonly JsonSerializerOptions s_options = new(JsonSerializerDefaults.Web)
	{
		Converters = { new BsonDocumentJsonConverter() },
	};

	[Fact]
	public void Serialize_writes_less_common_bson_scalar_types()
	{
		var objectId = ObjectId.GenerateNewId();
		var document = new BsonDocument
		{
			{ "count", 42L },
			{ "amount", Decimal128.Parse("9.99") },
			{ "ratio", 1.25d },
			{ "id", objectId },
		};

		var json = JsonSerializer.Serialize(document, s_options);

		using var parsed = JsonDocument.Parse(json);
		parsed.RootElement.GetProperty("count").GetInt64().Should().Be(42L);
		parsed.RootElement.GetProperty("amount").GetDecimal().Should().Be(9.99m);
		parsed.RootElement.GetProperty("ratio").GetDouble().Should().Be(1.25d);
		parsed.RootElement.GetProperty("id").GetString().Should().Be(objectId.ToString());
	}

	[Fact]
	public void Serialize_throws_for_an_unsupported_bson_type()
	{
		var document = new BsonDocument("code", new BsonJavaScript("1 + 1"));

		var serialize = () => JsonSerializer.Serialize(document, s_options);

		serialize
			.Should()
			.Throw<NotSupportedException>()
			.WithMessage("*BSON type JavaScript has no analytics JSON form*");
	}
}