using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaddyUi.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CaddyUiDbContext))]
[Migration("20260728270000_PhaseSevenRouteManagement")]
public sealed class PhaseSevenRouteManagement : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER TABLE caddy_ui.access_groups
                ADD COLUMN enabled boolean NOT NULL DEFAULT true,
                ADD COLUMN description text NOT NULL DEFAULT '';

            ALTER TABLE caddy_ui.managed_routes
                ADD COLUMN access_group_id uuid NULL,
                ADD COLUMN sort_order integer NOT NULL DEFAULT 0,
                ADD CONSTRAINT fk_managed_routes_access_group
                    FOREIGN KEY (access_group_id)
                    REFERENCES caddy_ui.access_groups(id)
                    ON DELETE SET NULL;

            CREATE INDEX ix_managed_routes_access_group_id
                ON caddy_ui.managed_routes (access_group_id);
            CREATE INDEX ix_managed_routes_order
                ON caddy_ui.managed_routes (lower(host), sort_order, lower(name));

            COMMENT ON COLUMN caddy_ui.managed_routes.config_json IS
                'Typed route-v1 document. Secrets and plaintext credentials are forbidden.';
            COMMENT ON COLUMN caddy_ui.managed_routes.access_group_id IS
                'Optional portal access group enforced through generated forward_auth.';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP INDEX IF EXISTS caddy_ui.ix_managed_routes_order;
            DROP INDEX IF EXISTS caddy_ui.ix_managed_routes_access_group_id;
            ALTER TABLE caddy_ui.managed_routes
                DROP CONSTRAINT IF EXISTS fk_managed_routes_access_group,
                DROP COLUMN IF EXISTS sort_order,
                DROP COLUMN IF EXISTS access_group_id;

            ALTER TABLE caddy_ui.access_groups
                DROP COLUMN IF EXISTS description,
                DROP COLUMN IF EXISTS enabled;
            """);
    }
}
