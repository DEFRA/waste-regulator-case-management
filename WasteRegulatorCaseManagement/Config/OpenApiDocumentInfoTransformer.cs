using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace WasteRegulatorCaseManagement.Config;

/// <summary>
/// Names, versions and describes the OpenAPI document and its tags.
/// </summary>
internal sealed class OpenApiDocumentInfoTransformer : IOpenApiDocumentTransformer
{
	private const string Description = """
		Holds the cases regulators work through. A source system's Case Intake Processor creates a case for each submission. Caseworkers, through the regulator frontend, assign it, add notes and conclude it, and the decision is sent back to the source system. Every change is recorded on the case's timeline.

		Source code: [DEFRA/waste-regulator-case-management](https://github.com/DEFRA/waste-regulator-case-management)

		## Callers

		Every request names its caller in `x-cdp-client-id`. Outside Development the caller signs the request with HMAC-SHA256, using the secret registered for its client id, in `x-cdp-auth-timestamp`, `x-cdp-auth-nonce` and `x-cdp-auth-signature`. A request without valid signed headers returns 401.

		- The **Case Intake Processor** creates cases, and is the only caller that can.
		- The **regulator frontend** acts for a caseworker and signs the caseworker's identity into `x-cdp-user-id`, `x-cdp-user-name`, `x-cdp-user-nation` and `x-cdp-user-email`.

		## Nations

		A caseworker sees and acts on the cases of their own nation only. Without `x-cdp-user-nation` a caseworker request returns 403, and a case in another nation returns 404.

		## Errors

		Errors are returned as problem details (RFC 9457), with the reason in `detail`.
		""";

	public Task TransformAsync(
		OpenApiDocument document,
		OpenApiDocumentTransformerContext context,
		CancellationToken cancellationToken
	)
	{
		document.Info.Title = "Waste Regulator Case Management";
		document.Info.Version = "0.1.0";
		document.Info.Description = Description;
		document.Tags = new HashSet<OpenApiTag>
		{
			new()
			{
				Name = "Cases",
				Description = "Creating cases, and the caseworker's work on them.",
			},
		};
		return Task.CompletedTask;
	}
}
