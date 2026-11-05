using System.Text.Json;
using Microsoft.Extensions.Hosting;

namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// At startup, writes each case type configuration embedded under
/// <c>Cases/CaseModuleConfigs</c> to the database, then loads every case
/// type in the database into the <see cref="CaseRegistry"/>. Completes
/// before the server accepts requests, so intake never sees an empty
/// registry.
/// </summary>
internal sealed class CaseModuleConfigLoader(
	ICaseModuleConfigStore store,
	CaseRegistry registry,
	ILogger<CaseModuleConfigLoader> logger
) : IHostedService
{
	private const string ResourcePrefix = "WasteRegulatorCaseManagement.Cases.CaseModuleConfigs.";

	public async Task StartAsync(CancellationToken cancellationToken)
	{
		foreach (var config in EmbeddedConfigs())
		{
			await store.UpsertAsync(config, cancellationToken);
		}

		var configs = await store.ListAsync(cancellationToken);
		registry.Load(configs);
		logger.LogInformation(
			"Loaded case types: {CaseTypes}",
			string.Join(", ", configs.Select(c => c.CaseType))
		);
	}

	public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	internal static IEnumerable<CaseModuleConfig> EmbeddedConfigs()
	{
		var assembly = typeof(CaseModuleConfigLoader).Assembly;
		foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
		{
			using var stream = assembly.GetManifestResourceStream(name)!;
			yield return JsonSerializer.Deserialize<CaseModuleConfig>(stream, JsonSerializerOptions.Web)
				?? throw new InvalidCaseModuleConfigException($"Embedded case type configuration '{name}' is empty.");
		}
	}
}
