from __future__ import annotations

import json
import pathlib
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[1]
COMPOSE = ROOT / "deploy" / "shadow" / "docker-compose.yml"
ENV_EXAMPLE = ROOT / "deploy" / "shadow" / ".env.example"
LEGACY_STATISTICS = ROOT / "deploy" / "shadow" / "legacy-statistics.example.json"
PREFLIGHT = ROOT / "scripts" / "shadow-preflight.sh"
VERSION = ROOT / "VERSION_DOTNET"


class ShadowDeploymentContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.compose = COMPOSE.read_text(encoding="utf-8")
        cls.environment = ENV_EXAMPLE.read_text(encoding="utf-8")
        cls.legacy_statistics = json.loads(LEGACY_STATISTICS.read_text(encoding="utf-8"))
        cls.preflight = PREFLIGHT.read_text(encoding="utf-8")
        cls.version = VERSION.read_text(encoding="utf-8").strip()

    def test_shadow_stack_uses_immutable_dotnet_beta_image(self) -> None:
        self.assertEqual("2.0.0-beta.1", self.version)
        self.assertIn(
            "ghcr.io/juloc/caddy-ui-dotnet-companion:${CADDY_UI_SHADOW_VERSION:-2.0.0-beta.1}",
            self.compose,
        )
        self.assertIn("pull_policy: always", self.compose)
        self.assertNotIn("dockerfile: Dockerfile.dotnet", self.compose)
        self.assertNotIn("ghcr.io/juloc/caddy-ui:latest", self.compose)

    def test_shadow_admin_port_has_safe_loopback_default(self) -> None:
        self.assertIn(
            "${CADDY_UI_SHADOW_BIND_ADDRESS:-127.0.0.1}:${CADDY_UI_SHADOW_ADMIN_PORT:-18098}:8098",
            self.compose,
        )
        self.assertIn("CADDY_UI_SHADOW_BIND_ADDRESS=127.0.0.1", self.environment)
        self.assertNotIn('"80:80"', self.compose)
        self.assertNotIn('"443:443"', self.compose)
        self.assertNotIn("8099:8099", self.compose)

    def test_productive_inputs_and_root_filesystems_are_read_only(self) -> None:
        self.assertIn("source: ${CADDY_UI_SHADOW_LOG_DIR}", self.compose)
        self.assertIn("source: ${CADDY_UI_SHADOW_LEGACY_SQLITE}", self.compose)
        self.assertGreaterEqual(self.compose.count("read_only: true"), 4)

    def test_all_productive_write_paths_are_disabled(self) -> None:
        required_settings = (
            "CADDY_UI_ROUTE_WRITE_MODE: disabled",
            "CADDY_UI_DNS_WRITE_MODE: disabled",
            'CADDY_UI_OPERATIONS_WORKER_ENABLED: "false"',
            "CADDY_UI_BLOCKLIST_WRITE_MODE: disabled",
            'CADDY_UI_IP_INTELLIGENCE_ENABLED: "false"',
            'CADDY_UI_RISK_ASSESSMENT_ENABLED: "false"',
            'Cutover__Enabled: "false"',
        )
        for setting in required_settings:
            with self.subTest(setting=setting):
                self.assertIn(setting, self.compose)

    def test_cutover_settings_match_application_options(self) -> None:
        self.assertIn(
            "Cutover__LegacyStatisticsPath: /state/legacy-statistics.json",
            self.compose,
        )
        self.assertIn("Cutover__MaximumBackupAgeHours:", self.compose)
        self.assertIn("Cutover__MaximumMetricDifferencePercent:", self.compose)
        self.assertNotIn("Cutover__MaximumIngestionLagMinutes", self.compose)
        self.assertNotIn("Cutover__StatisticsTolerancePercent", self.compose)
        self.assertNotIn("CADDY_UI_SHADOW_MAX_LAG_MINUTES", self.environment)

    def test_legacy_statistics_example_matches_required_schema(self) -> None:
        self.assertEqual(
            {
                "capturedAt",
                "windowStart",
                "windowEnd",
                "requests",
                "pageViews",
                "sessions",
                "clients",
                "errors",
            },
            set(self.legacy_statistics),
        )
        self.assertLess(
            self.legacy_statistics["windowStart"],
            self.legacy_statistics["windowEnd"],
        )

    def test_stack_has_no_docker_socket_and_uses_internal_network(self) -> None:
        self.assertNotIn("/var/run/docker.sock", self.compose)
        self.assertIn("internal: true", self.compose)
        self.assertIn("no-new-privileges:true", self.compose)
        self.assertIn("cap_drop:", self.compose)

    def test_example_requires_absolute_operator_paths_and_non_default_secrets(self) -> None:
        self.assertIn(
            "CADDY_UI_SHADOW_LOG_DIR=/absolute/path/to/caddy/logs",
            self.environment,
        )
        self.assertIn(
            "CADDY_UI_SHADOW_LEGACY_SQLITE=/absolute/path/to/caddy-ui.db",
            self.environment,
        )
        self.assertIn("replace-with-a-long-random-password", self.environment)
        self.assertIn(
            "replace-with-a-different-long-random-password",
            self.environment,
        )

    def test_preflight_is_validation_only(self) -> None:
        self.assertIn("docker compose", self.preflight)
        self.assertIn(" config >/dev/null", self.preflight)
        self.assertNotIn("docker compose down", self.preflight)
        self.assertNotIn("docker volume rm", self.preflight)
        self.assertNotIn("docker rm", self.preflight)
        self.assertNotIn("CADDY_UI_SHADOW_MAX_LAG_MINUTES", self.preflight)

        executable_lines = [
            line.strip()
            for line in self.preflight.splitlines()
            if line.strip() and not line.lstrip().startswith(("#", "printf"))
        ]
        self.assertFalse(
            any("docker compose" in line and " up " in line for line in executable_lines)
        )


if __name__ == "__main__":
    unittest.main()
