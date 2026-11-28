using System.ComponentModel;
using Microsoft.AspNetCore.Mvc;

namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// The filters, ordering and page a caseworker asks for when listing the
/// cases of their nation, as given in the query string of <c>GET /cases</c>.
/// </summary>
internal sealed class CaseListRequest
{
	[FromQuery(Name = "caseType")]
	[Description("Only cases of this case type. Repeat to match any of several.")]
	public string[]? CaseType { get; init; }

	[FromQuery(Name = "stateId")]
	[Description("Only cases in this state. Repeat to match any of several.")]
	public string[]? StateId { get; init; }

	[FromQuery(Name = "search")]
	[Description("Only cases whose id starts with this text, ignoring case.")]
	public string? Search { get; init; }

	[FromQuery(Name = "assigneeId")]
	[Description(
		"Only cases assigned to this user id. With `unassigned=true`, cases assigned to this user id or to no one."
	)]
	public string? AssigneeId { get; init; }

	[FromQuery(Name = "unassigned")]
	[Description("`true` for only the cases with no assignee.")]
	public bool? Unassigned { get; init; }

	[FromQuery(Name = "orgId")]
	[Description("Only cases of this organisation.")]
	public string? OrgId { get; init; }

	[FromQuery(Name = "producerType")]
	[Description(
		"Only cases of this producer type, such as `DIRECT_PRODUCER`. Repeat to match any of several."
	)]
	public string[]? ProducerType { get; init; }

	[FromQuery(Name = "isTerminal")]
	[Description("`true` for only concluded cases, `false` for only open ones.")]
	public bool? IsTerminal { get; init; }

	[FromQuery(Name = "sort")]
	[Description("`status` orders the cases by their case type's states, in the order the case type declares them.")]
	public string? Sort { get; init; }

	[FromQuery(Name = "dir")]
	[Description("`asc` or `desc`, the direction of `sort`.")]
	public string? Dir { get; init; }

	[FromQuery(Name = "page")]
	[Description("Page number, from 1 to 1000. Defaults to 1.")]
	public int? Page { get; init; }

	[FromQuery(Name = "pageSize")]
	[Description("Cases per page, up to 100. Defaults to 20.")]
	public int? PageSize { get; init; }
}