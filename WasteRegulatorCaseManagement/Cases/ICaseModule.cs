using Microsoft.AspNetCore.Routing;

namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// A self-contained case module. Each case type lives behind one of
/// these so it can register its own services and HTTP endpoints without the
/// core application needing to know about it. The only change required to add a
/// new type is to call <c>AddCaseModule&lt;T&gt;()</c> in <c>Program.cs</c>.
/// </summary>
public interface ICaseModule
{
    /// <summary>The type this module owns.</summary>
    ICaseType Type { get; }

    /// <summary>
    /// Register the module's service objects with DI. Called once during startup.
    /// Implementations must not register anything that conflicts with another
    /// module's registrations (use module-scoped interfaces). The application
    /// <paramref name="configuration"/> is supplied so a module can gate its
    /// own registrations on config (e.g. RA-422's <c>Notify:Enabled</c> flag)
    /// without reaching outside its folder.
    /// </summary>
    void RegisterServices(IServiceCollection services, IConfiguration configuration);

    /// <summary>
    /// Map the module's HTTP endpoints. Called after the framework has built its
    /// own routes. Modules should mount under a path that includes their CaseType
    /// to stay isolated, e.g. <c>/cases/&lt;type-id&gt;/...</c>.
    /// </summary>
    void MapEndpoints(IEndpointRouteBuilder endpoints);
}