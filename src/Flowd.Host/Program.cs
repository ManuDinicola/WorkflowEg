using System.CommandLine;
using Flowd.Bpmn.Model;
using Flowd.Bpmn.Reading;
using Flowd.Host;

var autoMigrate = new Option<bool>("--auto-migrate") { Description = "Apply pending migrations before serving." };
var fileArgument = new Argument<FileInfo>("file") { Description = "BPMN file to validate." };

var serve = new Command("serve", "Run the API, engine and schedulers.") { autoMigrate };
serve.SetAction(async (parseResult, cancellationToken) =>
{
    var args = Environment.GetCommandLineArgs().Skip(1).Where(a => a is not ("serve" or "--auto-migrate")).ToArray();
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

return await new RootCommand("flowd - BPMN workflow engine") { serve, migrate, validate }
    .Parse(args)
    .InvokeAsync()
    .ConfigureAwait(false);
