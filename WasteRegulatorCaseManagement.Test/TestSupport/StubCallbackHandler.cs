using System.Net;

namespace WasteRegulatorCaseManagement.Test.TestSupport;

/// <summary>
/// Stands in for a source system's callback endpoint: records each request
/// and answers with <see cref="Respond"/>, which returns 200 OK unless a
/// test replaces it.
/// </summary>
public sealed class StubCallbackHandler : HttpMessageHandler
{
	public List<RecordedCallback> Requests { get; } = [];

	public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
		_ => new HttpResponseMessage(HttpStatusCode.OK);

	protected override async Task<HttpResponseMessage> SendAsync(
		HttpRequestMessage request,
		CancellationToken cancellationToken
	)
	{
		Requests.Add(
			new RecordedCallback(
				request.Method,
				request.RequestUri!,
				request.Headers.Authorization?.ToString(),
				request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)
			)
		);
		return Respond(request);
	}

	// The client factory disposes expired handlers; this one lives as long as its test.
	protected override void Dispose(bool disposing) { }
}

public sealed record RecordedCallback(HttpMethod Method, Uri Uri, string? Authorization, string? Body);
