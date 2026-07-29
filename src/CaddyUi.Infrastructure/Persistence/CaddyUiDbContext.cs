using Microsoft.EntityFrameworkCore;

namespace CaddyUi.Infrastructure.Persistence;

public sealed class CaddyUiDbContext : DbContext
{
    public CaddyUiDbContext(DbContextOptions<CaddyUiDbContext> options)
        : base(options)
    {
    }

    public DbSet<SchemaMarker> SchemaMarkers => Set<SchemaMarker>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema("caddy_ui");

        modelBuilder.Entity<SchemaMarker>(entity =>
        {
            entity.ToTable("schema_markers");
            entity.HasKey(marker => marker.Id)
                .HasName("pk_schema_markers");
            entity.Property(marker => marker.Id)
                .HasColumnName("id");
            entity.Property(marker => marker.Name)
                .HasColumnName("name")
                .HasMaxLength(128)
                .IsRequired();
            entity.Property(marker => marker.CreatedAt)
                .HasColumnName("created_at")
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .IsRequired();
            entity.HasIndex(marker => marker.Name)
                .HasDatabaseName("ix_schema_markers_name")
                .IsUnique();
        });
    }
}
