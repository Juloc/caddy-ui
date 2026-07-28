using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaddyUi.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CaddyUiDbContext))]
[Migration("20260728250000_PhaseFiveIpSecurity")]
public sealed class PhaseFiveIpSecurity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER TABLE caddy_ui.ip_intelligence_cache
                DROP CONSTRAINT ck_ip_intelligence_cache_scope;
            ALTER TABLE caddy_ui.ip_intelligence_cache
                ADD CONSTRAINT ck_ip_intelligence_cache_scope
                CHECK (scope IN (
                    'public', 'private', 'loopback', 'link-local', 'multicast',
                    'documentation', 'shared', 'benchmark', 'reserved',
                    'unspecified', 'unknown')),
                ADD COLUMN failure_count integer NOT NULL DEFAULT 0,
                ADD COLUMN last_error_at timestamp with time zone NULL;

            CREATE TABLE caddy_ui.ip_intelligence_refresh_queue (
                address inet PRIMARY KEY,
                requested_at timestamp with time zone NOT NULL,
                not_before timestamp with time zone NOT NULL,
                attempt integer NOT NULL DEFAULT 0,
                last_error text NOT NULL DEFAULT ''
            );
            CREATE INDEX ix_ip_intelligence_refresh_queue_ready
                ON caddy_ui.ip_intelligence_refresh_queue(not_before, requested_at);

            ALTER TABLE caddy_ui.client_assessments
                DROP CONSTRAINT ck_client_assessments_classification;
            ALTER TABLE caddy_ui.client_assessments
                ADD CONSTRAINT ck_client_assessments_classification
                CHECK (classification IN ('human', 'bot', 'suspicious', 'unknown')),
                ADD COLUMN request_count bigint NOT NULL DEFAULT 0,
                ADD COLUMN sample_json jsonb NOT NULL DEFAULT '{}'::jsonb;
            CREATE INDEX ix_client_assessments_remote_created
                ON caddy_ui.client_assessments(remote_address, created_at DESC);

            ALTER TABLE caddy_ui.ip_block_rules
                ADD COLUMN activation_state character varying(24) NOT NULL DEFAULT 'shadow',
                ADD COLUMN correlation_id text NOT NULL DEFAULT '',
                ADD COLUMN updated_at timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP;
            ALTER TABLE caddy_ui.ip_block_rules
                ADD CONSTRAINT ck_ip_block_rules_activation_state
                CHECK (activation_state IN ('shadow', 'active', 'failed', 'released'));
            CREATE INDEX ix_ip_block_rules_unreleased_target
                ON caddy_ui.ip_block_rules(address_or_network)
                WHERE enabled = true AND released_at IS NULL;

            ALTER TABLE caddy_ui.ip_block_history
                ADD COLUMN correlation_id text NOT NULL DEFAULT '',
                ADD COLUMN details_json jsonb NOT NULL DEFAULT '{}'::jsonb;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER TABLE caddy_ui.ip_block_history
                DROP COLUMN IF EXISTS details_json,
                DROP COLUMN IF EXISTS correlation_id;

            DROP INDEX IF EXISTS caddy_ui.ix_ip_block_rules_unreleased_target;
            ALTER TABLE caddy_ui.ip_block_rules
                DROP CONSTRAINT IF EXISTS ck_ip_block_rules_activation_state,
                DROP COLUMN IF EXISTS updated_at,
                DROP COLUMN IF EXISTS correlation_id,
                DROP COLUMN IF EXISTS activation_state;

            DROP INDEX IF EXISTS caddy_ui.ix_client_assessments_remote_created;
            ALTER TABLE caddy_ui.client_assessments
                DROP CONSTRAINT IF EXISTS ck_client_assessments_classification,
                DROP COLUMN IF EXISTS sample_json,
                DROP COLUMN IF EXISTS request_count;
            ALTER TABLE caddy_ui.client_assessments
                ADD CONSTRAINT ck_client_assessments_classification
                CHECK (classification IN ('human', 'bot', 'unknown'));

            DROP TABLE IF EXISTS caddy_ui.ip_intelligence_refresh_queue;

            ALTER TABLE caddy_ui.ip_intelligence_cache
                DROP CONSTRAINT IF EXISTS ck_ip_intelligence_cache_scope,
                DROP COLUMN IF EXISTS last_error_at,
                DROP COLUMN IF EXISTS failure_count;
            ALTER TABLE caddy_ui.ip_intelligence_cache
                ADD CONSTRAINT ck_ip_intelligence_cache_scope
                CHECK (scope IN (
                    'public', 'private', 'loopback', 'link-local',
                    'reserved', 'unknown'));
            """);
    }
}
