namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// The case types this instance accepts, replaced as a whole by
/// <see cref="Load"/> when <see cref="CaseModuleConfigLoader"/> reads them
/// from the database at startup.
/// </summary>
public sealed class CaseRegistry
{
	private volatile Dictionary<string, CaseModuleConfig> _types;

	public CaseRegistry(IEnumerable<CaseModuleConfig> types) => _types = Index(types);

	public IReadOnlyCollection<CaseModuleConfig> Types => _types.Values.ToList().AsReadOnly();

	public CaseModuleConfig? Find(string caseType) =>
		caseType is not null && _types.TryGetValue(caseType, out var type) ? type : null;

	public void Load(IEnumerable<CaseModuleConfig> types) => _types = Index(types);

	private static Dictionary<string, CaseModuleConfig> Index(IEnumerable<CaseModuleConfig> types)
	{
		var indexed = new Dictionary<string, CaseModuleConfig>(StringComparer.OrdinalIgnoreCase);
		foreach (var type in types)
		{
			Validate(type);
			indexed[type.CaseType] = type;
		}
		return indexed;
	}

	private static void Validate(CaseModuleConfig type)
	{
		var stateIds = type.States.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
		if (!stateIds.Contains(type.InitialStateId))
		{
			throw new InvalidCaseModuleConfigException(
				$"Case type '{type.CaseType}' starts in undeclared state '{type.InitialStateId}'.");
		}

		foreach (var transition in type.Transitions)
		{
			if (!stateIds.Contains(transition.FromStateId) || !stateIds.Contains(transition.ToStateId))
			{
				throw new InvalidCaseModuleConfigException(
					$"Case type '{type.CaseType}' action '{transition.ActionId}' moves between undeclared states.");
			}
		}

		foreach (var mappedStateId in type.CallbackMappings.Keys.Where(id => !stateIds.Contains(id)))
		{
			throw new InvalidCaseModuleConfigException(
				$"Case type '{type.CaseType}' maps a callback for undeclared state '{mappedStateId}'.");
		}
	}
}

public sealed class InvalidCaseModuleConfigException(string message) : Exception(message);
