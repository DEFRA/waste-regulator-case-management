using System.Text.RegularExpressions;
using MongoDB.Bson;

namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// Renders a completion event payload template, replacing
/// <c>{{actor.field}}</c>, <c>{{decision.field}}</c> and
/// <c>{{state.field}}</c> placeholders in string values. Missing
/// <c>decision.*</c> values become JSON null; missing <c>actor.*</c> and
/// <c>state.*</c> values throw.
/// </summary>
public static partial class CaseEventPayload
{
	public static BsonDocument Render(BsonDocument template, IReadOnlyDictionary<string, string?> values) =>
		(BsonDocument)RenderValue(template, values);

	private static BsonValue RenderValue(BsonValue value, IReadOnlyDictionary<string, string?> values) =>
		value switch
		{
			BsonDocument document => new BsonDocument(
				document.Select(element => new BsonElement(element.Name, RenderValue(element.Value, values)))
			),
			BsonArray array => new BsonArray(array.Select(item => RenderValue(item, values))),
			BsonString text => RenderString(text.Value, values),
			_ => value,
		};

	private static BsonValue RenderString(string text, IReadOnlyDictionary<string, string?> values)
	{
		var match = EntirePlaceholder().Match(text);
		if (match.Success)
		{
			var resolved = Resolve(match.Groups[1].Value.Trim(), values);
			if (resolved is null)
			{
				return BsonNull.Value;
			}

			return new BsonString(resolved);
		}

		return new BsonString(
			Placeholder()
				.Replace(
					text,
					placeholderMatch =>
					{
						var name = placeholderMatch.Groups[1].Value.Trim();
						return Resolve(name, values)
							?? throw new CaseEventPayloadException($"Event field '{name}' has no value.");
					}
				)
		);
	}

	private static string? Resolve(string name, IReadOnlyDictionary<string, string?> values)
	{
		if (values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
		{
			return value;
		}

		return name.StartsWith("decision.", StringComparison.Ordinal)
			? null
			: throw new CaseEventPayloadException($"Event field '{name}' has no value.");
	}

	[GeneratedRegex(@"^\{\{\s*([A-Za-z]+\.[A-Za-z0-9_]+)\s*\}\}$")]
	private static partial Regex EntirePlaceholder();

	[GeneratedRegex(@"\{\{\s*([A-Za-z]+\.[A-Za-z0-9_]+)\s*\}\}")]
	private static partial Regex Placeholder();
}