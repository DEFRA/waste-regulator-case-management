using System.Collections.Concurrent;
using WasteRegulatorCaseManagement.Cases;

namespace WasteRegulatorCaseManagement.Test.TestSupport;

public sealed class InMemoryCaseModuleConfigStore : ICaseModuleConfigStore
{
    private readonly ConcurrentDictionary<string, CaseModuleConfig> _configs = new();

    public InMemoryCaseModuleConfigStore(IEnumerable<CaseModuleConfig> configs)
    {
        foreach (var config in configs)
        {
            _configs[config.CaseType] = config;
        }
    }

    public Task UpsertAsync(CaseModuleConfig config, CancellationToken cancellationToken)
    {
        _configs[config.CaseType] = config;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CaseModuleConfig>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CaseModuleConfig>>(_configs.Values.ToList());
}
