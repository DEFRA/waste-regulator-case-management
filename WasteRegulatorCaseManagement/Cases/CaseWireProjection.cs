namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// Maps engine projections to API wire shapes. Kept out of
/// <see cref="CaseEndpoints"/> so endpoint methods stay within Sonar's
/// parameter-count budget (S107).
/// </summary>
internal static class CaseWireProjection
{
	internal static CaseResponse ToDetailResponse(CaseEngineProjection projection) =>
		CaseResponse.FromProjection(projection);

	internal static CaseListItemResponse ToListItemResponse(CaseEngineProjection projection) =>
		CaseListItemResponse.FromProjection(projection);
}