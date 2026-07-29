using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CaddyUi.Infrastructure.Persistence;

public sealed class CaddyUiDbContext : DbContext, IDataProtectionKeyContext
{
    public CaddyUiDbContext(DbContextOptions<CaddyUiDbContext> options)
        : base(options)
    {
    }

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public DbSet<SchemaMarker> SchemaMarkers => Set<SchemaMarker>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema("caddy_ui");

        modelBuilder.Entity<DataProtectionKey>(entity =>
        {
            entity.ToTable("data_protection_keys");
            entity.HasKey(key => key.Id)
                .HasName("pk_data_protection_keys");
            entity.Property(key => key.Id)
                .HasColumnName("id");
            entity.Property(key => key.FriendlyName)
                .HasColumnName("friendly_name");
            entity.Property(key => key.Xml)
                .HasColumnName("xml");
        });

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
