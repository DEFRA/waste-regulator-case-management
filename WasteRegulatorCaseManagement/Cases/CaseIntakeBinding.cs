using System.Text.Json;

namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// Reads and validates the source system fields of a <c>POST /cases</c>
/// body into a <see cref="CaseIntake"/>.
/// </summary>
internal static class CaseIntakeBinding
{
    /// <summary>
    /// Returns <c>null</c> and sets <paramref name="intake"/> when the body is
    /// valid; otherwise returns a message describing the first problem.
    /// </summary>
    public static string? TryRead(JsonElement body, out CaseIntake? intake)
    {
        intake = null;

        if (ReadRequiredString(body, "sourceSystemId", out var sourceSystemId) is { } error)
        {
            return error;
        }
        if (ReadRequiredString(body, "callbackUrl", out var callbackUrl) is { } callbackError)
        {
            return callbackError;
        }
        if (
            !Uri.TryCreate(callbackUrl, UriKind.Absolute, out var callbackUri)
            || (callbackUri.Scheme != Uri.UriSchemeHttps && callbackUri.Scheme != Uri.UriSchemeHttp)
        )
        {
            return "'callbackUrl' must be an absolute http or https URL.";
        }
        if (ReadRequiredString(body, "nation", out var nation) is { } nationError)
        {
            return nationError;
        }
        if (!CaseNations.All.Contains(nation!))
        {
            return $"'nation' must be one of {string.Join(", ", CaseNations.All)}.";
        }
        if (ReadRequiredString(body, "orgId", out var orgId) is { } orgIdError)
        {
            return orgIdError;
        }

        string? producerType = null;
        if (body.TryGetProperty("producerType", out var producerTypeElement))
        {
            if (producerTypeElement.ValueKind != JsonValueKind.String)
            {
                return "'producerType' must be a string.";
            }
            producerType = producerTypeElement.GetString();
        }

        var displayData = new Dictionary<string, CaseDisplayField>(StringComparer.Ordinal);
        if (body.TryGetProperty("displayData", out var displayDataElement))
        {
            if (displayDataElement.ValueKind != JsonValueKind.Object)
            {
                return "'displayData' must be an object.";
            }
            foreach (var field in displayDataElement.EnumerateObject())
            {
                if (
                    field.Value.ValueKind != JsonValueKind.Object
                    || !field.Value.TryGetProperty("value", out var value)
                    || value.ValueKind != JsonValueKind.String
                    || !field.Value.TryGetProperty("type", out var type)
                    || type.ValueKind != JsonValueKind.String
                    || !CaseDisplayField.Types.Contains(type.GetString()!)
                )
                {
                    return $"'displayData.{field.Name}' must be {{ \"value\": string, \"type\": "
                        + $"one of {string.Join(", ", CaseDisplayField.Types)} }}.";
                }
                displayData[field.Name] = new CaseDisplayField(value.GetString()!, type.GetString()!);
            }
        }

        intake = new CaseIntake(sourceSystemId!, callbackUrl!, nation!, orgId!, producerType, displayData);
        return null;
    }

    private static string? ReadRequiredString(JsonElement body, string name, out string? value)
    {
        value = null;
        if (
            !body.TryGetProperty(name, out var element)
            || element.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(element.GetString())
        )
        {
            return $"'{name}' is required and must be a non-empty string.";
        }
        value = element.GetString()!.Trim();
        return null;
    }
}
