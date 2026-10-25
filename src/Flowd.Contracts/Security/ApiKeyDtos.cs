namespace Flowd.Contracts.Security;

/// <summary>Request to create an API key. Roles come from admin, operator, developer, worker, reader. A tenant admin always creates keys for its own tenant.</summary>
public sealed record CreateApiKeyRequestDto(string Name, IReadOnlyList<string> Roles, string? Tenant, IReadOnlyList<string>? Processes);

/// <summary>The only response that carries the plain key: it cannot be shown again.</summary>
public sealed record CreatedApiKeyDto(string Id, string Key, string Name, IReadOnlyList<string> Roles, string Tenant, IReadOnlyList<string> Processes);

public sealed record ApiKeyDto(string Id, string Name, IReadOnlyList<string> Roles, string Tenant, IReadOnlyList<string> Processes, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt);
