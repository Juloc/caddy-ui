using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaddyUi.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CaddyUiDbContext))]
[Migration("20260728230000_PhaseThreeAuthenticationAndDomainManagement")]
public sealed class PhaseThreeAuthenticationAndDomainManagement : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER TABLE caddy_ui.dns_providers
                ADD COLUMN enabled boolean NOT NULL DEFAULT true,
                ADD COLUMN secret_references_json jsonb NOT NULL DEFAULT '{}'::jsonb,
                ADD COLUMN last_tested_at timestamp with time zone NULL,
                ADD COLUMN last_test_status character varying(24) NOT NULL DEFAULT 'untested',
                ADD COLUMN last_test_error text NOT NULL DEFAULT '';

            CREATE UNIQUE INDEX ix_dns_providers_label_normalized
                ON caddy_ui.dns_providers (lower(label));

            CREATE TABLE caddy_ui.managed_domains (
                id uuid PRIMARY KEY,
                name text NOT NULL,
                display_name text NOT NULL,
                enabled boolean NOT NULL DEFAULT true,
                is_default boolean NOT NULL DEFAULT false,
                default_certificate_mode character varying(16) NOT NULL DEFAULT 'wildcard',
                dns_provider_id uuid NULL REFERENCES caddy_ui.dns_providers(id) ON DELETE SET NULL,
                config_json jsonb NOT NULL DEFAULT '{}'::jsonb,
                created_at timestamp with time zone NOT NULL,
                updated_at timestamp with time zone NOT NULL,
                CONSTRAINT ck_managed_domains_certificate_mode
                    CHECK (default_certificate_mode IN ('wildcard', 'individual'))
            );
            CREATE UNIQUE INDEX ix_managed_domains_name_normalized
                ON caddy_ui.managed_domains (lower(name));
            CREATE UNIQUE INDEX ix_managed_domains_single_default
                ON caddy_ui.managed_domains (is_default)
                WHERE is_default;
            CREATE INDEX ix_managed_domains_dns_provider_id
                ON caddy_ui.managed_domains (dns_provider_id);

            ALTER TABLE caddy_ui.managed_routes
                ADD COLUMN domain_id uuid NULL,
                ADD COLUMN subdomain text NOT NULL DEFAULT '',
                ADD COLUMN certificate_mode character varying(16) NOT NULL DEFAULT 'inherit',
                ADD CONSTRAINT fk_managed_routes_domain
                    FOREIGN KEY (domain_id)
                    REFERENCES caddy_ui.managed_domains(id)
                    ON DELETE RESTRICT,
                ADD CONSTRAINT ck_managed_routes_certificate_mode
                    CHECK (certificate_mode IN ('inherit', 'wildcard', 'individual'));
            CREATE INDEX ix_managed_routes_domain_id
                ON caddy_ui.managed_routes (domain_id);

            COMMENT ON COLUMN caddy_ui.managed_domains.default_certificate_mode IS
                'Wildcard is the default. Individual certificates require an explicit opt-in.';
            COMMENT ON COLUMN caddy_ui.managed_routes.certificate_mode IS
                'inherit resolves to the managed domain default, which is wildcard by default.';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP INDEX IF EXISTS caddy_ui.ix_managed_routes_domain_id;
            ALTER TABLE caddy_ui.managed_routes
                DROP CONSTRAINT IF EXISTS ck_managed_routes_certificate_mode,
                DROP CONSTRAINT IF EXISTS fk_managed_routes_domain,
                DROP COLUMN IF EXISTS certificate_mode,
                DROP COLUMN IF EXISTS subdomain,
                DROP COLUMN IF EXISTS domain_id;

            DROP TABLE IF EXISTS caddy_ui.managed_domains;

            DROP INDEX IF EXISTS caddy_ui.ix_dns_providers_label_normalized;
            ALTER TABLE caddy_ui.dns_providers
                DROP COLUMN IF EXISTS last_test_error,
                DROP COLUMN IF EXISTS last_test_status,
                DROP COLUMN IF EXISTS last_tested_at,
                DROP COLUMN IF EXISTS secret_references_json,
                DROP COLUMN IF EXISTS enabled;
            """);
    }
}
