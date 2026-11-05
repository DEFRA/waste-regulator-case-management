using WasteRegulatorCaseManagement.WorkItems.Core;
using Microsoft.AspNetCore.Http;

namespace WasteRegulatorCaseManagement.Test.WorkItems.Core;

/// <summary>
/// Regression coverage for <see cref="WorkItemQueryBinding.FromQueryString"/>.
/// <c>submittedBy</c> is an ordinary caller-supplied filter — scoping
/// decisions are made by the frontend, not enforced by this binder.
/// </summary>
public class WorkItemQueryBindingTests
{
    private static readonly string[] s_expectedSingleMaterial = ["plastic"];
    private static readonly string[] s_expectedSingleWasteProcessingType = ["exporter"];
    private static readonly string[] s_materialsPlasticGlassAndPaper =
    [
        "plastic",
        "glass",
        "paper",
    ];
    private static readonly string[] s_wasteProcessingTypesExporterAndReprocessor =
    [
        "exporter",
        "reprocessor",
    ];

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
        var query = WorkItemQueryBinding.FromQueryString(Q((key, "other-tenant-id")));

        Assert.Equal("other-tenant-id", query.SubmittedBy);
        Assert.Equal("other-tenant-id", query.NormalisedSubmittedBy);
    }

    [Fact]
    public void SubmittedByIsBoundAlongsideOtherParameters()
    {
        var query = WorkItemQueryBinding.FromQueryString(Q(
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
        var query = WorkItemQueryBinding.FromQueryString(Q(("submittedBy", "   ")));

        Assert.Null(query.SubmittedBy);
    }

    [Fact]
    public void PageFallsBackToTheDefaultWhenTheQueryValueIsNotAnInteger()
    {
        // Covers ReadInt's `int.TryParse` false arm.
        var query = WorkItemQueryBinding.FromQueryString(Q(("page", "not-a-number")));

        Assert.Equal(1, query.Page);
    }

    [Fact]
    public void UnassignedIsFalseWhenTheQueryValueIsWhitespace()
    {
        // Covers ReadBool's `IsNullOrWhiteSpace` true arm.
        var query = WorkItemQueryBinding.FromQueryString(Q(("unassigned", "   ")));

        Assert.False(query.UnassignedOnly);
    }

    [Fact]
    public void EmptyQueryProducesNullSubmittedBy()
    {
        var query = WorkItemQueryBinding.FromQueryString(new QueryCollection());

        Assert.Null(query.SubmittedBy);
    }

    // ──────────────────────────── OrgId / RegistrationId / OrgName ──────────────────────────────

    [Fact]
    public void OrgIdIsBound()
    {
        var query = WorkItemQueryBinding.FromQueryString(Q(("orgId", "EPR-123")));

        Assert.Equal("EPR-123", query.OrgId);
    }

    [Fact]
    public void RegistrationIdIsBound()
    {
        var query = WorkItemQueryBinding.FromQueryString(Q(("registrationId", "abc-123")));

        Assert.Equal("abc-123", query.RegistrationId);
    }

    [Fact]
    public void OrgNameIsBound()
    {
        var query = WorkItemQueryBinding.FromQueryString(Q(("orgName", "Acme Ltd")));

        Assert.Equal("Acme Ltd", query.OrgName);
    }

    [Fact]
    public void MissingOrgFieldsDefaultToNull()
    {
        var query = WorkItemQueryBinding.FromQueryString(new QueryCollection());

        Assert.Null(query.OrgId);
        Assert.Null(query.RegistrationId);
        Assert.Null(query.OrgName);
    }

    // ─────────────────────────── RA-324 filter + sort params ──────────────────────────

    [Fact]
    public void OrganisationIsBound()
    {
        var query = WorkItemQueryBinding.FromQueryString(Q(("organisation", "Acme or ORG-1")));

        Assert.Equal("Acme or ORG-1", query.Organisation);
    }

    [Fact]
    public void SingleMaterialIsBound()
    {
        var query = WorkItemQueryBinding.FromQueryString(Q(("material", "plastic")));

        Assert.NotNull(query.Materials);
        Assert.Equal(s_expectedSingleMaterial, query.Materials!);
    }

    [Fact]
    public void RepeatedMaterialParamsAreAllBound()
    {
        var dict = new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["material"] = new Microsoft.Extensions.Primitives.StringValues(
                s_materialsPlasticGlassAndPaper)
        };
        var query = WorkItemQueryBinding.FromQueryString(new QueryCollection(dict));

        Assert.Equal(s_materialsPlasticGlassAndPaper, query.Materials!);
    }

    [Fact]
    public void MissingMaterialAndOrganisationDefaultToNull()
    {
        var query = WorkItemQueryBinding.FromQueryString(new QueryCollection());

        Assert.Null(query.Materials);
        Assert.Null(query.Organisation);
    }

    // ───────────────────────── WasteProcessingType (RA-412) ─────────────────────────

    [Fact]
    public void SingleWasteProcessingTypeIsBound()
    {
        var query = WorkItemQueryBinding.FromQueryString(Q(("wasteProcessingType", "exporter")));

        Assert.NotNull(query.WasteProcessingTypes);
        Assert.Equal(s_expectedSingleWasteProcessingType, query.WasteProcessingTypes!);
    }

    [Fact]
    public void RepeatedWasteProcessingTypeParamsAreAllBound()
    {
        var dict = new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["wasteProcessingType"] = new Microsoft.Extensions.Primitives.StringValues(
                s_wasteProcessingTypesExporterAndReprocessor)
        };
        var query = WorkItemQueryBinding.FromQueryString(new QueryCollection(dict));

        Assert.Equal(s_wasteProcessingTypesExporterAndReprocessor, query.WasteProcessingTypes!);
    }

    [Fact]
    public void MissingWasteProcessingTypeDefaultsToNull()
    {
        var query = WorkItemQueryBinding.FromQueryString(new QueryCollection());

        Assert.Null(query.WasteProcessingTypes);
    }

    [Theory]
    [InlineData("organisation")]
    [InlineData("status")]
    [InlineData("due-date")]
    public void SortTokenIsBound(string token)
    {
        var query = WorkItemQueryBinding.FromQueryString(Q(("sort", token)));

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
        var query = WorkItemQueryBinding.FromQueryString(Q(("dir", dir)));

        Assert.Equal(expectedDescending, query.SortDescending);
    }

    [Theory]
    [InlineData("sideways")]
    [InlineData("")]
    [InlineData("   ")]
    public void UnrecognisedOrBlankSortDirectionIsNull(string dir)
    {
        var query = WorkItemQueryBinding.FromQueryString(Q(("dir", dir)));

        Assert.Null(query.SortDescending);
    }

    [Fact]
    public void MissingSortAndDirectionDefaultToNull()
    {
        var query = WorkItemQueryBinding.FromQueryString(new QueryCollection());

        Assert.Null(query.Sort);
        Assert.Null(query.SortDescending);
    }
}