using System.Runtime.CompilerServices;
using WasteRegulatorCaseManagement.Utils.Mongo;
using WasteRegulatorCaseManagement.Cases;

namespace WasteRegulatorCaseManagement.Test.TestSupport;

/// <summary>
/// Forces the same Mongo BSON conventions Program.cs registers at
/// production startup to be installed before any test in this assembly
/// touches a Case serializer. Without this, any test class that
/// renders / serialises a Case before a class that uses
/// <see cref="MongoIntegrationFixture"/> sees PascalCase element
/// names (e.g. "CaseType" rather than "caseType") because the
/// <see cref="MongoConventions.Register"/> call is otherwise gated on
/// the integration fixture's static constructor — and xUnit test
/// ordering across collections is non-deterministic in CI.
/// </summary>
internal static class TestAssemblyInitializer
{
    [ModuleInitializer]
    internal static void Init()
    {
        MongoConventions.Register();
    }
}
