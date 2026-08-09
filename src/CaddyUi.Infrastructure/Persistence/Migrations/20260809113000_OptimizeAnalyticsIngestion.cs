using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaddyUi.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CaddyUiDbContext))]
[Migration("20260809113000_OptimizeAnalyticsIngestion")]
public sealed class OptimizeAnalyticsIngestion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE INDEX ix_page_views_client_host_occurred_at
                ON caddy_ui.page_views(
                    anonymous_client_id,
                    host,
                    occurred_at DESC)
                WHERE anonymous_client_id IS NOT NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP INDEX IF EXISTS caddy_ui.ix_page_views_client_host_occurred_at;
            """);
    }
}
