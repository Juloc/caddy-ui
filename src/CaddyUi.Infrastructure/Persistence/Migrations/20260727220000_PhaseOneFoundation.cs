using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaddyUi.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CaddyUiDbContext))]
[Migration("20260727220000_PhaseOneFoundation")]
public partial class PhaseOneFoundation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "caddy_ui");

        migrationBuilder.CreateTable(
            name: "schema_markers",
            schema: "caddy_ui",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                created_at = table.Column<DateTimeOffset>(
                    type: "timestamp with time zone",
                    nullable: false,
                    defaultValueSql: "CURRENT_TIMESTAMP")
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_schema_markers", value => value.id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_schema_markers_name",
            schema: "caddy_ui",
            table: "schema_markers",
            column: "name",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "schema_markers",
            schema: "caddy_ui");
    }
}
