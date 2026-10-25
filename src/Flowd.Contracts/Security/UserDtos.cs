namespace Flowd.Contracts.Security;

public sealed record LoginRequestDto(string Username, string Password);

public sealed record LoginResponseDto(string AccessToken, int ExpiresInSeconds);

/// <summary>Create a built-in user. The password needs at least 12 characters. A tenant admin always creates users of its own tenant.</summary>
public sealed record CreateUserRequestDto(string Username, string Password, IReadOnlyList<string> Roles, string? Tenant, IReadOnlyList<string>? Groups);

/// <summary>Change a user: roles, groups, disabled flag and, when given, a new password (which also clears a lockout).</summary>
public sealed record UpdateUserRequestDto(IReadOnlyList<string> Roles, IReadOnlyList<string>? Groups, bool? Disabled, string? Password);

public sealed record UserDto(string Username, IReadOnlyList<string> Roles, string Tenant, IReadOnlyList<string> Groups, DateTimeOffset CreatedAt, bool Disabled);
