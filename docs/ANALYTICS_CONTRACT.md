# Analytics contract

## Operational statistics

The application retains per-host request counts, bot counts/share, HTTP errors and transferred bytes. It does not collect visitor analytics, pageviews, sessions, page-load groups, route performance distributions or historical individual requests.

`AnalyticsIngestionWorker` is the single ingestion owner. It reads Caddy's structured access log and classifies each event once. Query strings, unused headers and raw JSON are discarded. Retained fields are bounded. Caddy's input files remain subject to Caddy's own log rotation; the UI does not change that source logging policy.

`AnalyticsIngestionStore` groups counters into five-minute buckets and commits a batch with its source checkpoint in one transaction. Reads use these compact buckets, rather than scanning raw request tables. Period boundaries include the overlapping five-minute bucket. Normal ingestion flushes every 30 seconds, or sooner at 5,000 events. An idle or missing source does not poll PostgreSQL for its checkpoint. File reads are capped at 8 MiB plus at most one complete line, with a 4 MiB individual-line limit.

Source identity uses the opened file's device/inode on Linux and volume/file ID on Windows, plus its first complete line. Appends do not change identity; replacement and truncation restart the source position. Each source is transactionally locked during persistence, and committed offsets suppress complete or overlapping replay. On processing failure the worker discards its speculative position and resumes from the durable checkpoint. Shutdown attempts a bounded final flush; unfinished data is replayed on restart.

## Live request details

`AnalyticsLiveStream` holds request details only in process memory while authenticated viewers are connected. It is bounded to 1,000 rows and 64 viewers. Individual connections have a 20-second renewable lease. Disconnecting the last viewer, or expiry of abandoned viewers, clears the buffer. There is no persistent raw-request queue.

The authorized `/events/live` stream reads this buffer without database polling. Sequence cursors handle equal event timestamps and bounded replay on reconnect. A restarted process accepts an older cursor by starting at its available buffer. Hidden tabs and pages in the browser back/forward cache disconnect; returning reconnects. Live details from periods with no viewer cannot be recovered from the application.

## UI refresh

Normal traffic and security views render on the server and optionally refresh their read-only snapshot. The interval defaults to 30 seconds, is adjustable from 5 to 300 seconds, and can be paused with 0. The browser remembers the preference. Hidden/inactive pages stop requests, overlapping fetches are prevented, requests time out after 10 seconds, errors use bounded backoff, and a focused snapshot is not replaced. Navigation, filters, settings and manual reload remain functional without JavaScript.

## Security and DDNS

DDNS and Caddy request-guard enforcement operate independently of UI visibility. Risk scoring and IP discovery use compact per-client minute buckets: request/error/scanner/method counts, timing moments, at most 64 distinct path hashes, at most 8 host names, and the latest technical address/user-agent. No raw request paths are retained as normal security history. The set bounds exceed the engine's path/host detection thresholds.

Evidence is retained for the configured risk/discovery window, plus the maintenance interval. Security assessments retain their reasons and corresponding security events. Existing manual blocking, exact-address enforcement, authorization and CSRF boundaries remain in place. See [IP security contract](IP_SECURITY_CONTRACT.md).

## Retention

Defaults for newly collected data:

- Traffic/bot statistics: 90 days.
- Security events, assessments and processing failures: 30 days.
- Compact security evidence: the larger of the risk/discovery windows plus one minute; default 61 minutes.
- Maintenance: every 15 minutes.

Administrators can set statistics and security retention from 1 to 3,650 days on the System page. Changes are validated server-side, authorized and protected by antiforgery. Values live in PostgreSQL; maintenance reads them when it runs. Unreferenced technical identities created after the cutover are pruned in bounded batches after their security retention expires. Defaults can be supplied through `Analytics:StatisticsRetentionDays`, `Analytics:SecurityEventRetentionDays`, `Analytics:FlushIntervalSeconds` and `Analytics:MaintenanceIntervalMinutes` configuration.

## Existing history, upgrade and rollback

The lightweight migration creates new compact tables and records its cutover timestamp. Existing raw requests, visitor/session/page-load tables, old aggregates and pre-cutover security records are preserved as an archive. The supported SQLite importer can still import that history. Archived tables are not an alternative active analytics pipeline and are not queried or written by normal analytics pages or workers.

Before production upgrade, take and verify a PostgreSQL backup, preserve Caddy access logs, and record the prior application image/version. Validate the migration on a restored copy first. No production deployment is performed merely by merging the code.

An empty lightweight schema can be migrated down and back up while preserving the archive. Once new statistics or security evidence exist, the down migration refuses to drop them. Export those tables and settings and restore the verified pre-upgrade backup before rolling the application back. Keep the export with the backup so the newer records remain recoverable. Do not discard the live checkpoint or switch between ingestion implementations against the same running database.
