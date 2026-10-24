using System.ComponentModel.DataAnnotations;

namespace Flowd.Infrastructure.Postgres;

/// <summary>Strongly typed settings, validated at startup (CC-017). Bound from the <c>Flowd</c> configuration section.</summary>
public sealed class FlowdOptions
{
    public const string SectionName = "Flowd";

    /// <summary>Npgsql connection string for the PostgreSQL database.</summary>
    [Required]
    public string ConnectionString { get; init; } = string.Empty;

    /// <summary>Unique node id (0-1023) baked into generated keys; every engine node needs a different one (FR-DOM-001).</summary>
    [Range(0, 1023)]
    public int NodeId { get; init; }
}
