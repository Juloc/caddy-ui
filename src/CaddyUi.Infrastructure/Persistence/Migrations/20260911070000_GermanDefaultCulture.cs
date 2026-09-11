using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaddyUi.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CaddyUiDbContext))]
[Migration("20260911070000_GermanDefaultCulture")]
public sealed class GermanDefaultCulture : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER TABLE caddy_ui.users
                ALTER COLUMN language SET DEFAULT 'de';

            COMMENT ON COLUMN caddy_ui.users.language IS
                'BCP 47-compatible UI culture preference. German is the product default; English is supported.';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER TABLE caddy_ui.users
                ALTER COLUMN language SET DEFAULT 'en';

            COMMENT ON COLUMN caddy_ui.users.language IS
                'BCP 47-compatible UI culture preference. English is the product default.';
            """);
    }
}
