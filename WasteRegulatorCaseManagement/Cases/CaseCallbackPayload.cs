using System.Text.RegularExpressions;
using MongoDB.Bson;

namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// Renders a callback mapping into the payload sent to the source system,
/// replacing each <c>{{actor.field}}</c> or <c>{{decision.field}}</c>
/// placeholder in a string value with the matching value.
/// </summary>
public static partial class CaseCallbackPayload
{
	public static BsonDocument Render(BsonDocument mapping, IReadOnlyDictionary<string, string?> values) =>
		(BsonDocument)RenderValue(mapping, values);

	private static BsonValue RenderValue(BsonValue value, IReadOnlyDictionary<string, string?> values) =>
		value switch
		{
			BsonDocument document => new BsonDocument(
				document.Select(element => new BsonElement(element.Name, RenderValue(element.Value, values)))
			),
			BsonArray array => new BsonArray(array.Select(item => RenderValue(item, values))),
			BsonString text => new BsonString(
				Placeholder().Replace(text.Value, match => Resolve(match.Groups[1].Value, values))
			),
			_ => value,
		};

	private static string Resolve(string name, IReadOnlyDictionary<string, string?> values) =>
		values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
			? value
			: throw new CaseCallbackPayloadException($"Callback field '{name}' has no value.");

	[GeneratedRegex(@"\{\{\s*([A-Za-z]+\.[A-Za-z0-9_]+)\s*\}\}")]
	private static partial Regex Placeholder();
}
