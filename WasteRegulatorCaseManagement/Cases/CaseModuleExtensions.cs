namespace WasteRegulatorCaseManagement.Cases;

public static class CaseModuleExtensions
{
    public static IServiceCollection AddCaseFramework(this IServiceCollection services)
    {
        services.AddSingleton(new CaseRegistry([]));
        services.AddSingleton<ICaseModuleConfigStore, CaseModuleConfigPersistence>();
        services.AddHostedService<CaseModuleConfigLoader>();
        services.AddSingleton<ICaseService, CaseService>();
        services.AddSingleton<ICaseTimelineAppender, CaseTimelineAppender>();
        services.AddHostedService<CaseMigrationHostedService>();
        return services;
    }
}
