using WasteRegulatorCaseManagement.Config;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// DI and routing wiring for the case framework. Adding a new case
/// type to the application means:
/// <list type="number">
///   <item>Create a folder under <c>Cases/&lt;TypeName&gt;</c></item>
///   <item>Implement <see cref="ICaseType"/> and <see cref="ICaseModule"/></item>
///   <item>Call <c>services.AddCaseModule&lt;YourModule&gt;()</c> in <c>Program.cs</c></item>
/// </list>
/// No changes are required to other modules or to core code.
/// </summary>
public static class CaseModuleExtensions
{
    /// <summary>
    /// Register the framework itself. Call once before adding modules.
    /// </summary>
    public static IServiceCollection AddCaseFramework(this IServiceCollection services)
    {
        services.AddSingleton<ICaseRegistry>(sp =>
            new CaseRegistry(sp.GetServices<ICaseType>()));
        services.AddSingleton<ICaseService, CaseService>();
        services.AddSingleton<ICaseAuditAppender, CaseAuditAppender>();
        services.AddHostedService<CaseMigrationHostedService>();
        return services;
    }

    /// <summary>
    /// Register a single case module: its type, its services, and itself
    /// as an <see cref="ICaseModule"/> so its endpoints can be mapped later.
    /// </summary>
    public static IServiceCollection AddCaseModule<TModule>(
        this IServiceCollection services,
        IConfiguration configuration)
        where TModule : class, ICaseModule, new()
    {
        var module = new TModule();
        services.AddSingleton<ICaseModule>(module);
        services.AddSingleton(module.Type);
        module.RegisterServices(services, configuration);
        return services;
    }

    /// <summary>
    /// Map every registered module's endpoints onto the application. Call once
    /// during endpoint configuration.
    /// </summary>
    public static IEndpointRouteBuilder MapCaseModules(this IEndpointRouteBuilder endpoints)
    {
        var modules = endpoints.ServiceProvider.GetServices<ICaseModule>();
        foreach (var module in modules)
        {
            module.MapEndpoints(endpoints);
        }
        return endpoints;
    }
}