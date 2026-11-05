namespace WasteRegulatorCaseManagement.Cases;

public static class CaseModuleExtensions
{
    public static IServiceCollection AddCaseFramework(this IServiceCollection services)
    {
        services.AddSingleton(new CaseRegistry([]));
        services.AddSingleton<ICaseModuleConfigStore, CaseModuleConfigPersistence>();
        services.AddHostedService<CaseModuleConfigLoader>();
        services.AddSingleton<ICaseService, CaseService>();
        // The source systems are CDP services, reached directly rather than
        // through the outbound proxy that HttpClient.DefaultProxy points at.
        services
            .AddHttpClient(CaseCallbackClient.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseProxy = false })
            .AddHeaderPropagation();
        services.AddSingleton<ICaseCallbackClient, CaseCallbackClient>();
        services.AddSingleton<ICaseTimelineAppender, CaseTimelineAppender>();
        services.AddHostedService<CaseMigrationHostedService>();
        return services;
    }
}
