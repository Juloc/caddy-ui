using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaddyUi.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CaddyUiDbContext))]
[Migration("20260731200000_MultilingualUserPreferences")]
public sealed class MultilingualUserPreferences : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER TABLE caddy_ui.users
                ADD COLUMN language character varying(16) NOT NULL DEFAULT 'en',
                ADD CONSTRAINT ck_users_language
                    CHECK (language ~ '^[a-z]{2}(-[a-z0-9]{2,8})*$');

            COMMENT ON COLUMN caddy_ui.users.language IS
                'BCP 47-compatible UI culture preference. English is the product default.';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER TABLE caddy_ui.users
                DROP CONSTRAINT IF EXISTS ck_users_language,
                DROP COLUMN IF EXISTS language;
            """);
    }
}
