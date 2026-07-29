using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaddyUi.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CaddyUiDbContext))]
[Migration("20260728280000_PhaseEightDnsAndOperations")]
public sealed class PhaseEightDnsAndOperations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE TABLE caddy_ui.managed_dns_records (
                id uuid PRIMARY KEY,
                domain_id uuid NOT NULL REFERENCES caddy_ui.managed_domains(id) ON DELETE CASCADE,
                provider_id uuid NOT NULL REFERENCES caddy_ui.dns_providers(id) ON DELETE RESTRICT,
                name text NOT NULL,
                record_type character varying(16) NOT NULL,
                value text NOT NULL,
                ttl integer NOT NULL DEFAULT 300,
                priority integer NULL,
                enabled boolean NOT NULL DEFAULT true,
                source character varying(24) NOT NULL DEFAULT 'manual',
                created_at timestamp with time zone NOT NULL,
                updated_at timestamp with time zone NOT NULL,
                last_sync_at timestamp with time zone NULL,
                last_sync_status character varying(24) NOT NULL DEFAULT 'pending',
                last_sync_error text NOT NULL DEFAULT '',
                CONSTRAINT ck_managed_dns_records_type
                    CHECK (record_type IN ('A', 'AAAA', 'CNAME', 'TXT', 'MX', 'CAA', 'SRV')),
                CONSTRAINT ck_managed_dns_records_ttl CHECK (ttl BETWEEN 30 AND 86400),
                CONSTRAINT ck_managed_dns_records_source
                    CHECK (source IN ('manual', 'ddns', 'certificate', 'import'))
            );
            CREATE UNIQUE INDEX ix_managed_dns_records_target
                ON caddy_ui.managed_dns_records(domain_id, lower(name), record_type, value);
            CREATE INDEX ix_managed_dns_records_provider
                ON caddy_ui.managed_dns_records(provider_id, enabled);

            CREATE TABLE caddy_ui.ddns_targets (
                id uuid PRIMARY KEY,
                domain_id uuid NOT NULL REFERENCES caddy_ui.managed_domains(id) ON DELETE CASCADE,
                provider_id uuid NOT NULL REFERENCES caddy_ui.dns_providers(id) ON DELETE RESTRICT,
                name text NOT NULL,
                record_type character varying(8) NOT NULL,
                enabled boolean NOT NULL DEFAULT true,
                interval_seconds integer NOT NULL DEFAULT 300,
                address_source character varying(24) NOT NULL DEFAULT 'public',
                static_value text NOT NULL DEFAULT '',
                last_value text NOT NULL DEFAULT '',
                next_run_at timestamp with time zone NOT NULL,
                last_run_at timestamp with time zone NULL,
                last_status character varying(24) NOT NULL DEFAULT 'pending',
                last_error text NOT NULL DEFAULT '',
                created_at timestamp with time zone NOT NULL,
                updated_at timestamp with time zone NOT NULL,
                CONSTRAINT ck_ddns_targets_type CHECK (record_type IN ('A', 'AAAA')),
                CONSTRAINT ck_ddns_targets_interval CHECK (interval_seconds BETWEEN 60 AND 86400),
                CONSTRAINT ck_ddns_targets_source CHECK (address_source IN ('public', 'static'))
            );
            CREATE UNIQUE INDEX ix_ddns_targets_target
                ON caddy_ui.ddns_targets(domain_id, lower(name), record_type);
            CREATE INDEX ix_ddns_targets_due
                ON caddy_ui.ddns_targets(next_run_at) WHERE enabled;

            CREATE TABLE caddy_ui.notification_channels (
                id uuid PRIMARY KEY,
                name text NOT NULL,
                channel_type character varying(24) NOT NULL,
                enabled boolean NOT NULL DEFAULT true,
                config_json jsonb NOT NULL DEFAULT '{}'::jsonb,
                secret_references_json jsonb NOT NULL DEFAULT '{}'::jsonb,
                created_at timestamp with time zone NOT NULL,
                updated_at timestamp with time zone NOT NULL,
                last_tested_at timestamp with time zone NULL,
                last_test_status character varying(24) NOT NULL DEFAULT 'untested',
                last_test_error text NOT NULL DEFAULT '',
                CONSTRAINT ck_notification_channels_type
                    CHECK (channel_type IN ('email', 'webhook', 'discord', 'telegram'))
            );
            CREATE UNIQUE INDEX ix_notification_channels_name
                ON caddy_ui.notification_channels(lower(name));

            ALTER TABLE caddy_ui.scheduled_jobs
                ADD COLUMN interval_seconds integer NOT NULL DEFAULT 300,
                ADD COLUMN last_status character varying(24) NOT NULL DEFAULT 'pending',
                ADD COLUMN last_error text NOT NULL DEFAULT '',
                ADD COLUMN locked_at timestamp with time zone NULL,
                ADD COLUMN lock_owner text NOT NULL DEFAULT '';
            UPDATE caddy_ui.scheduled_jobs
            SET next_run_at = COALESCE(next_run_at, CURRENT_TIMESTAMP);
            ALTER TABLE caddy_ui.scheduled_jobs
                ALTER COLUMN next_run_at SET NOT NULL,
                ALTER COLUMN schedule SET DEFAULT 'interval';
            ALTER TABLE caddy_ui.scheduled_jobs
                ADD CONSTRAINT ck_scheduled_jobs_interval
                    CHECK (interval_seconds BETWEEN 60 AND 604800);
            CREATE UNIQUE INDEX ix_scheduled_jobs_name_normalized
                ON caddy_ui.scheduled_jobs(lower(name));
            CREATE INDEX ix_scheduled_jobs_due ON caddy_ui.scheduled_jobs(next_run_at)
                WHERE enabled;

            ALTER TABLE caddy_ui.job_runs
                ADD COLUMN status character varying(24) NOT NULL DEFAULT 'pending',
                ADD COLUMN message text NOT NULL DEFAULT '',
                ADD COLUMN details_json jsonb NOT NULL DEFAULT '{}'::jsonb,
                ADD COLUMN correlation_id text NULL;
            UPDATE caddy_ui.job_runs
            SET status = CASE
                    WHEN state IN ('succeeded', 'success', 'ok') THEN 'ok'
                    WHEN state IN ('failed', 'error') THEN 'failed'
                    ELSE state
                END,
                message = error,
                details_json = result_json,
                correlation_id = id::text;
            ALTER TABLE caddy_ui.job_runs
                ALTER COLUMN correlation_id SET NOT NULL,
                ALTER COLUMN job_type SET DEFAULT 'system',
                ALTER COLUMN state SET DEFAULT 'running';
            CREATE UNIQUE INDEX ix_job_runs_correlation ON caddy_ui.job_runs(correlation_id);
            CREATE INDEX ix_job_runs_job_started ON caddy_ui.job_runs(job_id, started_at DESC);

            CREATE TABLE caddy_ui.health_targets (
                id uuid PRIMARY KEY,
                name text NOT NULL,
                target_type character varying(16) NOT NULL,
                url text NOT NULL,
                enabled boolean NOT NULL DEFAULT true,
                expected_status_min integer NOT NULL DEFAULT 200,
                expected_status_max integer NOT NULL DEFAULT 399,
                timeout_seconds integer NOT NULL DEFAULT 5,
                created_at timestamp with time zone NOT NULL,
                updated_at timestamp with time zone NOT NULL,
                last_checked_at timestamp with time zone NULL,
                last_status character varying(24) NOT NULL DEFAULT 'unknown',
                last_http_status integer NULL,
                last_duration_ms double precision NULL,
                last_error text NOT NULL DEFAULT '',
                CONSTRAINT ck_health_targets_type CHECK (target_type IN ('public', 'upstream')),
                CONSTRAINT ck_health_targets_status CHECK (
                    expected_status_min BETWEEN 100 AND 599
                    AND expected_status_max BETWEEN expected_status_min AND 599),
                CONSTRAINT ck_health_targets_timeout CHECK (timeout_seconds BETWEEN 1 AND 120)
            );
            CREATE UNIQUE INDEX ix_health_targets_name ON caddy_ui.health_targets(lower(name));

            CREATE TABLE caddy_ui.health_checks (
                id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                target_id uuid NOT NULL REFERENCES caddy_ui.health_targets(id) ON DELETE CASCADE,
                checked_at timestamp with time zone NOT NULL,
                status character varying(24) NOT NULL,
                http_status integer NULL,
                duration_ms double precision NULL,
                error text NOT NULL DEFAULT ''
            );
            CREATE INDEX ix_health_checks_target_checked
                ON caddy_ui.health_checks(target_id, checked_at DESC);

            CREATE TABLE caddy_ui.backup_artifacts (
                id uuid PRIMARY KEY,
                created_at timestamp with time zone NOT NULL,
                file_name text NOT NULL,
                path text NOT NULL,
                size_bytes bigint NOT NULL,
                digest text NOT NULL,
                status character varying(24) NOT NULL,
                error text NOT NULL DEFAULT '',
                manifest_json jsonb NOT NULL DEFAULT '{}'::jsonb
            );
            CREATE INDEX ix_backup_artifacts_created ON caddy_ui.backup_artifacts(created_at DESC);
            CREATE UNIQUE INDEX ix_backup_artifacts_digest ON caddy_ui.backup_artifacts(digest)
                WHERE digest <> '';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP TABLE IF EXISTS caddy_ui.backup_artifacts;
            DROP TABLE IF EXISTS caddy_ui.health_checks;
            DROP TABLE IF EXISTS caddy_ui.health_targets;

            DROP INDEX IF EXISTS caddy_ui.ix_job_runs_job_started;
            DROP INDEX IF EXISTS caddy_ui.ix_job_runs_correlation;
            ALTER TABLE caddy_ui.job_runs
                ALTER COLUMN state DROP DEFAULT,
                ALTER COLUMN job_type DROP DEFAULT,
                DROP COLUMN IF EXISTS correlation_id,
                DROP COLUMN IF EXISTS details_json,
                DROP COLUMN IF EXISTS message,
                DROP COLUMN IF EXISTS status;

            DROP INDEX IF EXISTS caddy_ui.ix_scheduled_jobs_due;
            DROP INDEX IF EXISTS caddy_ui.ix_scheduled_jobs_name_normalized;
            ALTER TABLE caddy_ui.scheduled_jobs
                DROP CONSTRAINT IF EXISTS ck_scheduled_jobs_interval,
                ALTER COLUMN schedule DROP DEFAULT,
                ALTER COLUMN next_run_at DROP NOT NULL,
                DROP COLUMN IF EXISTS lock_owner,
                DROP COLUMN IF EXISTS locked_at,
                DROP COLUMN IF EXISTS last_error,
                DROP COLUMN IF EXISTS last_status,
                DROP COLUMN IF EXISTS interval_seconds;

            DROP TABLE IF EXISTS caddy_ui.notification_channels;
            DROP TABLE IF EXISTS caddy_ui.ddns_targets;
            DROP TABLE IF EXISTS caddy_ui.managed_dns_records;
            """);
    }
}
