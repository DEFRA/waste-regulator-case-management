using AwesomeAssertions.Execution;
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

		using var scope = new AssertionScope();
		registry.Find("alpha").Should().BeSameAs(typeA);
		registry.Find("beta").Should().BeSameAs(typeB);
	}

	[Fact]
	public void Find_is_case_insensitive_on_type_id()
	{
		var typeA = TestCaseType.Create("Alpha", "Alpha");

		var registry = new CaseRegistry([typeA]);

		registry.Find("ALPHA").Should().BeSameAs(typeA);
	}

	[Fact]
	public void Find_returns_null_when_type_is_not_registered()
	{
		var registry = new CaseRegistry([TestCaseType.Create("alpha", "Alpha")]);

		registry.Find("missing").Should().BeNull();
	}

	[Fact]
	public void Types_lists_every_registered_type()
	{
		var typeA = TestCaseType.Create("alpha", "Alpha");
		var typeB = TestCaseType.Create("beta", "Beta");

		var registry = new CaseRegistry([typeA, typeB]);

		using var scope = new AssertionScope();
		registry.Types.Should().HaveCount(2);
		registry.Types.Should().Contain(typeA);
		registry.Types.Should().Contain(typeB);
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
		registry.Types.Should().ContainSingle().Which.Should().BeSameAs(beta);
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
		var load = () => registry.Load([invalid]);

		// Assert
		load.Should().Throw<InvalidCaseModuleConfigException>().WithMessage("*undeclared state 'submitted'*");
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
		var load = () => registry.Load([invalid]);

		// Assert
		load.Should().Throw<InvalidCaseModuleConfigException>().WithMessage("*action 'accept'*");
	}

}
