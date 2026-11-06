using WasteRegulatorCaseManagement.Cases;

namespace WasteRegulatorCaseManagement.Test.Cases;

/// <summary>
/// Unit tests for <see cref="CaseQuery"/>'s computed clamping
/// properties. These are pure and cheap to exercise directly, so no
/// mocking/fixture is needed.
/// </summary>
public class CaseQueryTests
{
	[Fact]
	public void NormalisedPage_clamps_a_page_below_one_up_to_one()
	{
		var query = new CaseQuery(Page: 0);

		Assert.Equal(1, query.NormalisedPage);
	}

	[Fact]
	public void NormalisedPage_clamps_a_page_above_the_cap_down_to_the_cap()
	{
		var query = new CaseQuery(Page: CaseQuery.MaxPage + 1);

		Assert.Equal(CaseQuery.MaxPage, query.NormalisedPage);
	}

	[Fact]
	public void NormalisedPage_passes_through_a_page_within_range()
	{
		var query = new CaseQuery(Page: 5);

		Assert.Equal(5, query.NormalisedPage);
	}

	[Fact]
	public void NormalisedPageSize_clamps_a_size_below_the_minimum_up_to_the_minimum()
	{
		var query = new CaseQuery(PageSize: CaseQuery.MinPageSize - 1);

		Assert.Equal(CaseQuery.MinPageSize, query.NormalisedPageSize);
	}

	[Fact]
	public void NormalisedPageSize_clamps_a_size_above_the_maximum_down_to_the_maximum()
	{
		var query = new CaseQuery(PageSize: CaseQuery.MaxPageSize + 1);

		Assert.Equal(CaseQuery.MaxPageSize, query.NormalisedPageSize);
	}

	[Fact]
	public void NormalisedPageSize_passes_through_a_size_within_range()
	{
		var query = new CaseQuery(PageSize: 50);

		Assert.Equal(50, query.NormalisedPageSize);
	}
}
