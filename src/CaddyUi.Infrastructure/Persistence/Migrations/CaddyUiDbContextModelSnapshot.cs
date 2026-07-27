using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CaddyUi.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CaddyUiDbContext))]
public partial class CaddyUiDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        modelBuilder
            .HasDefaultSchema("caddy_ui")
            .HasAnnotation("ProductVersion", "10.0.10")
            .HasAnnotation("Relational:MaxIdentifierLength", 63);

        modelBuilder.Entity("Microsoft.AspNetCore.DataProtection.EntityFrameworkCore.DataProtectionKey", entity =>
        {
            entity.Property<int>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("integer")
                .HasColumnName("id")
                .HasAnnotation(
                    "Npgsql:ValueGenerationStrategy",
                    NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            entity.Property<string>("FriendlyName")
                .HasColumnType("text")
                .HasColumnName("friendly_name");

            entity.Property<string>("Xml")
                .HasColumnType("text")
                .HasColumnName("xml");

            entity.HasKey("Id")
                .HasName("pk_data_protection_keys");

            entity.ToTable("data_protection_keys", "caddy_ui");
        });

        modelBuilder.Entity("CaddyUi.Infrastructure.Persistence.SchemaMarker", entity =>
        {
            entity.Property<Guid>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("uuid")
                .HasColumnName("id");

            entity.Property<DateTimeOffset>("CreatedAt")
                .ValueGeneratedOnAdd()
                .HasColumnType("timestamp with time zone")
                .HasColumnName("created_at")
                .HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.Property<string>("Name")
                .IsRequired()
                .HasMaxLength(128)
                .HasColumnType("character varying(128)")
                .HasColumnName("name");

            entity.HasKey("Id")
                .HasName("pk_schema_markers");

            entity.HasIndex("Name")
                .IsUnique()
                .HasDatabaseName("ix_schema_markers_name");

            entity.ToTable("schema_markers", "caddy_ui");
        });
#pragma warning restore 612, 618
    }
}
