using DbUp;
using DbUp.Engine;
using Flowd.Application.Readiness;

namespace Flowd.Infrastructure.Postgres;

/// <summary>DbUp runner over the SQL scripts embedded from <c>db/migrations</c> (FR-DB-001).</summary>
public sealed class PostgresMigrationRunner(string connectionString) : IMigrationRunner
{
    private const string ScriptPrefix = "Flowd.Migrations.";

    public IReadOnlyList<string> Migrate()
    {
        EnsureDatabase.For.PostgresqlDatabase(connectionString);
        var result = CreateEngine().PerformUpgrade();
        return result.Successful
            ? [.. result.Scripts.Select(s => s.Name)]
            : throw new InvalidOperationException("Migration failed.", result.Error);
    }

    public bool IsCurrent() => CreateEngine().GetScriptsToExecute().Count == 0;

    private UpgradeEngine CreateEngine() =>
        DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(
                typeof(PostgresMigrationRunner).Assembly,
                name => name.StartsWith(ScriptPrefix, StringComparison.Ordinal))
            .LogToNowhere()
            .Build();
}
