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
    /// Conditionally register the startup seeder,
    /// <see cref="CaseSeederHostedService"/>. It writes demo records
    /// that reference stub user identifiers (e.g. <c>stub-standard-1</c>,
    /// <c>stub-assign-1</c>, <c>stub-portal-client</c>), so it must NEVER run
    /// outside a developer environment: it needs both
    /// <see cref="IHostEnvironment.IsDevelopment"/> and
    /// <c>Cases:SeedOnStartup</c>. If the flag is observed in any other
    /// environment a one-shot warning hosted service is registered instead, so
    /// the misconfiguration is visible in startup logs without ever touching
    /// the database.
    /// </summary>
    public static IServiceCollection AddCaseSeeders(
        this IServiceCollection services,
        IHostEnvironment environment,
        IConfiguration configuration)
    {
        var seedFlag = configuration.GetValue("Cases:SeedOnStartup", false);
        var seedDemoData = seedFlag && environment.IsDevelopment();

        if (seedFlag && !seedDemoData)
        {
            services.AddHostedService<CaseSeederMisconfigurationWarningHostedService>();
        }

        if (seedDemoData)
        {
            services.AddHostedService<CaseSeederHostedService>();
        }

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