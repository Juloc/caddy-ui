using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaddyUi.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CaddyUiDbContext))]
[Migration("20260801190000_AccessPortalPresentationAndLoginScope")]
public sealed class AccessPortalPresentationAndLoginScope : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER TABLE caddy_ui.login_attempts
                ALTER COLUMN scope TYPE text;
            ALTER TABLE caddy_ui.login_blocks
                ALTER COLUMN scope TYPE text;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DELETE FROM caddy_ui.login_attempts WHERE length(scope) > 24;
            DELETE FROM caddy_ui.login_blocks WHERE length(scope) > 24;
            ALTER TABLE caddy_ui.login_attempts
                ALTER COLUMN scope TYPE character varying(24);
            ALTER TABLE caddy_ui.login_blocks
                ALTER COLUMN scope TYPE character varying(24);
            """);
    }
}
