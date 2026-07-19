from __future__ import annotations

from dataclasses import replace
from datetime import UTC, datetime, timedelta
from typing import Any

from . import analytics


_BaseAnalyticsRepository = analytics.AnalyticsRepository
_PERFORMANCE_EXCLUSION = "category NOT IN ('asset','websocket')"


class AnalyticsRepository(_BaseAnalyticsRepository):
    """Adds combined error filtering and performance-aware default analytics semantics."""

    @staticmethod
    def _raw_where(
        filters: analytics.AnalyticsFilters,
        start: datetime,
        end: datetime,
    ) -> tuple[str, list[Any]]:
        if filters.status != "errors":
            return _BaseAnalyticsRepository._raw_where(filters, start, end)
        where, args = _BaseAnalyticsRepository._raw_where(replace(filters, status=""), start, end)
        return f"{where} AND status>=400", args

    def _bucket_where(
        self,
        filters: analytics.AnalyticsFilters,
        start: datetime,
        end: datetime,
    ) -> tuple[str, list[Any]]:
        if filters.status != "errors":
            return _BaseAnalyticsRepository._bucket_where(self, filters, start, end)
        where, args = _BaseAnalyticsRepository._bucket_where(self, replace(filters, status=""), start, end)
        return f"{where} AND status_class IN ('4xx','5xx')", args

    def _raw_summary(
        self,
        filters: analytics.AnalyticsFilters,
        start: datetime,
        end: datetime,
    ) -> dict[str, Any]:
        summary = _BaseAnalyticsRepository._raw_summary(self, filters, start, end)
        if filters.category:
            return summary
        where, args = self._raw_where(filters, start, end)
        performance_where = f"{where} AND {_PERFORMANCE_EXCLUSION}"
        with self.database.connect() as connection:
            row = connection.execute(
                f"""SELECT COUNT(*) requests,COALESCE(AVG(duration_ms),0) avg_ms,
                           COALESCE(MAX(duration_ms),0) max_ms
                    FROM request_events WHERE {performance_where}""",
                args,
            ).fetchone()
            count = int(row["requests"] or 0)
            summary["avg_ms"] = float(row["avg_ms"] or 0)
            summary["max_ms"] = float(row["max_ms"] or 0)
            summary["p50_ms"] = self._raw_percentile(connection, performance_where, args, count, 0.50)
            summary["p95_ms"] = self._raw_percentile(connection, performance_where, args, count, 0.95)
            summary["p99_ms"] = self._raw_percentile(connection, performance_where, args, count, 0.99)
        return summary

    def _bucket_summary(
        self,
        filters: analytics.AnalyticsFilters,
        start: datetime,
        end: datetime,
    ) -> dict[str, Any]:
        summary = _BaseAnalyticsRepository._bucket_summary(self, filters, start, end)
        if filters.category:
            return summary
        where, args = self._bucket_where(filters, start, end)
        with self.database.connect() as connection:
            row = connection.execute(
                f"""SELECT COALESCE(SUM(requests),0) requests,
                           COALESCE(SUM(duration_sum_ms),0) duration_sum_ms,
                           COALESCE(MAX(duration_max_ms),0) max_ms,
                           COALESCE(SUM(lt_100),0) lt_100,COALESCE(SUM(lt_250),0) lt_250,
                           COALESCE(SUM(lt_500),0) lt_500,COALESCE(SUM(lt_1000),0) lt_1000,
                           COALESCE(SUM(lt_3000),0) lt_3000,COALESCE(SUM(lt_10000),0) lt_10000,
                           COALESCE(SUM(ge_10000),0) ge_10000
                    FROM analytics_buckets WHERE {where} AND {_PERFORMANCE_EXCLUSION}""",
                args,
            ).fetchone()
        count = int(row["requests"] or 0)
        histogram = [
            (100.0, int(row["lt_100"] or 0)),
            (250.0, int(row["lt_250"] or 0)),
            (500.0, int(row["lt_500"] or 0)),
            (1000.0, int(row["lt_1000"] or 0)),
            (3000.0, int(row["lt_3000"] or 0)),
            (10000.0, int(row["lt_10000"] or 0)),
            (float(row["max_ms"] or 10000), int(row["ge_10000"] or 0)),
        ]
        summary["avg_ms"] = float(row["duration_sum_ms"] or 0) / count if count else 0.0
        summary["max_ms"] = float(row["max_ms"] or 0)
        summary["p50_ms"] = self._histogram_percentile(histogram, count, 0.50)
        summary["p95_ms"] = self._histogram_percentile(histogram, count, 0.95)
        summary["p99_ms"] = self._histogram_percentile(histogram, count, 0.99)
        return summary

    def series(
        self,
        filters: analytics.AnalyticsFilters,
        start: datetime,
        end: datetime,
    ) -> list[dict[str, Any]]:
        series = _BaseAnalyticsRepository.series(self, filters, start, end)
        if filters.category or not series:
            return series
        duration = end - start
        performance: dict[str, float] = {}
        if duration <= timedelta(hours=6):
            where, args = self._raw_where(filters, start, end)
            with self.database.connect() as connection:
                rows = connection.execute(
                    f"""SELECT substr(occurred_at,1,16) bucket,COALESCE(AVG(duration_ms),0) avg_ms
                        FROM request_events WHERE {where} AND {_PERFORMANCE_EXCLUSION}
                        GROUP BY bucket ORDER BY bucket""",
                    args,
                ).fetchall()
            performance = {str(row["bucket"]): float(row["avg_ms"] or 0) for row in rows}
        elif duration <= timedelta(days=30):
            where, args = self._bucket_where(filters, start, end)
            with self.database.connect() as connection:
                rows = connection.execute(
                    f"""SELECT substr(bucket_start,1,13) bucket,
                               CASE WHEN SUM(requests)>0 THEN SUM(duration_sum_ms)/SUM(requests) ELSE 0 END avg_ms
                        FROM analytics_buckets WHERE granularity='hour' AND {where} AND {_PERFORMANCE_EXCLUSION}
                        GROUP BY bucket ORDER BY bucket""",
                    args,
                ).fetchall()
            performance = {str(row["bucket"]): float(row["avg_ms"] or 0) for row in rows}
        else:
            where, args = self._bucket_where(filters, start, end)
            with self.database.connect() as connection:
                rows = connection.execute(
                    f"""SELECT substr(bucket_start,1,10) bucket,
                               CASE WHEN SUM(requests)>0 THEN SUM(duration_sum_ms)/SUM(requests) ELSE 0 END avg_ms
                        FROM analytics_buckets WHERE {where} AND {_PERFORMANCE_EXCLUSION}
                        GROUP BY bucket ORDER BY bucket""",
                    args,
                ).fetchall()
            performance = {str(row["bucket"]): float(row["avg_ms"] or 0) for row in rows}
        for item in series:
            item["avg_ms"] = performance.get(str(item["bucket"]), 0.0)
        return series

    def top(
        self,
        column: str,
        filters: analytics.AnalyticsFilters,
        start: datetime,
        end: datetime,
        limit: int = 10,
    ) -> list[tuple[str, int, float]]:
        if column != "endpoint" or filters.category:
            return _BaseAnalyticsRepository.top(self, column, filters, start, end, limit)
        raw_cutoff = datetime.now(UTC) - timedelta(days=analytics.analytics_settings(self.database)["raw_retention_days"])
        if start < raw_cutoff:
            where, args = self._bucket_where(filters, start, end)
            with self.database.connect() as connection:
                rows = connection.execute(
                    f"""SELECT endpoint label,SUM(requests) requests,
                               CASE WHEN SUM(requests)>0 THEN SUM(duration_sum_ms)/SUM(requests) ELSE 0 END avg_ms
                        FROM analytics_buckets WHERE {where} AND {_PERFORMANCE_EXCLUSION}
                        GROUP BY endpoint ORDER BY requests DESC,label LIMIT ?""",
                    (*args, min(max(limit, 1), 100)),
                ).fetchall()
        else:
            where, args = self._raw_where(filters, start, end)
            with self.database.connect() as connection:
                rows = connection.execute(
                    f"""SELECT endpoint label,COUNT(*) requests,AVG(duration_ms) avg_ms
                        FROM request_events WHERE {where} AND {_PERFORMANCE_EXCLUSION} AND endpoint<>''
                        GROUP BY endpoint ORDER BY requests DESC,label LIMIT ?""",
                    (*args, min(max(limit, 1), 100)),
                ).fetchall()
        return [(str(row["label"]), int(row["requests"]), float(row["avg_ms"] or 0)) for row in rows]

    def slow_endpoints(
        self,
        filters: analytics.AnalyticsFilters,
        start: datetime,
        end: datetime,
        limit: int = 10,
    ) -> list[tuple[str, int, float]]:
        where, args = self._raw_where(filters, start, end)
        exclusion = "" if filters.category else f" AND {_PERFORMANCE_EXCLUSION}"
        with self.database.connect() as connection:
            rows = connection.execute(
                f"""SELECT endpoint,COUNT(*) requests,AVG(duration_ms) avg_ms
                    FROM request_events WHERE {where}{exclusion}
                    GROUP BY endpoint HAVING COUNT(*)>=1 ORDER BY avg_ms DESC LIMIT ?""",
                (*args, min(max(limit, 1), 100)),
            ).fetchall()
        return [(str(row["endpoint"]), int(row["requests"]), float(row["avg_ms"] or 0)) for row in rows]


def install() -> None:
    """Expose the enhanced repository through the canonical analytics module."""
    analytics.AnalyticsRepository = AnalyticsRepository
