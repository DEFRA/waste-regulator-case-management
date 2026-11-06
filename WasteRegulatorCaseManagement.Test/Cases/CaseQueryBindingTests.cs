using WasteRegulatorCaseManagement.Cases;

namespace WasteRegulatorCaseManagement.Test.Cases;

public class CaseQueryBindingTests
{
	private static readonly CaseListRequest s_noFilters = new(
		CaseType: null,
		StateId: null,
		Search: null,
		AssigneeId: null,
		Unassigned: null,
		OrgId: null,
		IsTerminal: null,
		Sort: null,
		Dir: null,
		Page: null,
		PageSize: null
	);

	[Fact]
	public void OrgIdIsNullWhenTheQueryValueIsWhitespace()
	{
		var query = CaseQueryBinding.FromRequest(s_noFilters with { OrgId = "   " });

		query.OrgId.Should().BeNull();
	}

	[Fact]
	public void BlankAndRepeatedCaseTypesAreDropped()
	{
		var query = CaseQueryBinding.FromRequest(s_noFilters with { CaseType = [" CSoC ", "", "CSoC"] });

		query.CaseTypes.Should().Equal("CSoC");
	}

	[Fact]
	public void AbsentPagingAndAssignmentFallBackToTheDefaults()
	{
		var query = CaseQueryBinding.FromRequest(s_noFilters);

		query.Page.Should().Be(1);
		query.PageSize.Should().Be(CaseQuery.DefaultPageSize);
		query.UnassignedOnly.Should().BeFalse();
	}

	[Theory]
	[InlineData("status")]
	public void SortTokenIsBound(string token)
	{
		var query = CaseQueryBinding.FromRequest(s_noFilters with { Sort = token });

		query.Sort.Should().Be(token);
	}

	[Theory]
	[InlineData("desc", true)]
	[InlineData("DESC", true)]
	[InlineData("descending", true)]
	[InlineData("asc", false)]
	[InlineData("ascending", false)]
	public void SortDirectionIsParsed(string dir, bool expectedDescending)
	{
		var query = CaseQueryBinding.FromRequest(s_noFilters with { Dir = dir });

		query.SortDescending.Should().Be(expectedDescending);
	}

	[Theory]
	[InlineData("sideways")]
	[InlineData("")]
	[InlineData("   ")]
	public void UnrecognisedOrBlankSortDirectionIsNull(string dir)
	{
		var query = CaseQueryBinding.FromRequest(s_noFilters with { Dir = dir });

		query.SortDescending.Should().BeNull();
	}

	[Fact]
	public void MissingSortAndDirectionDefaultToNull()
	{
		var query = CaseQueryBinding.FromRequest(s_noFilters);

		query.Sort.Should().BeNull();
		query.SortDescending.Should().BeNull();
	}
}
