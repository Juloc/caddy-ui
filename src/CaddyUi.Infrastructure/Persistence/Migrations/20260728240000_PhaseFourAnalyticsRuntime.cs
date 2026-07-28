using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaddyUi.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CaddyUiDbContext))]
[Migration("20260728240000_PhaseFourAnalyticsRuntime")]
public sealed class PhaseFourAnalyticsRuntime : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER TABLE caddy_ui.navigation_events
                DROP CONSTRAINT fk_navigation_events_request;

            CREATE UNIQUE INDEX ix_navigation_events_request
                ON caddy_ui.navigation_events(request_event_id, request_occurred_at);
            CREATE UNIQUE INDEX ix_page_views_navigation
                ON caddy_ui.page_views(navigation_event_id);
            CREATE INDEX ix_page_loads_started_at
                ON caddy_ui.page_loads(started_at DESC);
            CREATE INDEX ix_analytics_sessions_active
                ON caddy_ui.analytics_sessions(
                    anonymous_client_id,
                    host,
                    last_activity_at DESC)
                WHERE ended_at IS NULL;
            CREATE INDEX ix_anonymous_clients_first_party_hash
                ON caddy_ui.anonymous_clients(first_party_identifier_hash)
                WHERE first_party_identifier_hash IS NOT NULL;

            CREATE OR REPLACE FUNCTION caddy_ui.drop_expired_request_event_partitions(cutoff date)
            RETURNS integer
            LANGUAGE plpgsql
            AS $function$
            DECLARE
                partition_record record;
                partition_month date;
                dropped_count integer := 0;
            BEGIN
                FOR partition_record IN
                    SELECT child.relname AS partition_name
                    FROM pg_inherits
                    JOIN pg_class AS parent ON pg_inherits.inhparent = parent.oid
                    JOIN pg_class AS child ON pg_inherits.inhrelid = child.oid
                    JOIN pg_namespace AS schema ON child.relnamespace = schema.oid
                    WHERE schema.nspname = 'caddy_ui'
                      AND parent.relname = 'request_events'
                      AND child.relname ~ '^request_events_[0-9]{4}_[0-9]{2}$'
                LOOP
                    partition_month := to_date(
                        substring(partition_record.partition_name from 16),
                        'YYYY_MM');
                    IF partition_month + interval '1 month' <= cutoff THEN
                        EXECUTE format(
                            'DROP TABLE IF EXISTS caddy_ui.%I',
                            partition_record.partition_name);
                        dropped_count := dropped_count + 1;
                    END IF;
                END LOOP;

                RETURN dropped_count;
            END;
            $function$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP FUNCTION IF EXISTS caddy_ui.drop_expired_request_event_partitions(date);
            DROP INDEX IF EXISTS caddy_ui.ix_anonymous_clients_first_party_hash;
            DROP INDEX IF EXISTS caddy_ui.ix_analytics_sessions_active;
            DROP INDEX IF EXISTS caddy_ui.ix_page_loads_started_at;
            DROP INDEX IF EXISTS caddy_ui.ix_page_views_navigation;
            DROP INDEX IF EXISTS caddy_ui.ix_navigation_events_request;

            ALTER TABLE caddy_ui.navigation_events
                ADD CONSTRAINT fk_navigation_events_request
                FOREIGN KEY (request_event_id, request_occurred_at)
                REFERENCES caddy_ui.request_events(id, occurred_at)
                ON DELETE CASCADE;
            """);
    }
}
