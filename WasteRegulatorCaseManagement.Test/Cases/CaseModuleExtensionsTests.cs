using WasteRegulatorCaseManagement.Cases;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace WasteRegulatorCaseManagement.Test.Cases;

public class CaseModuleExtensionsTests
{
    // AddCaseModule now threads IConfiguration into RegisterServices
    // (RA-422). These tests don't exercise any config-gated registration, so
    // an empty configuration suffices.
    private static readonly IConfiguration s_config = new ConfigurationBuilder().Build();

    [Fact]
    public void AddCaseModule_registers_module_type_and_calls_register_services()
    {
        var services = new ServiceCollection();

        services.AddCaseFramework();
        services.AddCaseModule<RecordingModule>(s_config);

        var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<ICaseRegistry>();
        Assert.Single(registry.Types);
        Assert.Equal("recording", registry.Find("recording")?.CaseType);

        // Module's RegisterServices should have run, registering the marker.
        Assert.NotNull(provider.GetService<RecordingModule.Marker>());
    }

    [Fact]
    public void AddCaseModule_exposes_module_via_ICaseModule()
    {
        var services = new ServiceCollection();
        services.AddCaseFramework();
        services.AddCaseModule<RecordingModule>(s_config);
        services.AddCaseModule<EndpointModule>(s_config);

        var provider = services.BuildServiceProvider();

        var modules = provider.GetServices<ICaseModule>().ToList();
        Assert.Equal(2, modules.Count);
        Assert.Contains(modules, m => m.Type.CaseType == "recording");
        Assert.Contains(modules, m => m.Type.CaseType == "endpoint");
    }

    [Fact]
    public void MapCaseModules_invokes_MapEndpoints_for_every_module()
    {
        var services = new ServiceCollection();
        services.AddRouting();
        services.AddCaseFramework();
        services.AddCaseModule<EndpointModule>(s_config);
        services.AddCaseModule<RecordingModule>(s_config);

        var provider = services.BuildServiceProvider();
        var endpoints = new TestEndpointRouteBuilder(provider);

        endpoints.MapCaseModules();

        var mapped = endpoints.DataSources
            .SelectMany(s => s.Endpoints)
            .Select(e => e.DisplayName)
            .ToList();

        Assert.Contains(mapped, name => name is not null && name.Contains("/cases/endpoint/ping"));
    }

    private sealed class TestEndpointRouteBuilder(IServiceProvider services) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = services;
        public ICollection<EndpointDataSource> DataSources { get; } = [];
        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }

    private sealed class RecordingModule : ICaseModule
    {
        public ICaseType Type { get; } = new TestCaseType("recording", "Recording");

        public void RegisterServices(IServiceCollection services, IConfiguration configuration) => services.AddSingleton<Marker>();

        public void MapEndpoints(IEndpointRouteBuilder endpoints)
        {
        }

        public sealed class Marker;
    }

    private sealed class EndpointModule : ICaseModule
    {
        public ICaseType Type { get; } = new TestCaseType("endpoint", "Endpoint");

        public void RegisterServices(IServiceCollection services, IConfiguration configuration)
        {
        }

        public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
            endpoints.MapGet("/cases/endpoint/ping", () => "pong");
    }
}