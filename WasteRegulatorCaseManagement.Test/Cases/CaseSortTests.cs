using WasteRegulatorCaseManagement.Cases;
using MongoDB.Bson;

namespace WasteRegulatorCaseManagement.Test.Cases;

/// <summary>
/// RA-324: unit coverage for <see cref="CaseSort"/>, the pure helper that
/// turns the <c>?sort=</c> / <c>?dir=</c> params into aggregation stages. The
/// tricky expression (status workflow ranking) is asserted here without a
/// database.
/// </summary>
public class CaseSortTests
{
	private static readonly IReadOnlyDictionary<string, int> s_rank =
		new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
		{
			["submitted"] = 0,
			["duly-made"] = 1,
			["approved"] = 2
		};

	private static CaseRegistry Registry(params CaseModuleConfig[] types) =>
		new CaseRegistry(types);

	// ─────────────────────────────── StatusRank ──────────────────────────────

	[Fact]
	public void StatusRank_ranks_states_in_declaration_order()
	{
		var registry = Registry(TestCaseType.Create("t1", "T1", states: new[]
		{
			new CaseState("submitted", "Submitted"),
			new CaseState("duly-made", "Duly made"),
			new CaseState("approved", "Approved", IsTerminal: true)
		}));

		var rank = CaseSort.StatusRank(registry);

		Assert.Equal(0, rank["submitted"]);
		Assert.Equal(1, rank["duly-made"]);
		Assert.Equal(2, rank["approved"]);
	}

	[Fact]
	public void StatusRank_is_case_insensitive()
	{
		var registry = Registry(TestCaseType.Create("t1", "T1", states: new[]
		{
			new CaseState("submitted", "Submitted")
		}));

		var rank = CaseSort.StatusRank(registry);

		Assert.Equal(0, rank["SUBMITTED"]);
	}

	[Fact]
	public void StatusRank_first_declaration_wins_across_types()
	{
		var registry = Registry(
			TestCaseType.Create("t1", "T1", states: new[]
			{
				new CaseState("submitted", "Submitted"),
				new CaseState("shared", "Shared")
			}),
			TestCaseType.Create("t2", "T2", initialState: new CaseState("shared", "Shared"), states: new[]
			{
				new CaseState("shared", "Shared"),
				new CaseState("other", "Other")
			}));

		var rank = CaseSort.StatusRank(registry);

		// 'shared' keeps the rank from its first (t1) appearance, and 'other'
		// continues the global counter rather than resetting per type.
		Assert.Equal(1, rank["shared"]);
		Assert.Equal(2, rank["other"]);
	}

	[Fact]
	public void StatusRank_throws_on_null_registry()
	{
		Assert.Throws<ArgumentNullException>(() => CaseSort.StatusRank(null!));
	}

	// ─────────────────────────────── BuildStages ─────────────────────────────

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("submittedAt")]
	[InlineData("nonsense")]
	public void BuildStages_returns_null_for_default_or_unknown_sort(string? token)
	{
		Assert.Null(CaseSort.BuildStages(token, descendingOverride: null, s_rank));
	}

	[Theory]
	[InlineData("Status", CaseSort.RankField)]
	public void BuildStages_normalises_token_casing_and_whitespace(string token, string expectedComputedField)
	{
		// Trimmed + lower-cased before matching, and — crucially — resolves to
		// the INTENDED column: assert an identifying computed field of that
		// column's $addFields stage, not merely that some stage came back.
		var stages = CaseSort.BuildStages(token, descendingOverride: null, s_rank);

		Assert.NotNull(stages);
		Assert.True(
			stages!.Value.AddFields!.Contains(expectedComputedField),
			$"Token '{token}' should resolve to the stage adding '{expectedComputedField}'.");
	}

	[Fact]
	public void BuildStages_throws_on_null_status_rank()
	{
		Assert.Throws<ArgumentNullException>(() =>
			CaseSort.BuildStages("status", descendingOverride: null, null!));
	}

	[Fact]
	public void BuildStages_status_builds_switch_branches_in_rank_order()
	{
		var stages = CaseSort.BuildStages("status", descendingOverride: null, s_rank);

		var (addFields, sort) = stages!.Value;
		var switchDoc = addFields![CaseSort.RankField]["$switch"].AsBsonDocument;
		var branches = switchDoc["branches"].AsBsonArray;
		Assert.Equal(3, branches.Count);
		// Branches ordered by ascending rank: submitted(0), duly-made(1), approved(2).
		Assert.Equal("$stateId", branches[0]["case"]["$eq"][0].AsString);
		Assert.Equal("submitted", branches[0]["case"]["$eq"][1].AsString);
		Assert.Equal(0, branches[0]["then"].AsInt32);
		Assert.Equal("duly-made", branches[1]["case"]["$eq"][1].AsString);
		Assert.Equal(2, branches[2]["then"].AsInt32);
		// Unknown states fall to the far end.
		Assert.Equal(int.MaxValue, switchDoc["default"].AsInt32);
		Assert.Equal(1, sort[CaseSort.RankField].AsInt32);
	}

	[Fact]
	public void BuildStages_status_descending_flips_rank_direction()
	{
		var stages = CaseSort.BuildStages("status", descendingOverride: true, s_rank);

		var (_, sort) = stages!.Value;
		Assert.Equal(-1, sort[CaseSort.RankField].AsInt32);
	}

}
