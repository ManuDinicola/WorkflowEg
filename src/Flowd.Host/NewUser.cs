namespace Flowd.Host;

/// <summary>A user to create from the command line.</summary>
internal sealed record NewUser(string Username, string Password, IReadOnlyList<string> Roles, string Tenant);
