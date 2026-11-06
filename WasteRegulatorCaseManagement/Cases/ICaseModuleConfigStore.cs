namespace WasteRegulatorCaseManagement.Cases;

public interface ICaseModuleConfigStore
{
	Task UpsertAsync(CaseModuleConfig config, CancellationToken cancellationToken);

	Task<IReadOnlyList<CaseModuleConfig>> ListAsync(CancellationToken cancellationToken);
}
