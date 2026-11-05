using WasteRegulatorCaseManagement.Cases;
using Microsoft.AspNetCore.Http;

namespace WasteRegulatorCaseManagement.Test.Cases;

/// <summary>
/// Regression coverage for <see cref="CaseQueryBinding.FromQueryString"/>.
/// <c>submittedBy</c> is an ordinary caller-supplied filter — scoping
/// decisions are made by the frontend, not enforced by this binder.
/// </summary>
public class CaseQueryBindingTests
{

    private static IQueryCollection Q(params (string Key, string Value)[] pairs)
    {
        var dict = new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in pairs)
        {
            dict[k] = v;
        }
        return new QueryCollection(dict);
    }

    [Theory]
    [InlineData("submittedBy")]
    [InlineData("SubmittedBy")]
    [InlineData("SUBMITTEDBY")]
    [InlineData("submittedby")]
    public void SubmittedByQueryParameterIsBoundRegardlessOfCase(string key)
    {
        var query = CaseQueryBinding.FromQueryString(Q((key, "other-tenant-id")));

        Assert.Equal("other-tenant-id", query.SubmittedBy);
        Assert.Equal("other-tenant-id", query.NormalisedSubmittedBy);
    }

    [Fact]
    public void SubmittedByIsBoundAlongsideOtherParameters()
    {
        var query = CaseQueryBinding.FromQueryString(Q(
            ("submittedBy", "other-tenant-id"),
            ("typeId", "re-accreditation"),
            ("page", "2"),
            ("pageSize", "10")));

        Assert.Equal("other-tenant-id", query.SubmittedBy);
        Assert.Equal(2, query.Page);
        Assert.Equal(10, query.PageSize);
        Assert.NotNull(query.TypeIds);
        Assert.Contains("re-accreditation", query.TypeIds!);
    }

    [Fact]
    public void SubmittedByIsNullWhenTheQueryValueIsWhitespace()
    {
        // Covers ReadString's `IsNullOrWhiteSpace` true arm — every other
        // test supplies a real value.
        var query = CaseQueryBinding.FromQueryString(Q(("submittedBy", "   ")));

        Assert.Null(query.SubmittedBy);
    }

    [Fact]
    public void PageFallsBackToTheDefaultWhenTheQueryValueIsNotAnInteger()
    {
        // Covers ReadInt's `int.TryParse` false arm.
        var query = CaseQueryBinding.FromQueryString(Q(("page", "not-a-number")));

        Assert.Equal(1, query.Page);
    }

    [Fact]
    public void UnassignedIsFalseWhenTheQueryValueIsWhitespace()
    {
        // Covers ReadBool's `IsNullOrWhiteSpace` true arm.
        var query = CaseQueryBinding.FromQueryString(Q(("unassigned", "   ")));

        Assert.False(query.UnassignedOnly);
    }

    [Fact]
    public void EmptyQueryProducesNullSubmittedBy()
    {
        var query = CaseQueryBinding.FromQueryString(new QueryCollection());

        Assert.Null(query.SubmittedBy);
    }

    // ───────────────────────── Sort (RA-324) ─────────────────────────

    [Theory]
    [InlineData("status")]
    public void SortTokenIsBound(string token)
    {
        var query = CaseQueryBinding.FromQueryString(Q(("sort", token)));

        Assert.Equal(token, query.Sort);
    }

    [Theory]
    [InlineData("desc", true)]
    [InlineData("DESC", true)]
    [InlineData("descending", true)]
    [InlineData("asc", false)]
    [InlineData("ascending", false)]
    public void SortDirectionIsParsed(string dir, bool expectedDescending)
    {
        var query = CaseQueryBinding.FromQueryString(Q(("dir", dir)));

        Assert.Equal(expectedDescending, query.SortDescending);
    }

    [Theory]
    [InlineData("sideways")]
    [InlineData("")]
    [InlineData("   ")]
    public void UnrecognisedOrBlankSortDirectionIsNull(string dir)
    {
        var query = CaseQueryBinding.FromQueryString(Q(("dir", dir)));

        Assert.Null(query.SortDescending);
    }

    [Fact]
    public void MissingSortAndDirectionDefaultToNull()
    {
        var query = CaseQueryBinding.FromQueryString(new QueryCollection());

        Assert.Null(query.Sort);
        Assert.Null(query.SortDescending);
    }
}