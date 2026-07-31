namespace CaddyUi.Infrastructure.Security;

public sealed record UserAccount(
    Guid Id,
    string Username,
    string DisplayName,
    string PasswordHash,
    string Role,
    bool Enabled,
    byte[]? TotpSecretEncrypted,
    bool TotpEnabled,
    string Theme);

public sealed record ValidatedAdminSession(
    UserAccount User,
    string TokenHash,
    DateTimeOffset ExpiresAt);

public sealed record ActiveLoginBlock(
    DateTimeOffset ExpiresAt,
    string Reason);

public sealed record PortalAccessGroup(
    Guid Id,
    string Name,
    string ConfigJson);

public sealed record PortalCredential(
    Guid Id,
    Guid GroupId,
    string Username,
    string PasswordHash,
    bool Enabled);

public sealed record ValidatedPortalSession(
    string Username,
    string TokenHash,
    DateTimeOffset ExpiresAt);
