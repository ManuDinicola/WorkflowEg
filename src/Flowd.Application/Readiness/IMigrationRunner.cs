namespace Flowd.Application.Readiness;

/// <summary>Port: forward-only schema migrations (FR-DB-001).</summary>
public interface IMigrationRunner
{
    /// <summary>Applies all pending migrations. Returns the names of the scripts that were applied.</summary>
    IReadOnlyList<string> Migrate();

    /// <summary>True when no migration is pending.</summary>
    bool IsCurrent();
}
