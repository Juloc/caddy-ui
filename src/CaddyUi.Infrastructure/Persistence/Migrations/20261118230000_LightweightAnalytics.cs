using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CaddyUi.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CaddyUiDbContext))]
[Migration("20261118230000_LightweightAnalytics")]
public sealed class LightweightAnalytics : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE caddy_ui.traffic_buckets (
                bucket_start timestamptz NOT NULL,
                host text NOT NULL,
                actor_type text NOT NULL,
                status_class integer NOT NULL,
                requests bigint NOT NULL,
                bytes_sent bigint NOT NULL,
                PRIMARY KEY (bucket_start, host, actor_type, status_class)
            );
            CREATE INDEX ix_traffic_buckets_host_start ON caddy_ui.traffic_buckets(host, bucket_start);
            CREATE TABLE caddy_ui.client_activity_buckets (
                client_id uuid NOT NULL REFERENCES caddy_ui.anonymous_clients(id) ON DELETE NO ACTION,
                bucket_start timestamptz NOT NULL,
                first_seen_at timestamptz NOT NULL,
                last_seen_at timestamptz NOT NULL,
                remote_address inet NULL,
                user_agent varchar(512) NOT NULL,
                is_bot boolean NOT NULL,
                is_human boolean NOT NULL,
                request_count bigint NOT NULL,
                not_found_count bigint NOT NULL,
                auth_failure_count bigint NOT NULL,
                unsafe_method_count bigint NOT NULL,
                scanner_count bigint NOT NULL,
                gap_count bigint NOT NULL,
                gap_sum double precision NOT NULL,
                gap_squares double precision NOT NULL,
                path_hashes jsonb NOT NULL,
                hosts jsonb NOT NULL,
                PRIMARY KEY (client_id, bucket_start)
            );
            CREATE INDEX ix_client_activity_buckets_last_seen ON caddy_ui.client_activity_buckets(last_seen_at);
            CREATE INDEX ix_anonymous_clients_last_seen ON caddy_ui.anonymous_clients(last_seen_at DESC);
            INSERT INTO caddy_ui.application_settings(key, value_json, updated_at)
            VALUES ('analytics.lightweight_started_at', jsonb_build_object('at', CURRENT_TIMESTAMP), CURRENT_TIMESTAMP);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $rollback$
            BEGIN
                IF EXISTS (SELECT 1 FROM caddy_ui.traffic_buckets) OR EXISTS (SELECT 1 FROM caddy_ui.client_activity_buckets) THEN
                    RAISE EXCEPTION 'Export the new analytics data and restore the pre-upgrade backup before rolling back.';
                END IF;
            END $rollback$;
            DROP TABLE caddy_ui.client_activity_buckets;
            DROP TABLE caddy_ui.traffic_buckets;
            DROP INDEX caddy_ui.ix_anonymous_clients_last_seen;
            DELETE FROM caddy_ui.application_settings WHERE key = 'analytics.lightweight_started_at';
            """);
    }
}
