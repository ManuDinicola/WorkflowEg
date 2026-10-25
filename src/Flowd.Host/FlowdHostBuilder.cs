using System.Threading.RateLimiting;
using Flowd.Api.Endpoints;
using Flowd.Api.Security;
using Flowd.Application.Audit;
using Flowd.Application.Batch;
using Flowd.Application.Connectors;
using Flowd.Application.Decisions;
using Flowd.Application.Deployment;
using Flowd.Application.Execution;
using Flowd.Application.Forms;
using Flowd.Application.Jobs;
using Flowd.Application.Messaging;
using Flowd.Application.Migration;
using Flowd.Application.Observability;
using Flowd.Application.Queries;
using Flowd.Application.Readiness;
using Flowd.Application.Retention;
using Flowd.Application.Security;
using Flowd.Application.Timers;
using Flowd.Application.UserTasks;
using Flowd.Engine.Execution;
using Flowd.Engine.Handlers;
using Flowd.Engine.Keys;
using Flowd.Infrastructure.Background;
using Flowd.Infrastructure.Connectors;
using Flowd.Infrastructure.Keys;
using Flowd.Infrastructure.Postgres;
using Flowd.Infrastructure.Postgres.Execution;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using OpenTelemetry.Trace;

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

    public static async Task<string> CreateApiKeyAsync(FlowdOptions options, string name, IReadOnlyList<string> roles, string tenant, IReadOnlyList<string> processes, CancellationToken cancellationToken)
    {
        await using var dataSource = NpgsqlDataSource.Create(options.ConnectionString);
        var key = ApiKeyHasher.NewKey();
        await new PostgresApiKeyStore(dataSource).CreateAsync(ApiKeyHasher.Hash(key), new ApiKeyInfo(name, roles, tenant, processes), DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        return key;
    }

    public static IMigrationRunner CreateMigrationRunner(FlowdOptions options) =>
        new PostgresMigrationRunner(options.ConnectionString);

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Host.UseWindowsService(); // no-op unless started by the Windows Service Control Manager
        ConfigureObservability(builder);
        builder.Services
            .AddOptions<FlowdOptions>()
            .BindConfiguration(FlowdOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        AddInfrastructure(builder.Services);
        AddApplication(builder.Services);
        AddSecurity(builder.Services, builder.Configuration);
        AddLimits(builder);
        builder.Services.AddOpenApi();

        var app = builder.Build();
        _ = app.Services.GetRequiredService<PrometheusMetrics>(); // start listening before the first measurement
        app.UseProblemStatusCodes();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.UseMiddleware<ProcessScopeMiddleware>();
        app.UseMiddleware<AuditMiddleware>();
        app.UseMiddleware<IdempotencyMiddleware>();
        app.MapOpenApi().AllowAnonymous();
        app.MapOperationalEndpoints();
        app.MapUi();
        app.MapDeploymentEndpoints();
        app.MapDecisionEndpoints();
        app.MapJobStreamEndpoints();
        app.MapWebhookEndpoints();
        app.MapAnalyticsEndpoints();
        app.MapJobSearchEndpoints();
        app.MapFormEndpoints();
        app.MapBatchEndpoints();
        app.MapAuditAndEventEndpoints();
        app.MapInstanceEndpoints();
        app.MapJobEndpoints();
        app.MapMessageEndpoints();
        app.MapMetricsEndpoint();
        app.MapSignalAndUserTaskEndpoints();
        return app;
    }

    /// <summary>JSON logs with scopes (FR-OBS-003), the <c>/metrics</c> exposition and optional OTLP trace export (FR-OBS-002).</summary>
    private static void ConfigureObservability(WebApplicationBuilder builder)
    {
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.UseUtcTimestamp = true;
            options.TimestampFormat = "O";
        });
        builder.Services.AddSingleton<PrometheusMetrics>();
        builder.Services.AddOpenTelemetry().WithTracing(tracing =>
        {
            tracing.AddSource(EngineTelemetry.Name);
            if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            {
                tracing.AddOtlpExporter();
            }
        });
    }

    private static void AddLimits(WebApplicationBuilder builder)
    {
        const int MaxBodyBytes = 4 * 1024 * 1024; // FR-SEC-004
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = MaxBodyBytes);
        builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = MaxBodyBytes);
        var limit = builder.Configuration.GetSection(RateLimitOptions.SectionName).Get<RateLimitOptions>() ?? new RateLimitOptions();
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "1";
                return ValueTask.CompletedTask;
            };
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var exempt = limit.TokensPerSecond <= 0 || IsOperational(context.Request.Path);
                var caller = context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
                return exempt
                    ? RateLimitPartition.GetNoLimiter("exempt")
                    : RateLimitPartition.GetTokenBucketLimiter(caller, _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = Math.Max(limit.Burst, 1),
                        TokensPerPeriod = limit.TokensPerSecond,
                        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    });
            });
        });
    }

    private static bool IsOperational(PathString path) =>
        path.StartsWithSegments("/healthz", StringComparison.Ordinal) || path.StartsWithSegments("/readyz", StringComparison.Ordinal) || path.StartsWithSegments("/metrics", StringComparison.Ordinal);

    private static void AddSecurity(IServiceCollection services, ConfigurationManager configuration)
    {
        services.AddOptions<AuthOptions>().BindConfiguration(AuthOptions.SectionName);
        var auth = configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
        var jwtConfigured = !string.IsNullOrWhiteSpace(auth.JwtAuthority) || !string.IsNullOrWhiteSpace(auth.JwtSigningKey);
        var authentication = services
            .AddAuthentication(options =>
            {
                options.DefaultScheme = "Flowd";
                options.DefaultChallengeScheme = "Flowd";
            })
            .AddPolicyScheme("Flowd", "Flowd", options =>
                options.ForwardDefaultSelector = context =>
                    jwtConfigured && !context.Request.Headers.ContainsKey(ApiKeyAuthenticationHandler.HeaderName) ? JwtBearerDefaults.AuthenticationScheme : ApiKeyAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, _ => { });
        if (jwtConfigured)
        {
            authentication.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.Authority = auth.JwtAuthority;
                options.Audience = auth.JwtAudience;
                options.MapInboundClaims = false;
                options.TokenValidationParameters.ValidateAudience = auth.JwtAudience is not null;
                if (!string.IsNullOrWhiteSpace(auth.JwtSigningKey))
                {
                    options.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(auth.JwtSigningKey));
                    options.TokenValidationParameters.ValidateIssuer = true;
                    options.TokenValidationParameters.ValidIssuer = auth.JwtIssuer ?? throw new InvalidOperationException("Flowd:Auth:JwtIssuer is required together with JwtSigningKey.");
                }
            });
        }

        services.AddSingleton<IClaimsTransformation, FlowdClaimsTransformation>();
        services.AddAuthorizationPolicies(auth.Insecure);
    }

    private static void AddInfrastructure(IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp => NpgsqlDataSource.Create(sp.GetRequiredService<IOptions<FlowdOptions>>().Value.ConnectionString));
        services.AddSingleton<IKeyGenerator>(sp =>
            new SnowflakeKeyGenerator(sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<IOptions<FlowdOptions>>().Value.NodeId));
        services.AddSingleton<IProcessDefinitionStore, PostgresProcessDefinitionStore>();
        services.AddSingleton<IDecisionStore, PostgresDecisionStore>();
        services.AddSingleton<IAnalyticsQueries, PostgresAnalyticsQueries>();
        services.AddSingleton<IJobQueries, PostgresJobQueries>();
        services.AddSingleton<IFormStore, PostgresFormStore>();
        services.AddSingleton<IInstanceMigrationStore, PostgresMigrationStore>();
        services.AddSingleton<IAuditLog, PostgresAuditLog>();
        services.AddSingleton<IEventFeed, PostgresEventFeed>();
        services.AddSingleton<IProcessModelProvider, ProcessModelProvider>();
        services.AddSingleton<IInstanceTransactionFactory, PostgresInstanceTransactionFactory>();
        services.AddSingleton<IInstanceLookup, PostgresInstanceLookup>();
        services.AddSingleton<IJobQueue, PostgresJobQueue>();
        services.AddSingleton<IInstanceQueries, PostgresInstanceQueries>();
        services.AddSingleton<ITimerQueue, PostgresTimerQueue>();
        services.AddSingleton<IStartTriggerStore, PostgresStartTriggerStore>();
        services.AddSingleton<IMessageStore, PostgresMessageStore>();
        services.AddSingleton<ISignalStore, PostgresSignalStore>();
        services.AddSingleton<IUserTaskQueries, PostgresUserTaskQueries>();
        services.AddSingleton<IChildInstanceStore, PostgresChildInstanceStore>();
        services.AddSingleton<IApiKeyStore, PostgresApiKeyStore>();
        services.AddSingleton<IRetentionStore, PostgresRetentionStore>();
        services.AddOptions<RetentionOptions>().BindConfiguration(RetentionOptions.SectionName);
        services.AddSingleton<Janitor>();
        services.AddHostedService(sp => sp.GetRequiredService<Janitor>());
        services.AddSingleton<IIdempotencyStore, PostgresIdempotencyStore>();
        services.AddSingleton<IMigrationRunner>(sp =>
            new PostgresMigrationRunner(sp.GetRequiredService<IOptions<FlowdOptions>>().Value.ConnectionString));
        services.AddSingleton<IReadinessProbe>(sp =>
            new PostgresReadinessProbe(
                sp.GetRequiredService<IOptions<FlowdOptions>>().Value.ConnectionString,
                sp.GetRequiredService<IMigrationRunner>()));
        services.AddHostedService<JobTimeoutSweeper>();
        services.AddSingleton<DecisionJobProcessor>();
        services.AddSingleton(sp => sp.GetRequiredService<IConfiguration>().GetSection(ConnectorOptions.SectionName).Get<ConnectorOptions>() ?? new ConnectorOptions());
        services.AddSingleton<ISecretProvider, ConfigSecretProvider>();
        services.AddSingleton<IHttpCallSender, HttpCallSender>();
        services.AddSingleton<HttpConnectorHandler>();
        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        services.AddSingleton<EmailConnectorHandler>();
        services.AddSingleton<ConnectorJobProcessor>();
        services.AddHostedService<ConnectorWorker>();
        services.AddHostedService<DecisionWorker>();
        services.AddHttpClient<OutboxPublisher>();
        services.AddHostedService(sp => sp.GetRequiredService<OutboxPublisher>());
        services.AddHostedService<TimerScheduler>();
        services.AddHostedService<MessageExpirySweeper>();
    }

    private static void AddApplication(IServiceCollection services)
    {
        services.AddSingleton(new Interpreter(ElementHandlerRegistry.CreateDefault()));
        services.AddSingleton<EngineServices>();
        services.AddSingleton<DeployProcessesUseCase>();
        services.AddSingleton<DeployDecisionsUseCase>();
        services.AddSingleton<DeployFormsUseCase>();
        services.AddSingleton<BatchService>();
        services.AddSingleton<MigrateInstanceUseCase>();
        services.AddSingleton<EvaluateDecisionUseCase>();
        services.AddSingleton<StartInstanceUseCase>();
        services.AddSingleton<CommandProcessor>();
        services.AddSingleton<InstanceCommandService>();
        services.AddSingleton<JobActivationService>();
        services.AddSingleton<TimerService>();
        services.AddSingleton<MessageService>();
        services.AddSingleton<IMessagePublisher>(sp => sp.GetRequiredService<MessageService>());
        services.AddSingleton<SignalService>();
        services.AddSingleton<ISignalBroadcaster>(sp => sp.GetRequiredService<SignalService>());
        services.AddSingleton<EngineTelemetry>();
        services.AddSingleton<PostCommitEffects>();
        services.AddSingleton<IPostCommitEffects>(sp => sp.GetRequiredService<PostCommitEffects>());
        services.AddSingleton<Func<IPostCommitEffects>>(sp => () => sp.GetRequiredService<IPostCommitEffects>());
        services.AddSingleton<UserTaskService>();
    }
}
