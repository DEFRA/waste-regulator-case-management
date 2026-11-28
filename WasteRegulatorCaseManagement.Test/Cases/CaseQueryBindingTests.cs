using WasteRegulatorCaseManagement.Cases;

namespace WasteRegulatorCaseManagement.Test.Cases;

public class CaseQueryBindingTests
{
	private static readonly CaseListRequest s_noFilters = new();

	[Fact]
	public void OrgIdIsNullWhenTheQueryValueIsWhitespace()
	{
		var query = CaseQueryBinding.FromRequest(new CaseListRequest { OrgId = "   " });

		query.OrgId.Should().BeNull();
	}

	[Fact]
	public void BlankAndRepeatedProducerTypesAreDropped()
	{
		var query = CaseQueryBinding.FromRequest(
			new CaseListRequest { ProducerType = [" DIRECT_PRODUCER ", "", "DIRECT_PRODUCER"] }
		);

		query.ProducerTypes.Should().Equal("DIRECT_PRODUCER");
	}

	[Fact]
	public void BlankAndRepeatedCaseTypesAreDropped()
	{
		var query = CaseQueryBinding.FromRequest(
			new CaseListRequest { CaseType = [" CSoC ", "", "CSoC"] }
		);

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
		var query = CaseQueryBinding.FromRequest(new CaseListRequest { Sort = token });

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
		var query = CaseQueryBinding.FromRequest(new CaseListRequest { Dir = dir });

		query.SortDescending.Should().Be(expectedDescending);
	}

	[Theory]
	[InlineData("sideways")]
	[InlineData("")]
	[InlineData("   ")]
	public void UnrecognisedOrBlankSortDirectionIsNull(string dir)
	{
		var query = CaseQueryBinding.FromRequest(new CaseListRequest { Dir = dir });

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