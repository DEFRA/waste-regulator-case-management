using MongoDB.Bson;
using WasteRegulatorCaseManagement.Cases;

namespace WasteRegulatorCaseManagement.Test.Cases;

public class CaseRegistryTests
{
	[Fact]
	public void Find_returns_registered_type_by_id()
	{
		var typeA = TestCaseType.Create("alpha", "Alpha");
		var typeB = TestCaseType.Create("beta", "Beta");

		var registry = new CaseRegistry([typeA, typeB]);

		Assert.Same(typeA, registry.Find("alpha"));
		Assert.Same(typeB, registry.Find("beta"));
	}

	[Fact]
	public void Find_is_case_insensitive_on_type_id()
	{
		var typeA = TestCaseType.Create("Alpha", "Alpha");

		var registry = new CaseRegistry([typeA]);

		Assert.Same(typeA, registry.Find("ALPHA"));
	}

	[Fact]
	public void Find_returns_null_when_type_is_not_registered()
	{
		var registry = new CaseRegistry([TestCaseType.Create("alpha", "Alpha")]);

		Assert.Null(registry.Find("missing"));
	}

	[Fact]
	public void Types_lists_every_registered_type()
	{
		var typeA = TestCaseType.Create("alpha", "Alpha");
		var typeB = TestCaseType.Create("beta", "Beta");

		var registry = new CaseRegistry([typeA, typeB]);

		Assert.Equal(2, registry.Types.Count);
		Assert.Contains(typeA, registry.Types);
		Assert.Contains(typeB, registry.Types);
	}

	[Fact]
	public void Load_replaces_every_registered_type()
	{
		// Arrange
		var registry = new CaseRegistry([TestCaseType.Create("alpha", "Alpha")]);
		var beta = TestCaseType.Create("beta", "Beta");

		// Act
		registry.Load([beta]);

		// Assert
		Assert.Same(beta, Assert.Single(registry.Types));
	}

	[Fact]
	public void Load_throws_when_the_initial_state_is_not_declared()
	{
		// Arrange
		var registry = new CaseRegistry([]);
		var invalid = TestCaseType.Create(
			"alpha",
			"Alpha",
			initialState: new CaseState("submitted", "Submitted"),
			states: [new CaseState("accepted", "Accepted", IsTerminal: true)]);

		// Act
		var exception = Record.Exception(() => registry.Load([invalid]));

		// Assert
		Assert.IsType<InvalidCaseModuleConfigException>(exception);
		Assert.Contains("undeclared state 'submitted'", exception.Message);
	}

	[Fact]
	public void Load_throws_when_a_transition_moves_to_an_undeclared_state()
	{
		// Arrange
		var registry = new CaseRegistry([]);
		var invalid = TestCaseType.Create(
			"alpha",
			"Alpha",
			transitions: [new CaseTransition("accept", "Accept", "submitted", "accepted")]);

		// Act
		var exception = Record.Exception(() => registry.Load([invalid]));

		// Assert
		Assert.IsType<InvalidCaseModuleConfigException>(exception);
		Assert.Contains("action 'accept'", exception.Message);
	}

	[Fact]
	public void Load_throws_when_a_callback_is_mapped_for_an_undeclared_state()
	{
		// Arrange
		var registry = new CaseRegistry([]);
		var invalid = new CaseModuleConfig
		{
			CaseType = "alpha",
			DisplayName = "Alpha",
			TemplateVersion = "v1",
			InitialStateId = "submitted",
			States = [new CaseState("submitted", "Submitted")],
			Transitions = [],
			CallbackMappings = new() { ["accepted"] = new BsonDocument("status", "Accepted") },
		};

		// Act
		var exception = Record.Exception(() => registry.Load([invalid]));

		// Assert
		Assert.IsType<InvalidCaseModuleConfigException>(exception);
		Assert.Contains("undeclared state 'accepted'", exception.Message);
	}
}
