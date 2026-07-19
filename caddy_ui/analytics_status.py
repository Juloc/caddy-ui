from __future__ import annotations

from dataclasses import replace
from datetime import datetime
from typing import Any

from . import analytics


class AnalyticsRepository(analytics.AnalyticsRepository):
    """Extends the base repository with the synthetic combined `errors` status filter."""

    @staticmethod
    def _raw_where(
        filters: analytics.AnalyticsFilters,
        start: datetime,
        end: datetime,
    ) -> tuple[str, list[Any]]:
        if filters.status != "errors":
            return analytics.AnalyticsRepository._raw_where(filters, start, end)
        where, args = analytics.AnalyticsRepository._raw_where(replace(filters, status=""), start, end)
        return f"{where} AND status>=400", args

    def _bucket_where(
        self,
        filters: analytics.AnalyticsFilters,
        start: datetime,
        end: datetime,
    ) -> tuple[str, list[Any]]:
        if filters.status != "errors":
            return super()._bucket_where(filters, start, end)
        where, args = super()._bucket_where(replace(filters, status=""), start, end)
        return f"{where} AND status_class IN ('4xx','5xx')", args


def install() -> None:
    """Expose the enhanced repository through the canonical analytics module."""
    analytics.AnalyticsRepository = AnalyticsRepository
