namespace CaddyUi.Infrastructure.Persistence;

public sealed class SchemaMarker
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}
