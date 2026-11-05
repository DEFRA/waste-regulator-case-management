using System.Text.Json;
using System.Text.Json.Serialization;
using MongoDB.Bson;

namespace WasteRegulatorCaseManagement.Cases;

/// <summary>Reads a case type's callback mappings from its JSON configuration into stored documents.</summary>
public sealed class CallbackMappingsJsonConverter : JsonConverter<Dictionary<string, BsonDocument>>
{
	public override Dictionary<string, BsonDocument> Read(
		ref Utf8JsonReader reader,
		Type typeToConvert,
		JsonSerializerOptions options
	)
	{
		using var mappings = JsonDocument.ParseValue(ref reader);
		return mappings.RootElement.EnumerateObject()
			.ToDictionary(mapping => mapping.Name, mapping => BsonDocument.Parse(mapping.Value.GetRawText()));
	}

	public override void Write(
		Utf8JsonWriter writer,
		Dictionary<string, BsonDocument> value,
		JsonSerializerOptions options
	) => throw new NotSupportedException($"{nameof(CallbackMappingsJsonConverter)} only supports reading.");
}
