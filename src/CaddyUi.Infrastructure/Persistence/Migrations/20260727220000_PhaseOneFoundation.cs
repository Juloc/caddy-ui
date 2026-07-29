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
        migrationBuilder.Sql(
            """
            CREATE SCHEMA IF NOT EXISTS caddy_ui;

            CREATE TABLE IF NOT EXISTS caddy_ui.schema_markers (
                id uuid NOT NULL,
                name character varying(128) NOT NULL,
                created_at timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
                CONSTRAINT pk_schema_markers PRIMARY KEY (id)
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ix_schema_markers_name
                ON caddy_ui.schema_markers (name);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "schema_markers",
            schema: "caddy_ui");
    }
}
