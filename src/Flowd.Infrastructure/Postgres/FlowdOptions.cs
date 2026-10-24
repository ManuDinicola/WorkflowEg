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

    /// <summary>Webhook endpoint that receives lifecycle events (FR-HIS-002). Empty disables delivery.</summary>
    public Uri? WebhookUrl { get; init; }

    /// <summary>Shared secret used to sign webhook bodies with HMAC-SHA256.</summary>
    public string? WebhookSecret { get; init; }

    /// <summary>Writes outbox rows even without a webhook (for another publisher).</summary>
    public bool OutboxEnabled { get; init; }

    public bool OutboxActive => OutboxEnabled || WebhookUrl is not null;
}
