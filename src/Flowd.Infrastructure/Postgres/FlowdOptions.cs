using System.ComponentModel.DataAnnotations;

namespace Flowd.Infrastructure.Postgres;

/// <summary>Strongly typed settings, validated at startup (CC-017). Bound from the <c>Flowd</c> configuration section.</summary>
public sealed class FlowdOptions
{
    public const string SectionName = "Flowd";

    /// <summary>Npgsql connection string for the PostgreSQL database.</summary>
    [Required]
    public string ConnectionString { get; init; } = string.Empty;
}
