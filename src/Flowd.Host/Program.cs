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

    Microsoft.AspNetCore.Builder.WebApplication app;
    try
    {
        app = FlowdHostBuilder.Build(args);
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("Node id", StringComparison.Ordinal))
    {
        await Console.Error.WriteLineAsync(ex.Message.AsMemory(), cancellationToken).ConfigureAwait(false);
        return 3; // a clear message and a failing exit code, not a stack trace
    }

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
var userName = new Option<string>("--username") { Description = "Login name of the user.", Required = true };
var userRoles = new Option<string>("--user-roles") { Description = "Comma separated roles: admin, operator, developer, worker, reader.", Required = true };
var userTenant = new Option<string>("--user-tenant") { Description = "Tenant of the user (empty for single-tenant mode).", DefaultValueFactory = _ => string.Empty };
var user = new Command("user", "Manage built-in users.");
var createUser = new Command("create", "Create a user. The password is read from the environment variable FLOWD_USER_PASSWORD (at least 12 characters), never from the command line.") { userName, userRoles, userTenant };
createUser.SetAction(async (parseResult, cancellationToken) =>
{
    var roles = parseResult.GetRequiredValue(userRoles).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var password = Environment.GetEnvironmentVariable("FLOWD_USER_PASSWORD") ?? string.Empty;
    if (password.Length < Flowd.Application.Security.PasswordHasher.MinimumLength || roles.Length == 0 || roles.Any(r => !Flowd.Application.Security.Roles.All.Contains(r, StringComparer.Ordinal)))
    {
        await Console.Error.WriteLineAsync($"Set FLOWD_USER_PASSWORD (at least {Flowd.Application.Security.PasswordHasher.MinimumLength} characters) and give valid roles.".AsMemory(), cancellationToken).ConfigureAwait(false);
        return 2;
    }

    var options = FlowdHostBuilder.LoadOptions(Environment.GetCommandLineArgs().Skip(3).ToArray());
    var created = await FlowdHostBuilder.CreateUserAsync(options, new NewUser(parseResult.GetRequiredValue(userName), password, roles, parseResult.GetValue(userTenant) ?? string.Empty), cancellationToken).ConfigureAwait(false);
    await Console.Out.WriteLineAsync((created ? "User created." : "That username already exists.").AsMemory(), cancellationToken).ConfigureAwait(false);
    return created ? 0 : 1;
});
user.Add(createUser);

var urlOption = new Option<string>("--url") { Description = "Address of the running Flowd (default: environment variable FLOWD_URL, else http://localhost:8080).", DefaultValueFactory = _ => Environment.GetEnvironmentVariable("FLOWD_URL") ?? "http://localhost:8080" };
var filesArgument = new Argument<FileInfo[]>("files") { Description = "BPMN, DMN or form files to deploy.", Arity = ArgumentArity.OneOrMore };
var deploy = new Command("deploy", "Deploy .bpmn, .dmn and .form files to a running Flowd. The API key or token is read from the environment variable FLOWD_API_KEY.") { filesArgument, urlOption };
deploy.SetAction(async (parseResult, cancellationToken) =>
{
    using var client = RemoteCommands.CreateClient(new Uri(parseResult.GetRequiredValue(urlOption)), Environment.GetEnvironmentVariable("FLOWD_API_KEY"));
    return await RemoteCommands.DeployAsync(client, parseResult.GetRequiredValue(filesArgument), Console.Out, cancellationToken).ConfigureAwait(false);
});

var processArgument = new Argument<string>("process") { Description = "The bpmnProcessId to start." };
var variablesOption = new Option<string>("--variables") { Description = "Variables as a JSON object, for example '{\"orderId\":42}'." };
var start = new Command("start", "Start an instance of a deployed process. The API key or token is read from FLOWD_API_KEY.") { processArgument, variablesOption, urlOption };
start.SetAction(async (parseResult, cancellationToken) =>
{
    using var client = RemoteCommands.CreateClient(new Uri(parseResult.GetRequiredValue(urlOption)), Environment.GetEnvironmentVariable("FLOWD_API_KEY"));
    return await RemoteCommands.StartAsync(client, parseResult.GetRequiredValue(processArgument), parseResult.GetValue(variablesOption), Console.Out, cancellationToken).ConfigureAwait(false);
});

return await new RootCommand("flowd - BPMN workflow engine") { serve, migrate, validate, apiKey, user, deploy, start }
    .Parse(args)
    .InvokeAsync()
    .ConfigureAwait(false);
