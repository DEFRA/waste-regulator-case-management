using System.Text.Json;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.IO;

namespace WasteRegulatorCaseManagement.AuditEvents.Analytics;

/// <summary>Writes a stored document as relaxed extended JSON.</summary>
public sealed class BsonDocumentJsonConverter : JsonConverter<BsonDocument>
{
	private static readonly JsonWriterSettings s_settings = new() { OutputMode = JsonOutputMode.RelaxedExtendedJson };

	public override BsonDocument Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
		throw new NotSupportedException($"{nameof(BsonDocumentJsonConverter)} only supports writing.");

	public override void Write(Utf8JsonWriter writer, BsonDocument value, JsonSerializerOptions options) =>
		writer.WriteRawValue(value.ToJson(s_settings));
}
