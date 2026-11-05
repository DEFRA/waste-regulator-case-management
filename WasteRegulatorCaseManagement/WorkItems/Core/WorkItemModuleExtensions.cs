using WasteRegulatorCaseManagement.Config;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WasteRegulatorCaseManagement.WorkItems.Core;

/// <summary>
/// DI and routing wiring for the work item framework. Adding a new work item
/// type to the application means:
/// <list type="number">
///   <item>Create a folder under <c>WorkItems/&lt;TypeName&gt;</c></item>
///   <item>Implement <see cref="IWorkItemType"/> and <see cref="IWorkItemModule"/></item>
///   <item>Call <c>services.AddWorkItemModule&lt;YourModule&gt;()</c> in <c>Program.cs</c></item>
/// </list>
/// No changes are required to other modules or to core code.
/// </summary>
public static class WorkItemModuleExtensions
{
    /// <summary>
    /// Register the framework itself. Call once before adding modules.
    /// </summary>
    public static IServiceCollection AddWorkItemFramework(this IServiceCollection services)
    {
        services.AddSingleton<IWorkItemRegistry>(sp =>
            new WorkItemRegistry(sp.GetServices<IWorkItemType>()));
        services.AddSingleton<IWorkItemService, WorkItemService>();
        services.AddSingleton<IWorkItemAuditAppender, WorkItemAuditAppender>();
        services.AddHostedService<WorkItemMigrationHostedService>();
        return services;
    }

    /// <summary>
    /// Conditionally register the startup seeder,
    /// <see cref="WorkItemSeederHostedService"/>. It writes demo records
    /// that reference stub user identifiers (e.g. <c>stub-standard-1</c>,
    /// <c>stub-assign-1</c>, <c>stub-portal-client</c>), so it must NEVER run
    /// outside a developer environment: it needs both
    /// <see cref="IHostEnvironment.IsDevelopment"/> and
    /// <c>WorkItems:SeedOnStartup</c>. If the flag is observed in any other
    /// environment a one-shot warning hosted service is registered instead, so
    /// the misconfiguration is visible in startup logs without ever touching
    /// the database.
    /// </summary>
    public static IServiceCollection AddWorkItemSeeders(
        this IServiceCollection services,
        IHostEnvironment environment,
        IConfiguration configuration)
    {
        var seedFlag = configuration.GetValue("WorkItems:SeedOnStartup", false);
        var seedDemoData = seedFlag && environment.IsDevelopment();

        if (seedFlag && !seedDemoData)
        {
            services.AddHostedService<WorkItemSeederMisconfigurationWarningHostedService>();
        }

        if (seedDemoData)
        {
            services.AddHostedService<WorkItemSeederHostedService>();
        }

        return services;
    }

    /// <summary>
    /// Register a single work item module: its type, its services, and itself
    /// as an <see cref="IWorkItemModule"/> so its endpoints can be mapped later.
    /// </summary>
    public static IServiceCollection AddWorkItemModule<TModule>(
        this IServiceCollection services,
        IConfiguration configuration)
        where TModule : class, IWorkItemModule, new()
    {
        var module = new TModule();
        services.AddSingleton<IWorkItemModule>(module);
        services.AddSingleton(module.Type);
        module.RegisterServices(services, configuration);
        return services;
    }

    /// <summary>
    /// Map every registered module's endpoints onto the application. Call once
    /// during endpoint configuration.
    /// </summary>
    public static IEndpointRouteBuilder MapWorkItemModules(this IEndpointRouteBuilder endpoints)
    {
        var modules = endpoints.ServiceProvider.GetServices<IWorkItemModule>();
        foreach (var module in modules)
        {
            module.MapEndpoints(endpoints);
        }
        return endpoints;
    }
}