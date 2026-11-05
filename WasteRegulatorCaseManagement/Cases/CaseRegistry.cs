namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// Lookup over the case types registered by modules. Resolved as a
/// singleton; safe to inject anywhere in the application.
/// </summary>
public interface ICaseRegistry
{
    IReadOnlyCollection<ICaseType> Types { get; }

    ICaseType? Find(string typeId);
}

public sealed class CaseRegistry : ICaseRegistry
{
    private readonly Dictionary<string, ICaseType> _types;

    public CaseRegistry(IEnumerable<ICaseType> types)
    {
        _types = new Dictionary<string, ICaseType>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in types)
        {
            if (string.IsNullOrWhiteSpace(type.TypeId))
            {
                throw new InvalidCaseTypeException("Case type id cannot be empty.");
            }

            if (!_types.TryAdd(type.TypeId, type))
            {
                throw new DuplicateCaseTypeException(type.TypeId);
            }
        }
    }

    public IReadOnlyCollection<ICaseType> Types => _types.Values.ToList().AsReadOnly();

    public ICaseType? Find(string typeId) =>
        typeId is not null && _types.TryGetValue(typeId, out var type) ? type : null;
}

public sealed class DuplicateCaseTypeException(string typeId)
    : Exception($"A case type with id '{typeId}' is already registered.")
{
    public string TypeId { get; } = typeId;
}

public sealed class InvalidCaseTypeException(string message) : Exception(message);