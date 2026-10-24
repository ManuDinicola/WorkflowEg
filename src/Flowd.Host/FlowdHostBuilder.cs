using Flowd.Api.Endpoints;
using Flowd.Application.Deployment;
using Flowd.Application.Readiness;
using Flowd.Engine.Keys;
using Flowd.Infrastructure.Keys;
using Flowd.Infrastructure.Postgres;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Flowd.Host;

/// <summary>Composition root: the only place that wires ports to adapters (CC-006, CC-017).</summary>
internal static class FlowdHostBuilder
{
    public static FlowdOptions LoadOptions(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .AddCommandLine(args)
            .Build();
        var options = new FlowdOptions();
        configuration.GetSection(FlowdOptions.SectionName).Bind(options);
        return options;
    }

    public static IMigrationRunner CreateMigrationRunner(FlowdOptions options) =>
        new PostgresMigrationRunner(options.ConnectionString);

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Host.UseWindowsService(); // no-op unless started by the Windows Service Control Manager
        builder.Services
            .AddOptions<FlowdOptions>()
            .BindConfiguration(FlowdOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        AddInfrastructure(builder.Services);
        builder.Services.AddSingleton<DeployProcessesUseCase>();

        var app = builder.Build();
        app.MapOperationalEndpoints();
        app.MapDeploymentEndpoints();
        return app;
    }

    private static void AddInfrastructure(IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp => NpgsqlDataSource.Create(sp.GetRequiredService<IOptions<FlowdOptions>>().Value.ConnectionString));
        services.AddSingleton<IKeyGenerator>(sp =>
            new SnowflakeKeyGenerator(sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<IOptions<FlowdOptions>>().Value.NodeId));
        services.AddSingleton<IProcessDefinitionStore, PostgresProcessDefinitionStore>();
        services.AddSingleton<IMigrationRunner>(sp =>
            new PostgresMigrationRunner(sp.GetRequiredService<IOptions<FlowdOptions>>().Value.ConnectionString));
        services.AddSingleton<IReadinessProbe>(sp =>
            new PostgresReadinessProbe(
                sp.GetRequiredService<IOptions<FlowdOptions>>().Value.ConnectionString,
                sp.GetRequiredService<IMigrationRunner>()));
    }
}
