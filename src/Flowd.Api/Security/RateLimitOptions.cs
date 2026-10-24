namespace Flowd.Api.Security;

/// <summary>Token-bucket rate limit per caller (FR-SEC-004). A rate of 0 switches it off.</summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "Flowd:RateLimit";

    public int TokensPerSecond { get; init; } = 500;

    public int Burst { get; init; } = 1000;
}
