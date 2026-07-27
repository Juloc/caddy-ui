from __future__ import annotations

import sys
from datetime import UTC, datetime, timedelta
from typing import Any

from . import protection


def install() -> None:
    base_type = protection.SecurityService
    if getattr(base_type, "_caddy_ui_multidimensional", False):
        service_type = base_type
    else:

        class MultiDimensionSecurityService(base_type):
            _caddy_ui_multidimensional = True

            @staticmethod
            def _login_dimensions(scope: str, client_ip: str, username: str) -> tuple[tuple[str, str, str], ...]:
                account = username.strip().lower() or "<empty>"
                return (
                    (scope, client_ip, account),
                    (f"{scope}:address", client_ip, "*"),
                    (f"{scope}:account", "*", account),
                )

            def login_state(self, scope: str, client_ip: str, username: str) -> dict[str, Any]:
                states = [
                    super(MultiDimensionSecurityService, self).login_state(dimension_scope, address, account)
                    for dimension_scope, address, account in self._login_dimensions(scope, client_ip, username)
                ]
                return {
                    "allowed": all(bool(state["allowed"]) for state in states),
                    "delay": max(float(state["delay"]) for state in states),
                    "retry_after": max(int(state["retry_after"]) for state in states),
                    "failures": max(int(state["failures"]) for state in states),
                }

            def _record_login_dimension(self, scope: str, client_ip: str, username: str) -> tuple[bool, int]:
                key = self._login_key(scope, client_ip, username)
                now = datetime.now(UTC)
                settings = protection.protection_settings(self.database)["login"]
                with self.database.transaction() as connection:
                    row = connection.execute(
                        "SELECT * FROM login_protection WHERE scope_key=?",
                        (key,),
                    ).fetchone()
                    if row and datetime.fromisoformat(row["last_failure_at"]) >= now - timedelta(
                        seconds=settings["window_seconds"]
                    ):
                        failures = int(row["failures"]) + 1
                        escalation = int(row["escalation"])
                        first_failure = row["first_failure_at"]
                    else:
                        failures = 1
                        escalation = int(row["escalation"]) if row else 0
                        first_failure = now.isoformat()

                    blocked_until = None
                    newly_blocked = False
                    if failures >= settings["block_after"]:
                        durations = (900, 3600, 86400)
                        block_seconds = durations[min(escalation, len(durations) - 1)]
                        blocked_until = (now + timedelta(seconds=block_seconds)).isoformat()
                        newly_blocked = not row or not row["blocked_until"] or datetime.fromisoformat(
                            row["blocked_until"]
                        ) <= now
                        escalation = min(escalation + 1, len(durations) - 1)

                    connection.execute(
                        """INSERT INTO login_protection(scope_key,failures,first_failure_at,last_failure_at,blocked_until,escalation)
                           VALUES(?,?,?,?,?,?) ON CONFLICT(scope_key) DO UPDATE SET failures=excluded.failures,
                           first_failure_at=excluded.first_failure_at,last_failure_at=excluded.last_failure_at,
                           blocked_until=excluded.blocked_until,escalation=excluded.escalation""",
                        (key, failures, first_failure, now.isoformat(), blocked_until, escalation),
                    )
                return newly_blocked, failures

            def record_login_failure(
                self,
                scope: str,
                client_ip: str,
                username: str,
                host: str = "",
            ) -> dict[str, Any]:
                dimensions = self._login_dimensions(scope, client_ip, username)
                primary = super(MultiDimensionSecurityService, self).record_login_failure(
                    *dimensions[0],
                    host,
                )
                extra_blocks: list[tuple[str, int]] = []
                for dimension_scope, address, account in dimensions[1:]:
                    blocked, failures = self._record_login_dimension(dimension_scope, address, account)
                    if blocked:
                        extra_blocks.append((dimension_scope, failures))

                state = self.login_state(scope, client_ip, username)
                if primary["allowed"] and extra_blocks and not state["allowed"]:
                    dimension_scope, failures = extra_blocks[0]
                    self.record_event(
                        "brute_force",
                        "warning",
                        client_ip,
                        host,
                        "/login" if scope == "ui" else "/__caddy_ui_auth/login",
                        f"Login temporarily blocked after {failures} failed attempts.",
                        {
                            "scope": scope,
                            "dimension": dimension_scope.rsplit(":", 1)[-1],
                            "username": username,
                            "block_seconds": state["retry_after"],
                        },
                    )
                return state

            def clear_login(self, scope: str, client_ip: str, username: str) -> None:
                for dimension_scope, address, account in self._login_dimensions(scope, client_ip, username):
                    super(MultiDimensionSecurityService, self).clear_login(dimension_scope, address, account)

        service_type = MultiDimensionSecurityService
        protection.SecurityService = service_type

    enhanced_web = sys.modules.get("caddy_ui.enhanced_web")
    if enhanced_web is not None:
        enhanced_web.SecurityService = service_type
