using WasteRegulatorCaseManagement.Cases;

namespace WasteRegulatorCaseManagement.Test.Cases;

public class CaseRegistryTests
{
    [Fact]
    public void Find_returns_registered_type_by_id()
    {
        var typeA = new TestCaseType("alpha", "Alpha");
        var typeB = new TestCaseType("beta", "Beta");

        var registry = new CaseRegistry([typeA, typeB]);

        Assert.Same(typeA, registry.Find("alpha"));
        Assert.Same(typeB, registry.Find("beta"));
    }

    [Fact]
    public void Find_is_case_insensitive_on_type_id()
    {
        var typeA = new TestCaseType("Alpha", "Alpha");

        var registry = new CaseRegistry([typeA]);

        Assert.Same(typeA, registry.Find("ALPHA"));
    }

    [Fact]
    public void Find_returns_null_when_type_is_not_registered()
    {
        var registry = new CaseRegistry([new TestCaseType("alpha", "Alpha")]);

        Assert.Null(registry.Find("missing"));
    }

    [Fact]
    public void Types_lists_every_registered_type()
    {
        var typeA = new TestCaseType("alpha", "Alpha");
        var typeB = new TestCaseType("beta", "Beta");

        var registry = new CaseRegistry([typeA, typeB]);

        Assert.Equal(2, registry.Types.Count);
        Assert.Contains(typeA, registry.Types);
        Assert.Contains(typeB, registry.Types);
    }

    [Fact]
    public void Constructor_throws_when_a_type_id_is_blank()
    {
        var invalid = new TestCaseType(" ", "Blank");

        Assert.Throws<InvalidCaseTypeException>(() => new CaseRegistry([invalid]));
    }

    [Fact]
    public void Constructor_throws_when_two_types_share_an_id()
    {
        var first = new TestCaseType("alpha", "First");
        var second = new TestCaseType("alpha", "Second");

        var ex = Assert.Throws<DuplicateCaseTypeException>(
            () => new CaseRegistry([first, second]));
        Assert.Equal("alpha", ex.CaseType);
    }
}