namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// Validates the source system fields of a <see cref="CaseIntakeRequest"/>
/// and turns them into a <see cref="CaseIntake"/>.
/// </summary>
internal static class CaseIntakeBinding
{
	/// <summary>
	/// Returns <c>null</c> and sets <paramref name="intake"/> when the request
	/// is valid; otherwise returns a message describing the first problem.
	/// </summary>
	public static string? TryRead(CaseIntakeRequest request, out CaseIntake? intake)
	{
		intake = null;

		if (RequireValue(request.SourceSystemId, "sourceSystemId") is { } error)
		{
			return error;
		}
		if (RequireValue(request.CallbackUrl, "callbackUrl") is { } callbackError)
		{
			return callbackError;
		}
		if (
			!Uri.TryCreate(request.CallbackUrl.Trim(), UriKind.Absolute, out var callbackUri)
			|| (callbackUri.Scheme != Uri.UriSchemeHttps && callbackUri.Scheme != Uri.UriSchemeHttp)
		)
		{
			return "'callbackUrl' must be an absolute http or https URL.";
		}
		if (RequireValue(request.Nation, "nation") is { } nationError)
		{
			return nationError;
		}
		if (!CaseNations.All.Contains(request.Nation.Trim()))
		{
			return $"'nation' must be one of {string.Join(", ", CaseNations.All)}.";
		}
		if (RequireValue(request.OrgId, "orgId") is { } orgIdError)
		{
			return orgIdError;
		}

		var displayData = new Dictionary<string, CaseDisplayField>(StringComparer.Ordinal);
		foreach (var (name, field) in request.DisplayData ?? new Dictionary<string, CaseDisplayField>())
		{
			if (field?.Value is null || field.Type is null || !CaseDisplayField.Types.Contains(field.Type))
			{
				return $"'displayData.{name}' must be {{ \"value\": string, \"type\": "
					+ $"one of {string.Join(", ", CaseDisplayField.Types)} }}.";
			}
			displayData[name] = field;
		}

		if (request.InitialState is not null && RequireValue(request.InitialState, "initialState") is { } initialStateError)
		{
			return initialStateError;
		}

		intake = new CaseIntake(
			request.SourceSystemId.Trim(),
			request.CallbackUrl.Trim(),
			request.Nation.Trim(),
			request.OrgId.Trim(),
			request.ProducerType,
			displayData,
			request.InitialState?.Trim(),
			request.CreatedAt is { } createdAt ? AsUtc(createdAt) : null
		);
		return null;
	}

	private static string? RequireValue(string? value, string name) =>
		string.IsNullOrWhiteSpace(value) ? $"'{name}' is required and must be a non-empty string." : null;

	private static DateTime AsUtc(DateTime value) =>
		value.Kind == DateTimeKind.Unspecified
			? DateTime.SpecifyKind(value, DateTimeKind.Utc)
			: value.ToUniversalTime();
}
