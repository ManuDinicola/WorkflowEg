using Flowd.Api.Endpoints;
using Flowd.Application.Readiness;
using Flowd.Infrastructure.Postgres;
using Microsoft.Extensions.Options;

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
        builder.Services.AddSingleton<IMigrationRunner>(sp =>
            new PostgresMigrationRunner(sp.GetRequiredService<IOptions<FlowdOptions>>().Value.ConnectionString));
        builder.Services.AddSingleton<IReadinessProbe>(sp =>
            new PostgresReadinessProbe(
                sp.GetRequiredService<IOptions<FlowdOptions>>().Value.ConnectionString,
                sp.GetRequiredService<IMigrationRunner>()));

        var app = builder.Build();
        app.MapOperationalEndpoints();
        return app;
    }
}
