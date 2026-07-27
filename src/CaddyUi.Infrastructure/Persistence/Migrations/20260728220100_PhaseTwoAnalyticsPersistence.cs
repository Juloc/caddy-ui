using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaddyUi.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CaddyUiDbContext))]
[Migration("20260728220100_PhaseTwoAnalyticsPersistence")]
public sealed class PhaseTwoAnalyticsPersistence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE TABLE caddy_ui.anonymous_clients (
                id uuid PRIMARY KEY,
                client_key text NOT NULL,
                first_seen_at timestamp with time zone NOT NULL,
                last_seen_at timestamp with time zone NOT NULL,
                first_party_identifier_hash text NULL,
                metadata_json jsonb NOT NULL DEFAULT '{}'::jsonb
            );
            CREATE UNIQUE INDEX ix_anonymous_clients_client_key ON caddy_ui.anonymous_clients (client_key);

            CREATE TABLE caddy_ui.analytics_sessions (
                id uuid PRIMARY KEY,
                anonymous_client_id uuid NOT NULL REFERENCES caddy_ui.anonymous_clients(id) ON DELETE CASCADE,
                host text NOT NULL,
                started_at timestamp with time zone NOT NULL,
                last_activity_at timestamp with time zone NOT NULL,
                ended_at timestamp with time zone NULL,
                page_view_count integer NOT NULL DEFAULT 0,
                request_count bigint NOT NULL DEFAULT 0
            );
            CREATE INDEX ix_analytics_sessions_client_started
                ON caddy_ui.analytics_sessions (anonymous_client_id, started_at DESC);

            CREATE TABLE caddy_ui.request_events (
                id uuid NOT NULL,
                occurred_at timestamp with time zone NOT NULL,
                ingested_at timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
                source_file text NOT NULL,
                source_offset bigint NOT NULL,
                host text NOT NULL,
                method character varying(16) NOT NULL,
                path text NOT NULL,
                query_string text NOT NULL DEFAULT '',
                status integer NOT NULL,
                duration_ms double precision NOT NULL DEFAULT 0,
                bytes_sent bigint NOT NULL DEFAULT 0,
                remote_address inet NULL,
                user_agent text NOT NULL DEFAULT '',
                referer text NOT NULL DEFAULT '',
                accept_header text NOT NULL DEFAULT '',
                content_type text NOT NULL DEFAULT '',
                sec_fetch_dest text NOT NULL DEFAULT '',
                actor_type character varying(16) NOT NULL DEFAULT 'unknown',
                request_type character varying(24) NOT NULL DEFAULT 'other',
                classification_confidence character varying(16) NOT NULL DEFAULT 'low',
                managed_route_id uuid NULL,
                anonymous_client_id uuid NULL,
                raw_json jsonb NOT NULL,
                PRIMARY KEY (id, occurred_at),
                CONSTRAINT ck_request_events_actor_type
                    CHECK (actor_type IN ('human', 'bot', 'internal', 'unknown')),
                CONSTRAINT ck_request_events_request_type
                    CHECK (request_type IN ('document', 'asset', 'api', 'websocket', 'healthcheck', 'auth', 'system', 'other')),
                CONSTRAINT ck_request_events_confidence
                    CHECK (classification_confidence IN ('high', 'medium', 'low'))
            ) PARTITION BY RANGE (occurred_at);

            CREATE INDEX ix_request_events_occurred_at ON caddy_ui.request_events (occurred_at DESC);
            CREATE INDEX ix_request_events_host_occurred_at ON caddy_ui.request_events (host, occurred_at DESC);
            CREATE INDEX ix_request_events_type_occurred_at
                ON caddy_ui.request_events (request_type, occurred_at DESC);
            CREATE INDEX ix_request_events_status_occurred_at ON caddy_ui.request_events (status, occurred_at DESC);
            CREATE INDEX ix_request_events_client_occurred_at
                ON caddy_ui.request_events (anonymous_client_id, occurred_at DESC);
            CREATE UNIQUE INDEX ix_request_events_source_position
                ON caddy_ui.request_events (source_file, source_offset, occurred_at);

            CREATE TABLE caddy_ui.request_events_default
                PARTITION OF caddy_ui.request_events DEFAULT;

            CREATE OR REPLACE FUNCTION caddy_ui.ensure_request_event_partition(target_month date)
            RETURNS text
            LANGUAGE plpgsql
            AS $function$
            DECLARE
                partition_start date := date_trunc('month', target_month)::date;
                partition_end date := (partition_start + interval '1 month')::date;
                partition_name text := 'request_events_' || to_char(partition_start, 'YYYY_MM');
            BEGIN
                EXECUTE format(
                    'CREATE TABLE IF NOT EXISTS caddy_ui.%I PARTITION OF caddy_ui.request_events FOR VALUES FROM (%L) TO (%L)',
                    partition_name,
                    partition_start,
                    partition_end);
                RETURN partition_name;
            END;
            $function$;

            SELECT caddy_ui.ensure_request_event_partition(CURRENT_DATE);
            SELECT caddy_ui.ensure_request_event_partition((CURRENT_DATE + interval '1 month')::date);

            CREATE TABLE caddy_ui.navigation_events (
                id uuid PRIMARY KEY,
                request_event_id uuid NOT NULL,
                request_occurred_at timestamp with time zone NOT NULL,
                analytics_session_id uuid NULL REFERENCES caddy_ui.analytics_sessions(id) ON DELETE SET NULL,
                occurred_at timestamp with time zone NOT NULL,
                host text NOT NULL,
                path text NOT NULL,
                state character varying(24) NOT NULL,
                evidence_json jsonb NOT NULL,
                CONSTRAINT fk_navigation_events_request
                    FOREIGN KEY (request_event_id, request_occurred_at)
                    REFERENCES caddy_ui.request_events(id, occurred_at)
                    ON DELETE CASCADE
            );
            CREATE INDEX ix_navigation_events_occurred_at ON caddy_ui.navigation_events (occurred_at DESC);

            CREATE TABLE caddy_ui.page_views (
                id uuid PRIMARY KEY,
                navigation_event_id uuid NOT NULL REFERENCES caddy_ui.navigation_events(id) ON DELETE CASCADE,
                analytics_session_id uuid NULL REFERENCES caddy_ui.analytics_sessions(id) ON DELETE SET NULL,
                anonymous_client_id uuid NULL REFERENCES caddy_ui.anonymous_clients(id) ON DELETE SET NULL,
                occurred_at timestamp with time zone NOT NULL,
                host text NOT NULL,
                path text NOT NULL,
                source character varying(24) NOT NULL,
                successful boolean NOT NULL,
                estimated boolean NOT NULL DEFAULT true
            );
            CREATE INDEX ix_page_views_host_occurred_at ON caddy_ui.page_views (host, occurred_at DESC);
            CREATE INDEX ix_page_views_client_occurred_at ON caddy_ui.page_views (anonymous_client_id, occurred_at DESC);

            CREATE TABLE caddy_ui.page_loads (
                id uuid PRIMARY KEY,
                page_view_id uuid NOT NULL REFERENCES caddy_ui.page_views(id) ON DELETE CASCADE,
                started_at timestamp with time zone NOT NULL,
                completed_at timestamp with time zone NULL,
                request_count integer NOT NULL DEFAULT 0,
                asset_request_count integer NOT NULL DEFAULT 0,
                api_request_count integer NOT NULL DEFAULT 0,
                bytes_sent bigint NOT NULL DEFAULT 0,
                estimated boolean NOT NULL DEFAULT true,
                grouping_evidence_json jsonb NOT NULL DEFAULT '{}'::jsonb
            );
            CREATE UNIQUE INDEX ix_page_loads_page_view_id ON caddy_ui.page_loads (page_view_id);

            CREATE TABLE caddy_ui.hourly_traffic_aggregates (
                bucket_start timestamp with time zone NOT NULL,
                host text NOT NULL,
                status_class character varying(8) NOT NULL,
                actor_type character varying(16) NOT NULL DEFAULT 'unknown',
                request_type character varying(24) NOT NULL DEFAULT 'other',
                requests bigint NOT NULL,
                page_views bigint NOT NULL DEFAULT 0,
                bytes_sent bigint NOT NULL,
                duration_sum_ms double precision NOT NULL DEFAULT 0,
                duration_max_ms double precision NOT NULL DEFAULT 0,
                PRIMARY KEY (bucket_start, host, status_class, actor_type, request_type)
            );

            CREATE TABLE caddy_ui.daily_traffic_aggregates (
                bucket_start date NOT NULL,
                host text NOT NULL,
                status_class character varying(8) NOT NULL,
                actor_type character varying(16) NOT NULL DEFAULT 'unknown',
                request_type character varying(24) NOT NULL DEFAULT 'other',
                requests bigint NOT NULL,
                page_views bigint NOT NULL DEFAULT 0,
                bytes_sent bigint NOT NULL,
                duration_sum_ms double precision NOT NULL DEFAULT 0,
                duration_max_ms double precision NOT NULL DEFAULT 0,
                PRIMARY KEY (bucket_start, host, status_class, actor_type, request_type)
            );

            CREATE TABLE caddy_ui.monthly_traffic_aggregates (
                bucket_start date NOT NULL,
                host text NOT NULL,
                status_class character varying(8) NOT NULL,
                actor_type character varying(16) NOT NULL DEFAULT 'unknown',
                request_type character varying(24) NOT NULL DEFAULT 'other',
                requests bigint NOT NULL,
                page_views bigint NOT NULL DEFAULT 0,
                bytes_sent bigint NOT NULL,
                duration_sum_ms double precision NOT NULL DEFAULT 0,
                duration_max_ms double precision NOT NULL DEFAULT 0,
                PRIMARY KEY (bucket_start, host, status_class, actor_type, request_type)
            );

            CREATE TABLE caddy_ui.route_performance_aggregates (
                bucket_start timestamp with time zone NOT NULL,
                granularity character varying(8) NOT NULL,
                host text NOT NULL,
                path_pattern text NOT NULL,
                request_type character varying(24) NOT NULL,
                request_count bigint NOT NULL,
                error_count bigint NOT NULL,
                duration_sum_ms double precision NOT NULL,
                duration_max_ms double precision NOT NULL,
                p50_ms double precision NULL,
                p95_ms double precision NULL,
                p99_ms double precision NULL,
                PRIMARY KEY (bucket_start, granularity, host, path_pattern, request_type)
            );

            CREATE TABLE caddy_ui.analytics_checkpoints (
                source text PRIMARY KEY,
                source_identity text NOT NULL,
                byte_offset bigint NOT NULL,
                last_event_at timestamp with time zone NULL,
                updated_at timestamp with time zone NOT NULL,
                metadata_json jsonb NOT NULL DEFAULT '{}'::jsonb
            );

            CREATE TABLE caddy_ui.ingestion_failures (
                id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                source text NOT NULL,
                source_offset bigint NULL,
                occurred_at timestamp with time zone NOT NULL,
                raw_line text NOT NULL,
                error text NOT NULL,
                resolved_at timestamp with time zone NULL
            );
            CREATE INDEX ix_ingestion_failures_occurred_at ON caddy_ui.ingestion_failures (occurred_at DESC);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP TABLE IF EXISTS caddy_ui.ingestion_failures;
            DROP TABLE IF EXISTS caddy_ui.analytics_checkpoints;
            DROP TABLE IF EXISTS caddy_ui.route_performance_aggregates;
            DROP TABLE IF EXISTS caddy_ui.monthly_traffic_aggregates;
            DROP TABLE IF EXISTS caddy_ui.daily_traffic_aggregates;
            DROP TABLE IF EXISTS caddy_ui.hourly_traffic_aggregates;
            DROP TABLE IF EXISTS caddy_ui.page_loads;
            DROP TABLE IF EXISTS caddy_ui.page_views;
            DROP TABLE IF EXISTS caddy_ui.navigation_events;
            DROP FUNCTION IF EXISTS caddy_ui.ensure_request_event_partition(date);
            DROP TABLE IF EXISTS caddy_ui.request_events CASCADE;
            DROP TABLE IF EXISTS caddy_ui.analytics_sessions;
            DROP TABLE IF EXISTS caddy_ui.anonymous_clients;
            """);
    }
}
