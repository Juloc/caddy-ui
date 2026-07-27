from __future__ import annotations

from datetime import UTC, datetime, timedelta

from .. import analytics


def install() -> None:
    """Install lossless compaction on top of the currently active analytics repository."""
    base_repository = analytics.AnalyticsRepository

    class AnalyticsRepository(base_repository):
        def compact(self) -> None:
            settings = analytics.analytics_settings(self.database)
            now = datetime.now(UTC)
            raw_cutoff = now - timedelta(days=settings["raw_retention_days"])
            day_cutoff = raw_cutoff.replace(hour=0, minute=0, second=0, microsecond=0)
            aggregate_cutoff = now - timedelta(days=settings["aggregate_retention_days"])

            with self.database.transaction() as connection:
                rows = connection.execute(
                    """
                    SELECT substr(bucket_start,1,10) || 'T00:00:00+00:00' bucket_start,
                           host,endpoint,method,status_class,client_type,category,
                           SUM(requests) requests,SUM(bytes_sent) bytes_sent,
                           SUM(duration_sum_ms) duration_sum_ms,MAX(duration_max_ms) duration_max_ms,
                           SUM(lt_100) lt_100,SUM(lt_250) lt_250,SUM(lt_500) lt_500,
                           SUM(lt_1000) lt_1000,SUM(lt_3000) lt_3000,
                           SUM(lt_10000) lt_10000,SUM(ge_10000) ge_10000
                    FROM analytics_buckets
                    WHERE granularity='hour' AND bucket_start<?
                    GROUP BY substr(bucket_start,1,10),host,endpoint,method,status_class,client_type,category
                    """,
                    (day_cutoff.isoformat(),),
                ).fetchall()

                for row in rows:
                    connection.execute(
                        """
                        INSERT INTO analytics_buckets(
                            bucket_start,granularity,host,endpoint,method,status_class,client_type,category,
                            requests,bytes_sent,duration_sum_ms,duration_max_ms,
                            lt_100,lt_250,lt_500,lt_1000,lt_3000,lt_10000,ge_10000
                        ) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
                        ON CONFLICT(bucket_start,granularity,host,endpoint,method,status_class,client_type,category)
                        DO UPDATE SET
                            requests=analytics_buckets.requests+excluded.requests,
                            bytes_sent=analytics_buckets.bytes_sent+excluded.bytes_sent,
                            duration_sum_ms=analytics_buckets.duration_sum_ms+excluded.duration_sum_ms,
                            duration_max_ms=MAX(analytics_buckets.duration_max_ms,excluded.duration_max_ms),
                            lt_100=analytics_buckets.lt_100+excluded.lt_100,
                            lt_250=analytics_buckets.lt_250+excluded.lt_250,
                            lt_500=analytics_buckets.lt_500+excluded.lt_500,
                            lt_1000=analytics_buckets.lt_1000+excluded.lt_1000,
                            lt_3000=analytics_buckets.lt_3000+excluded.lt_3000,
                            lt_10000=analytics_buckets.lt_10000+excluded.lt_10000,
                            ge_10000=analytics_buckets.ge_10000+excluded.ge_10000
                        """,
                        (
                            row["bucket_start"],
                            "day",
                            row["host"],
                            row["endpoint"],
                            row["method"],
                            row["status_class"],
                            row["client_type"],
                            row["category"],
                            row["requests"],
                            row["bytes_sent"],
                            row["duration_sum_ms"],
                            row["duration_max_ms"],
                            row["lt_100"],
                            row["lt_250"],
                            row["lt_500"],
                            row["lt_1000"],
                            row["lt_3000"],
                            row["lt_10000"],
                            row["ge_10000"],
                        ),
                    )

                connection.execute("DELETE FROM request_events WHERE occurred_at<?", (raw_cutoff.isoformat(),))
                connection.execute(
                    "DELETE FROM analytics_buckets WHERE granularity='hour' AND bucket_start<?",
                    (day_cutoff.isoformat(),),
                )
                connection.execute("DELETE FROM analytics_buckets WHERE bucket_start<?", (aggregate_cutoff.isoformat(),))
                result = connection.execute("PRAGMA integrity_check").fetchone()[0]
                if result != "ok":
                    raise RuntimeError(f"Analytics compaction integrity check failed: {result}")

    analytics.AnalyticsRepository = AnalyticsRepository
