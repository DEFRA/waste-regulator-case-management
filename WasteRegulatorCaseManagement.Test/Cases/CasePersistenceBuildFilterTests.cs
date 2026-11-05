using WasteRegulatorCaseManagement.Cases;
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

		Assert.Equal(new BsonDocument(), doc);
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

		Assert.DoesNotContain(terminalStateId, doc.ToJson());
	}

	[Fact]
	public void TypeIdsRenderAsInClause()
	{
		var doc = Render(new CaseQuery(CaseTypes: s_typeIdsReAccreditationAndRegistration));

		var expected = new BsonDocument("caseType", new BsonDocument("$in",
			new BsonArray { "re-accreditation", "registration" }));
		Assert.Equal(expected, doc);
	}

	[Fact]
	public void StateIdsRenderAsInClause()
	{
		// RA-313: the caller's selection is the entire stateId clause — what
		// you ask for is what you get.
		var doc = Render(new CaseQuery(StateIds: s_stateIdsSubmittedAndInReview));

		var expected = new BsonDocument("stateId", new BsonDocument("$in",
			new BsonArray { "submitted", "in-review" }));
		Assert.Equal(expected, doc);
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
		Assert.Equal(terminalStateId, stateDoc["$in"].AsBsonArray[0].AsString);
		Assert.False(stateDoc.Contains("$nin"), "RA-313: no terminal-state exclusion.");
	}

	[Fact]
	public void MixedTerminalAndActiveStateIdsRenderAsOneInClause()
	{
		var doc = Render(new CaseQuery(StateIds: s_stateIdsWithdrawnAndSubmitted));

		var selected = doc["stateId"]["$in"].AsBsonArray.Select(v => v.AsString).ToList();
		Assert.Equal(s_stateIdsWithdrawnAndSubmitted, selected);
		Assert.False(doc["stateId"].AsBsonDocument.Contains("$nin"), "RA-313: no terminal-state exclusion.");
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
		Assert.Contains(@"\.", rendered);
		Assert.Contains(@"\*", rendered);
	}

	[Fact]
	public void BlankSearchIsIgnored()
	{
		var doc = Render(new CaseQuery(Search: "   "));

		Assert.Equal(new BsonDocument(), doc);
	}

	[Fact]
	public void AssigneeIdAloneRendersAsEquality()
	{
		var doc = Render(new CaseQuery(AssigneeId: " user-1 "));

		Assert.Equal("user-1", doc["assignedToId"].AsString);
	}

	[Fact]
	public void UnassignedOnlyAloneRendersAsNullEquality()
	{
		var doc = Render(new CaseQuery(UnassignedOnly: true));

		Assert.Equal(BsonNull.Value, doc["assignedToId"]);
	}

	[Fact]
	public void AssigneeIdWithUnassignedOnlyRendersAsOr()
	{
		var doc = Render(new CaseQuery(AssigneeId: "user-1", UnassignedOnly: true));

		var or = doc["$or"].AsBsonArray;
		Assert.Equal(2, or.Count);
		Assert.Equal("user-1", or[0]["assignedToId"].AsString);
		Assert.Equal(BsonNull.Value, or[1]["assignedToId"]);
	}

	[Fact]
	public void BlankAssigneeIdIsIgnored()
	{
		var doc = Render(new CaseQuery(AssigneeId: "   "));

		Assert.Equal(new BsonDocument(), doc);
	}

	[Fact]
	public void MultipleClausesAreCombined()
	{
		var doc = Render(new CaseQuery(
			CaseTypes: s_typeIdsReAccreditation,
			StateIds: s_stateIdsSubmitted,
			AssigneeId: "user-1"));

		Assert.Equal("re-accreditation", doc["caseType"]["$in"].AsBsonArray[0].AsString);
		Assert.Equal("submitted", doc["stateId"]["$in"].AsBsonArray[0].AsString);
		Assert.Equal("user-1", doc["assignedToId"].AsString);
	}
}
