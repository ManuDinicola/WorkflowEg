using System.CommandLine;
using Flowd.Bpmn.Model;
using Flowd.Bpmn.Reading;
using Flowd.Host;

var autoMigrate = new Option<bool>("--auto-migrate") { Description = "Apply pending migrations before serving." };
var insecure = new Option<bool>("--insecure-no-auth") { Description = "DEVELOPMENT ONLY: disable authentication and authorization." };
var keyName = new Option<string>("--name") { Description = "Name of the API key.", Required = true };
var keyRoles = new Option<string>("--roles") { Description = "Comma separated roles: admin, operator, developer, worker, reader.", Required = true };
var keyTenant = new Option<string>("--tenant") { Description = "Tenant of the key (empty for single-tenant mode).", DefaultValueFactory = _ => string.Empty };
var keyProcesses = new Option<string>("--processes") { Description = "Comma separated process ids the key is limited to (default: all).", DefaultValueFactory = _ => string.Empty };
var fileArgument = new Argument<FileInfo>("file") { Description = "BPMN file to validate." };

var serve = new Command("serve", "Run the API, engine and schedulers.") { autoMigrate, insecure };
serve.SetAction(async (parseResult, cancellationToken) =>
{
    var args = Environment.GetCommandLineArgs().Skip(1).Where(a => a is not ("serve" or "--auto-migrate" or "--insecure-no-auth")).ToArray();
    if (parseResult.GetValue(insecure))
    {
        args = [.. args, "--Flowd:Auth:Insecure=true"];
    }

    var runner = FlowdHostBuilder.CreateMigrationRunner(FlowdHostBuilder.LoadOptions(args));
    if (parseResult.GetValue(autoMigrate))
    {
        runner.Migrate();
    }
    else if (!runner.IsCurrent())
    {
        await Console.Error.WriteLineAsync("Pending migrations. Run 'flowd migrate' or start with --auto-migrate.".AsMemory(), cancellationToken).ConfigureAwait(false);
        return 1;
    }

    var app = FlowdHostBuilder.Build(args);
    await app.RunAsync(cancellationToken).ConfigureAwait(false);
    return 0;
});

var migrate = new Command("migrate", "Apply pending database migrations.");
migrate.SetAction(async _ =>
{
    var args = Environment.GetCommandLineArgs().Skip(2).ToArray();
    var applied = FlowdHostBuilder.CreateMigrationRunner(FlowdHostBuilder.LoadOptions(args)).Migrate();
    await Console.Out.WriteLineAsync($"Applied {applied.Count} migration(s).").ConfigureAwait(false);
    return 0;
});

var validate = new Command("validate", "Validate a BPMN file without deploying it.") { fileArgument };
validate.SetAction(async (parseResult, cancellationToken) =>
{
    var file = parseResult.GetRequiredValue(fileArgument);
    if (!file.Exists)
    {
        await Console.Error.WriteLineAsync($"File not found: {file.FullName}".AsMemory(), cancellationToken).ConfigureAwait(false);
        return 2;
    }

    await using var stream = file.OpenRead();
    IReadOnlyList<BpmnIssue> issues = BpmnReader.Read(stream) switch
    {
        BpmnInvalid invalid => invalid.Issues,
        BpmnDefinitions => [],
    };
    foreach (var issue in issues)
    {
        await Console.Error.WriteLineAsync($"{issue.Code} {issue.ElementId}: {issue.Message}".AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    if (issues.Count > 0)
    {
        return 1;
    }

    await Console.Out.WriteLineAsync($"{file.Name}: valid".AsMemory(), cancellationToken).ConfigureAwait(false);
    return 0;
});

var apiKey = new Command("apikey", "Manage API keys.");
var createKey = new Command("create", "Create an API key; the key is printed once and only its hash is stored.") { keyName, keyRoles, keyTenant, keyProcesses };
createKey.SetAction(async (parseResult, cancellationToken) =>
{
    var roles = parseResult.GetRequiredValue(keyRoles).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var unknown = roles.Where(r => !Flowd.Application.Security.Roles.All.Contains(r, StringComparer.Ordinal)).ToList();
    if (unknown.Count > 0)
    {
        await Console.Error.WriteLineAsync($"Unknown role(s): {string.Join(", ", unknown)}".AsMemory(), cancellationToken).ConfigureAwait(false);
        return 2;
    }

    var options = FlowdHostBuilder.LoadOptions(Environment.GetCommandLineArgs().Skip(3).ToArray());
    var key = await FlowdHostBuilder.CreateApiKeyAsync(options, parseResult.GetRequiredValue(keyName), roles, parseResult.GetValue(keyTenant) ?? string.Empty, (parseResult.GetValue(keyProcesses) ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), cancellationToken).ConfigureAwait(false);
    await Console.Out.WriteLineAsync(key.AsMemory(), cancellationToken).ConfigureAwait(false);
    return 0;
});
apiKey.Add(createKey);

return await new RootCommand("flowd - BPMN workflow engine") { serve, migrate, validate, apiKey }
    .Parse(args)
    .InvokeAsync()
    .ConfigureAwait(false);
