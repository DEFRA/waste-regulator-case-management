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
		DisplayData: null,
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
	public void DisplayDataFiltersAreGroupedByName()
	{
		var query = CaseQueryBinding.FromRequest(s_noFilters with
		{
			DisplayData = ["producerType:DIRECT_PRODUCER", "producerType:COMPLIANCE_SCHEME", "size:large"],
		});

		query.DisplayData.Should().BeEquivalentTo(new Dictionary<string, IReadOnlyCollection<string>>
		{
			["producerType"] = ["DIRECT_PRODUCER", "COMPLIANCE_SCHEME"],
			["size"] = ["large"],
		});
	}

	[Fact]
	public void BlankDisplayDataEntriesAreDropped()
	{
		var query = CaseQueryBinding.FromRequest(s_noFilters with { DisplayData = ["", "   "] });

		query.DisplayData.Should().BeNull();
	}

	[Fact]
	public void DisplayDataValueIsKeptExactly()
	{
		var query = CaseQueryBinding.FromRequest(s_noFilters with { DisplayData = ["producerType: DIRECT_PRODUCER:x "] });

		query.DisplayData!["producerType"].Should().Equal(" DIRECT_PRODUCER:x ");
	}

	[Theory]
	[InlineData("producerType")]
	[InlineData("producerType:")]
	[InlineData(":DIRECT_PRODUCER")]
	[InlineData(" producerType:DIRECT_PRODUCER")]
	[InlineData("producer.type:DIRECT_PRODUCER")]
	[InlineData("$where:DIRECT_PRODUCER")]
	public void MalformedDisplayDataFilterIsReported(string entry)
	{
		var bind = () => CaseQueryBinding.FromRequest(s_noFilters with { DisplayData = ["producerType:DIRECT_PRODUCER", entry] });

		bind.Should().Throw<InvalidCaseQueryException>().Which.Message.Should().Contain($"'{entry}'");
	}

	[Fact]
	public void ALongMalformedDisplayDataFilterIsShortenedInTheError()
	{
		var entry = new string('x', 500);

		var bind = () => CaseQueryBinding.FromRequest(s_noFilters with { DisplayData = [entry] });

		bind.Should().Throw<InvalidCaseQueryException>().Which.Message
			.Should().Contain($"'{entry[..100]}…'").And.NotContain(entry[..101]);
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
