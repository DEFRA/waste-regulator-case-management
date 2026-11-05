namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// Lookup over the case types registered by modules. Resolved as a
/// singleton; safe to inject anywhere in the application.
/// </summary>
public interface ICaseRegistry
{
    IReadOnlyCollection<ICaseType> Types { get; }

    ICaseType? Find(string caseType);
}

public sealed class CaseRegistry : ICaseRegistry
{
    private readonly Dictionary<string, ICaseType> _types;

    public CaseRegistry(IEnumerable<ICaseType> types)
    {
        _types = new Dictionary<string, ICaseType>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in types)
        {
            if (string.IsNullOrWhiteSpace(type.CaseType))
            {
                throw new InvalidCaseTypeException("Case case type cannot be empty.");
            }

            if (!_types.TryAdd(type.CaseType, type))
            {
                throw new DuplicateCaseTypeException(type.CaseType);
            }
        }
    }

    public IReadOnlyCollection<ICaseType> Types => _types.Values.ToList().AsReadOnly();

    public ICaseType? Find(string caseType) =>
        caseType is not null && _types.TryGetValue(caseType, out var type) ? type : null;
}

public sealed class DuplicateCaseTypeException(string caseType)
    : Exception($"A case type with id '{caseType}' is already registered.")
{
    public string CaseType { get; } = caseType;
}

public sealed class InvalidCaseTypeException(string message) : Exception(message);