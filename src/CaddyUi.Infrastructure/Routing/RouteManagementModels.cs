using CaddyUi.Domain.Routing;

namespace CaddyUi.Infrastructure.Routing;

public sealed record ManagementActor(
    Guid? UserId,
    string Username,
    string RemoteAddress)
{
    public static ManagementActor System { get; } = new(null, "system", string.Empty);
}

public sealed record ManagedDomainOption(
    Guid Id,
    string Name,
    string DisplayName,
    bool Enabled,
    bool IsDefault);

public sealed record ManagedRouteRecord(
    ManagedRouteDefinition Definition,
    string DomainDisplayName,
    string AccessGroupName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AccessGroupRecord(
    Guid Id,
    string Name,
    string Description,
    string AccentColor,
    string IconUrl,
    bool Enabled,
    int CredentialCount,
    int RouteCount,
    DateTimeOffset UpdatedAt);

public sealed record AccessCredentialRecord(
    Guid Id,
    Guid GroupId,
    string Username,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record RouteRevisionRecord(
    Guid Id,
    DateTimeOffset CreatedAt,
    Guid? ActorUserId,
    string ActorUsername,
    string Reason,
    string ManifestJson,
    string Content,
    string Digest,
    bool Applied);

public sealed record ApplyOperationRecord(
    Guid Id,
    Guid? RouteRevisionId,
    string CorrelationId,
    string State,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string Error,
    Guid? PreviousSnapshotId);

public sealed record CaddySnapshotRecord(
    Guid Id,
    DateTimeOffset CreatedAt,
    string Digest,
    string ManifestJson,
    string Content,
    string Reason);
