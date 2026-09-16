using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaddyUi.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CaddyUiDbContext))]
[Migration("20260916203000_ScopeManagedRouteNamesToDomain")]
public sealed class ScopeManagedRouteNamesToDomain : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP INDEX IF EXISTS caddy_ui.ix_managed_routes_name_normalized;
            CREATE UNIQUE INDEX ix_managed_routes_domain_name_normalized
                ON caddy_ui.managed_routes (domain_id, lower(name));
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP INDEX IF EXISTS caddy_ui.ix_managed_routes_domain_name_normalized;
            CREATE UNIQUE INDEX ix_managed_routes_name_normalized
                ON caddy_ui.managed_routes (lower(name));
            """);
    }
}
