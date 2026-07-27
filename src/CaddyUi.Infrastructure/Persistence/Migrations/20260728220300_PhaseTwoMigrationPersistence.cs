using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaddyUi.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CaddyUiDbContext))]
[Migration("20260728220300_PhaseTwoMigrationPersistence")]
public sealed class PhaseTwoMigrationPersistence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE TABLE caddy_ui.migration_runs (
                id uuid PRIMARY KEY,
                source_path text NOT NULL,
                source_digest text NOT NULL,
                source_schema_version integer NOT NULL,
                source_size_bytes bigint NOT NULL,
                started_at timestamp with time zone NOT NULL,
                completed_at timestamp with time zone NULL,
                status character varying(24) NOT NULL,
                dry_run boolean NOT NULL DEFAULT false,
                backup_path text NULL,
                report_json jsonb NOT NULL DEFAULT '{}'::jsonb,
                error text NOT NULL DEFAULT ''
            );
            CREATE UNIQUE INDEX ix_migration_runs_successful_digest
                ON caddy_ui.migration_runs (source_digest)
                WHERE status = 'succeeded';
            CREATE INDEX ix_migration_runs_started_at ON caddy_ui.migration_runs (started_at DESC);

            CREATE TABLE caddy_ui.migration_table_results (
                migration_run_id uuid NOT NULL REFERENCES caddy_ui.migration_runs(id) ON DELETE CASCADE,
                table_name text NOT NULL,
                source_rows bigint NOT NULL,
                imported_rows bigint NOT NULL,
                preserved_rows bigint NOT NULL,
                skipped_rows bigint NOT NULL,
                target_table text NOT NULL,
                note text NOT NULL DEFAULT '',
                PRIMARY KEY (migration_run_id, table_name)
            );

            CREATE TABLE caddy_ui.legacy_import_keys (
                source_digest text NOT NULL,
                table_name text NOT NULL,
                source_key text NOT NULL,
                target_table text NOT NULL,
                target_key text NOT NULL,
                imported_at timestamp with time zone NOT NULL,
                PRIMARY KEY (source_digest, table_name, source_key)
            );

            CREATE TABLE caddy_ui.legacy_source_rows (
                source_digest text NOT NULL,
                table_name text NOT NULL,
                source_key text NOT NULL,
                payload_json jsonb NOT NULL,
                imported_at timestamp with time zone NOT NULL,
                PRIMARY KEY (source_digest, table_name, source_key)
            );

            CREATE TABLE caddy_ui.legacy_migration_state (
                source_name text PRIMARY KEY,
                imported_at timestamp with time zone NOT NULL,
                source_digest text NOT NULL,
                original_payload_json jsonb NOT NULL
            );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP TABLE IF EXISTS caddy_ui.legacy_migration_state;
            DROP TABLE IF EXISTS caddy_ui.legacy_source_rows;
            DROP TABLE IF EXISTS caddy_ui.legacy_import_keys;
            DROP TABLE IF EXISTS caddy_ui.migration_table_results;
            DROP TABLE IF EXISTS caddy_ui.migration_runs;
            """);
    }
}
