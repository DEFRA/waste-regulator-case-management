using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MongoDB.Bson;

namespace WasteRegulatorCaseManagement.AuditEvents.Analytics;

/// <summary>
/// Writes a stored document as plain JSON, with dates as UTC ISO 8601 strings
/// to the millisecond.
/// </summary>
public sealed class BsonDocumentJsonConverter : JsonConverter<BsonDocument>
{
	private const string DateFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

	public override BsonDocument Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
		throw new NotSupportedException($"{nameof(BsonDocumentJsonConverter)} only supports writing.");

	public override void Write(Utf8JsonWriter writer, BsonDocument value, JsonSerializerOptions options) =>
		WriteValue(writer, value);

	private static void WriteValue(Utf8JsonWriter writer, BsonValue value)
	{
		switch (value.BsonType)
		{
			case BsonType.Document:
				writer.WriteStartObject();
				foreach (var element in value.AsBsonDocument)
				{
					writer.WritePropertyName(element.Name);
					WriteValue(writer, element.Value);
				}
				writer.WriteEndObject();
				break;
			case BsonType.Array:
				writer.WriteStartArray();
				foreach (var item in value.AsBsonArray)
				{
					WriteValue(writer, item);
				}
				writer.WriteEndArray();
				break;
			case BsonType.DateTime:
				writer.WriteStringValue(
					value.ToUniversalTime().ToString(DateFormat, CultureInfo.InvariantCulture)
				);
				break;
			case BsonType.String:
				writer.WriteStringValue(value.AsString);
				break;
			case BsonType.Boolean:
				writer.WriteBooleanValue(value.AsBoolean);
				break;
			case BsonType.Int32:
				writer.WriteNumberValue(value.AsInt32);
				break;
			case BsonType.Int64:
				writer.WriteNumberValue(value.AsInt64);
				break;
			case BsonType.Double:
				writer.WriteNumberValue(value.AsDouble);
				break;
			case BsonType.Decimal128:
				writer.WriteNumberValue(value.AsDecimal);
				break;
			case BsonType.ObjectId:
				writer.WriteStringValue(value.AsObjectId.ToString());
				break;
			case BsonType.Null:
				writer.WriteNullValue();
				break;
			default:
				throw new NotSupportedException($"BSON type {value.BsonType} has no analytics JSON form.");
		}
	}
}
