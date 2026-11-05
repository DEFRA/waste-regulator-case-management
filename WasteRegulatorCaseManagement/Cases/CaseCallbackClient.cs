using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.IO;

namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// PATCHes a concluded case's decision to its <see cref="Case.CallbackUrl"/>,
/// authenticating with the Basic credentials configured for the case type
/// under <c>CALLBACK_AUTH:{caseType}</c> as <c>clientId:clientSecret</c>.
/// </summary>
public sealed class CaseCallbackClient(
	IHttpClientFactory httpClientFactory,
	IConfiguration configuration,
	ILogger<CaseCallbackClient> logger
) : ICaseCallbackClient
{
	public const string HttpClientName = nameof(CaseCallbackClient);

	private static readonly JsonWriterSettings s_jsonSettings = new()
	{
		OutputMode = JsonOutputMode.RelaxedExtendedJson,
	};

	public async Task<string?> SendAsync(
		string caseType,
		string callbackUrl,
		BsonDocument payload,
		CancellationToken cancellationToken
	)
	{
		var credentials = configuration[$"CALLBACK_AUTH:{caseType}"];
		if (string.IsNullOrWhiteSpace(credentials))
		{
			logger.LogWarning("No callback credentials are configured for case type {CaseType}", caseType);
		}

		var httpClient = httpClientFactory.CreateClient(HttpClientName);
		try
		{
			using var patch = Request(HttpMethod.Patch, callbackUrl, credentials);
			patch.Content = new StringContent(payload.ToJson(s_jsonSettings), Encoding.UTF8, "application/json");
			using var response = await httpClient.SendAsync(patch, cancellationToken);

			if (response.IsSuccessStatusCode)
			{
				return null;
			}

			// The source system refuses a decision it already holds, so a
			// PATCH that reached it before this service's own write failed
			// is applied when the resource already carries the payload's
			// values.
			if (
				response.StatusCode == HttpStatusCode.UnprocessableEntity
				&& await AlreadyAppliedAsync(httpClient, callbackUrl, credentials, payload, cancellationToken)
			)
			{
				return null;
			}

			return $"{callbackUrl} returned {(int)response.StatusCode} {response.ReasonPhrase}.";
		}
		catch (Exception exception)
			when (exception is HttpRequestException or JsonException
				|| (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
		{
			logger.LogError(exception, "Callback to {CallbackUrl} failed", callbackUrl);
			return $"{callbackUrl} could not be reached: {exception.Message}";
		}
	}

	private static async Task<bool> AlreadyAppliedAsync(
		HttpClient httpClient,
		string callbackUrl,
		string? credentials,
		BsonDocument payload,
		CancellationToken cancellationToken
	)
	{
		using var get = Request(HttpMethod.Get, callbackUrl, credentials);
		using var response = await httpClient.SendAsync(get, cancellationToken);
		if (!response.IsSuccessStatusCode)
		{
			return false;
		}

		using var resource = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
		var shared = payload
			.Where(element => element.Value.IsString)
			.Where(element => resource.RootElement.TryGetProperty(element.Name, out _))
			.ToList();

		return shared.Count > 0
			&& shared.All(element =>
				resource.RootElement.GetProperty(element.Name) is { ValueKind: JsonValueKind.String } value
				&& value.GetString() == element.Value.AsString
			);
	}

	private static HttpRequestMessage Request(HttpMethod method, string callbackUrl, string? credentials)
	{
		var request = new HttpRequestMessage(method, callbackUrl);
		if (!string.IsNullOrWhiteSpace(credentials))
		{
			request.Headers.Authorization = new AuthenticationHeaderValue(
				"Basic",
				Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials))
			);
		}
		return request;
	}
}
