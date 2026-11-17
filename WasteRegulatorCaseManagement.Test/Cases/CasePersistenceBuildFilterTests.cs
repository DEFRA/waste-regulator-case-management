using WasteRegulatorCaseManagement.Cases;
using AwesomeAssertions.Execution;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace WasteRegulatorCaseManagement.Test.Cases;

/// <summary>
/// Direct unit coverage for <see cref="CasePersistence.BuildFilter"/>.
/// The class as a whole sits behind a real Mongo driver, but the filter
/// construction is pure logic and is the highest-leverage thing to keep
/// inside the coverage gate.
/// </summary>
public class CasePersistenceBuildFilterTests
{
	private static readonly string[] s_stateIdsSubmitted = ["submitted"];
	private static readonly string[] s_stateIdsSubmittedAndInReview = ["submitted", "in-review"];
	private static readonly string[] s_stateIdsWithdrawnAndSubmitted = ["withdrawn", "submitted"];
	private static readonly string[] s_typeIdsReAccreditation = ["re-accreditation"];
	private static readonly string[] s_typeIdsReAccreditationAndRegistration =
	[
		"re-accreditation",
		"registration",
	];

	private static readonly IBsonSerializer<Case> s_caseSerializer =
		BsonSerializer.SerializerRegistry.GetSerializer<Case>();

	private static BsonDocument Render(CaseQuery query)
	{
		var filter = CasePersistence.BuildFilter(query);
		return filter.Render(new RenderArgs<Case>(s_caseSerializer, BsonSerializer.SerializerRegistry));
	}

	[Fact]
	public void DefaultQueryFiltersNothing()
	{
		// RA-313: a bare query renders an EMPTY filter. Before RA-313 this
		// emitted a $nin excluding every terminal state, which is exactly what
		// kept withdrawn applications off the regulator's worklist.
		var doc = Render(new CaseQuery());

		doc.Should().BeEmpty();
	}

	[Theory]
	[InlineData("approved")]
	[InlineData("rejected")]
	[InlineData("withdrawn")]
	public void TerminalStateIsNeverExcluded(string terminalStateId)
	{
		// RA-313 AC01, at the filter level: nothing anywhere in the rendered
		// filter may exclude a terminal state.
		var doc = Render(new CaseQuery());

		doc.ToJson().Should().NotContain(terminalStateId);
	}

	[Fact]
	public void TypeIdsRenderAsInClause()
	{
		var doc = Render(new CaseQuery(CaseTypes: s_typeIdsReAccreditationAndRegistration));

		var expected = new BsonDocument("caseType", new BsonDocument("$in",
			new BsonArray { "re-accreditation", "registration" }));
		doc.Should().BeEquivalentTo(expected);
	}

	[Fact]
	public void StateIdsRenderAsInClause()
	{
		// RA-313: the caller's selection is the entire stateId clause — what
		// you ask for is what you get.
		var doc = Render(new CaseQuery(StateIds: s_stateIdsSubmittedAndInReview));

		var expected = new BsonDocument("stateId", new BsonDocument("$in",
			new BsonArray { "submitted", "in-review" }));
		doc.Should().BeEquivalentTo(expected);
	}

	[Theory]
	[InlineData("approved")]
	[InlineData("rejected")]
	[InlineData("withdrawn")]
	public void FilteringToASingleTerminalStateRendersThatStateAlone(string terminalStateId)
	{
		// The "Withdrawn" status checkbox on the Applications page lands here.
		// It worked before RA-313 only because of a special case that spared
		// explicitly-requested terminal states from the $nin; with the
		// exclusion gone it is now just an ordinary $in.
		var doc = Render(new CaseQuery(StateIds: new[] { terminalStateId }));

		var stateDoc = doc["stateId"].AsBsonDocument;
		using var scope = new AssertionScope();
		stateDoc["$in"].AsBsonArray[0].AsString.Should().Be(terminalStateId);
		stateDoc.Contains("$nin").Should().BeFalse("RA-313: no terminal-state exclusion.");
	}

	[Fact]
	public void MixedTerminalAndActiveStateIdsRenderAsOneInClause()
	{
		var doc = Render(new CaseQuery(StateIds: s_stateIdsWithdrawnAndSubmitted));

		var selected = doc["stateId"]["$in"].AsBsonArray.Select(v => v.AsString).ToList();
		using var scope = new AssertionScope();
		selected.Should().Equal(s_stateIdsWithdrawnAndSubmitted);
		doc["stateId"].AsBsonDocument.Contains("$nin").Should().BeFalse("RA-313: no terminal-state exclusion.");
	}

	[Fact]
	public void SearchRendersCaseInsensitivePrefixMatchOnId()
	{
		var doc = Render(new CaseQuery(Search: "  01kb  "));

		doc["_id"].AsBsonRegularExpression.Should().Be(new BsonRegularExpression("^01kb", "i"));
	}

	[Fact]
	public void SearchEscapesRegexMetacharacters()
	{
		var doc = Render(new CaseQuery(Search: "a.b*c"));

		var rendered = doc["_id"].AsBsonRegularExpression.Pattern;
		// Regex.Escape backslash-escapes the metacharacters; the literal
		// dot must not be interpreted as "any char".
		using var scope = new AssertionScope();
		rendered.Should().Contain(@"\.");
		rendered.Should().Contain(@"\*");
	}

	[Fact]
	public void BlankSearchIsIgnored()
	{
		var doc = Render(new CaseQuery(Search: "   "));

		doc.Should().BeEmpty();
	}

	[Fact]
	public void AssigneeIdAloneRendersAsEquality()
	{
		var doc = Render(new CaseQuery(AssigneeId: " user-1 "));

		doc["assignedToId"].AsString.Should().Be("user-1");
	}

	[Fact]
	public void UnassignedOnlyAloneRendersAsNullEquality()
	{
		var doc = Render(new CaseQuery(UnassignedOnly: true));

		doc["assignedToId"].Should().Be(BsonNull.Value);
	}

	[Fact]
	public void AssigneeIdWithUnassignedOnlyRendersAsOr()
	{
		var doc = Render(new CaseQuery(AssigneeId: "user-1", UnassignedOnly: true));

		var or = doc["$or"].AsBsonArray;
		or.Count.Should().Be(2);
		using var scope = new AssertionScope();
		or[0]["assignedToId"].AsString.Should().Be("user-1");
		or[1]["assignedToId"].Should().Be(BsonNull.Value);
	}

	[Fact]
	public void BlankAssigneeIdIsIgnored()
	{
		var doc = Render(new CaseQuery(AssigneeId: "   "));

		doc.Should().BeEmpty();
	}

	[Fact]
	public void MultipleClausesAreCombined()
	{
		var doc = Render(new CaseQuery(
			CaseTypes: s_typeIdsReAccreditation,
			StateIds: s_stateIdsSubmitted,
			AssigneeId: "user-1"));

		using var scope = new AssertionScope();
		doc["caseType"]["$in"].AsBsonArray[0].AsString.Should().Be("re-accreditation");
		doc["stateId"]["$in"].AsBsonArray[0].AsString.Should().Be("submitted");
		doc["assignedToId"].AsString.Should().Be("user-1");
	}
}
