using WasteRegulatorCaseManagement.Utils.Http;

namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// A case list request that cannot be turned into a <see cref="CaseQuery"/>,
/// answered with a 400.
/// </summary>
public sealed class InvalidCaseQueryException(string message)
	: BadRequestException("Invalid case list filter", message);
