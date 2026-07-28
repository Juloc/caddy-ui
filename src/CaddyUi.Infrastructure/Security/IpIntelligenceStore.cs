using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Net;
using CaddyUi.Application.Security;
using CaddyUi.Domain.Security;
using CaddyUi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaddyUi.Infrastructure.Security;

public sealed record IpIntelligenceSnapshot(
    IpIntelligenceResult? Result,
    bool Pending,
    bool Stale);

public sealed record IpRefreshRequest(
    IPAddress Address,
    int Attempt);

public sealed class IpIntelligenceStore
{
    private readonly IDbContextFactory<CaddyUiDbContext> _contextFactory;
    private readonly IpAddressClassifier _classifier;
    private readonly IpSecurityOptions _options;
    private readonly TimeProvider _timeProvider;

    public IpIntelligenceStore(
        IDbContextFactory<CaddyUiDbContext> contextFactory,
        IpAddressClassifier classifier,
        IpSecurityOptions options,
        TimeProvider timeProvider)
    {
        _contextFactory = contextFactory;
        _classifier = classifier;
        _options = options;
        _timeProvider = timeProvider;
    }

    public async Task<IpIntelligenceSnapshot> GetOrQueueAsync(
        IPAddress address,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        var classification = _classifier.Classify(address);
        var now = _timeProvider.GetUtcNow();
        if (!classification.ExternalLookupAllowed)
        {
            var local = new IpIntelligenceResult(
                classification.Address,
                classification.Scope,
                true,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                "local",
                now,
                now.AddHours(_options.SuccessCacheHours),
                string.Empty,
                "{}");
            await SaveResultAsync(local, cancellationToken);
            return new IpIntelligenceSnapshot(local, false, false);
        }

        var cached = await ReadAsync(classification.Address, cancellationToken);
        if (cached is not null && cached.ExpiresAt > now)
        {
            return new IpIntelligenceSnapshot(cached, false, false);
        }

        if (_options.IntelligenceEnabled)
        {
            await QueueAsync(classification.Address, now, cancellationToken);
        }

        return new IpIntelligenceSnapshot(
            cached,
            _options.IntelligenceEnabled,
            cached is not null);
    }

    public async Task DiscoverRecentAddressesAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_options.IntelligenceEnabled)
        {
            return;
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO caddy_ui.ip_intelligence_refresh_queue(
                address, requested_at, not_before, attempt, last_error)
            SELECT DISTINCT requests.remote_address,
                   @now,
                   @now,
                   0,
                   ''
            FROM caddy_ui.request_events AS requests
            LEFT JOIN caddy_ui.ip_intelligence_cache AS cache
              ON cache.address = requests.remote_address
            LEFT JOIN caddy_ui.ip_intelligence_refresh_queue AS queue
              ON queue.address = requests.remote_address
            WHERE requests.remote_address IS NOT NULL
              AND requests.occurred_at >= @cutoff
              AND queue.address IS NULL
              AND (cache.address IS NULL OR cache.expires_at <= @now)
            ON CONFLICT (address) DO NOTHING
            """;
        var now = _timeProvider.GetUtcNow();
        AddParameter(command, "now", now);
        AddParameter(command, "cutoff", now.AddMinutes(-_options.DiscoveryLookbackMinutes));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<IpRefreshRequest>> ListReadyAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT address, attempt
            FROM caddy_ui.ip_intelligence_refresh_queue
            WHERE not_before <= @now
            ORDER BY requested_at, address
            LIMIT @limit
            """;
        AddParameter(command, "now", _timeProvider.GetUtcNow());
        AddParameter(command, "limit", _options.RefreshBatchSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<IpRefreshRequest>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var address = ReadAddress(reader, 0);
            if (address is not null)
            {
                result.Add(new IpRefreshRequest(address, reader.GetInt32(1)));
            }
        }

        return result;
    }

    public async Task CompleteAsync(
        IpIntelligenceResult result,
        int previousAttempt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await SaveResultAsync(connection, transaction, result, cancellationToken);
            if (result.Available || result.Source == "local")
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    "DELETE FROM caddy_ui.ip_intelligence_refresh_queue WHERE address = @address",
                    command => AddParameter(command, "address", result.Address),
                    cancellationToken);
            }
            else
            {
                var nextAttempt = previousAttempt + 1;
                var delayMinutes = Math.Min(
                    24 * 60,
                    _options.FailureCacheMinutes * (int)Math.Pow(2, Math.Min(nextAttempt - 1, 7)));
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    UPDATE caddy_ui.ip_intelligence_refresh_queue
                    SET attempt = @attempt,
                        not_before = @not_before,
                        last_error = @last_error
                    WHERE address = @address
                    """,
                    command =>
                    {
                        AddParameter(command, "attempt", nextAttempt);
                        AddParameter(command, "not_before", _timeProvider.GetUtcNow().AddMinutes(delayMinutes));
                        AddParameter(command, "last_error", result.Error);
                        AddParameter(command, "address", result.Address);
                    },
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task SaveResultAsync(
        IpIntelligenceResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SaveResultAsync(connection, transaction, result, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<IpIntelligenceResult?> ReadAsync(
        IPAddress address,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT address, scope, available, asn, prefix::text, holder,
                   registry, source, refreshed_at, expires_at, error,
                   payload_json::text
            FROM caddy_ui.ip_intelligence_cache
            WHERE address = @address
            """;
        AddParameter(command, "address", address);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var storedAddress = ReadAddress(reader, 0);
        return storedAddress is null
            ? null
            : new IpIntelligenceResult(
                storedAddress,
                ParseScope(reader.GetString(1)),
                reader.GetBoolean(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                ReadTimestamp(reader, 8),
                ReadTimestamp(reader, 9),
                reader.GetString(10),
                reader.GetString(11));
    }

    private static Task SaveResultAsync(
        DbConnection connection,
        DbTransaction transaction,
        IpIntelligenceResult result,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO caddy_ui.ip_intelligence_cache(
                address, scope, available, asn, prefix, holder, registry,
                source, payload_json, error, refreshed_at, expires_at,
                failure_count, last_error_at)
            VALUES(
                @address, @scope, @available, @asn,
                CASE WHEN @prefix = '' THEN NULL ELSE CAST(@prefix AS cidr) END,
                @holder, @registry, @source, CAST(@payload_json AS jsonb),
                @error, @refreshed_at, @expires_at,
                @failure_increment,
                CASE WHEN @failure_increment = 1 THEN @refreshed_at ELSE NULL END)
            ON CONFLICT (address)
            DO UPDATE SET
                scope = EXCLUDED.scope,
                available = EXCLUDED.available,
                asn = EXCLUDED.asn,
                prefix = EXCLUDED.prefix,
                holder = EXCLUDED.holder,
                registry = EXCLUDED.registry,
                source = EXCLUDED.source,
                payload_json = EXCLUDED.payload_json,
                error = EXCLUDED.error,
                refreshed_at = EXCLUDED.refreshed_at,
                expires_at = EXCLUDED.expires_at,
                failure_count = CASE
                    WHEN EXCLUDED.available THEN 0
                    ELSE caddy_ui.ip_intelligence_cache.failure_count + 1
                END,
                last_error_at = CASE
                    WHEN EXCLUDED.available THEN NULL
                    ELSE EXCLUDED.refreshed_at
                END
            """,
            command =>
            {
                AddParameter(command, "address", result.Address);
                AddParameter(command, "scope", result.Scope.ToStorageValue());
                AddParameter(command, "available", result.Available);
                AddParameter(command, "asn", result.Asn);
                AddParameter(command, "prefix", result.Prefix);
                AddParameter(command, "holder", result.Holder);
                AddParameter(command, "registry", result.Registry);
                AddParameter(command, "source", result.Source);
                AddParameter(command, "payload_json", result.PayloadJson);
                AddParameter(command, "error", result.Error);
                AddParameter(command, "refreshed_at", result.FetchedAt);
                AddParameter(command, "expires_at", result.ExpiresAt);
                AddParameter(command, "failure_increment", result.Available ? 0 : 1);
            },
            cancellationToken);
    }

    private async Task QueueAsync(
        IPAddress address,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO caddy_ui.ip_intelligence_refresh_queue(
                address, requested_at, not_before, attempt, last_error)
            VALUES(@address, @now, @now, 0, '')
            ON CONFLICT (address)
            DO UPDATE SET requested_at = LEAST(
                caddy_ui.ip_intelligence_refresh_queue.requested_at,
                EXCLUDED.requested_at)
            """;
        AddParameter(command, "address", address);
        AddParameter(command, "now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IpAddressScope ParseScope(string value)
    {
        return value switch
        {
            "public" => IpAddressScope.Public,
            "private" => IpAddressScope.Private,
            "loopback" => IpAddressScope.Loopback,
            "link-local" => IpAddressScope.LinkLocal,
            "multicast" => IpAddressScope.Multicast,
            "documentation" => IpAddressScope.Documentation,
            "shared" => IpAddressScope.Shared,
            "benchmark" => IpAddressScope.Benchmark,
            "reserved" => IpAddressScope.Reserved,
            "unspecified" => IpAddressScope.Unspecified,
            _ => IpAddressScope.Reserved,
        };
    }

    private static IPAddress? ReadAddress(DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value switch
        {
            IPAddress address => IpAddressClassifier.Normalize(address),
            string text when IPAddress.TryParse(text.Split('/')[0], out var address) =>
                IpAddressClassifier.Normalize(address),
            _ => null,
        };
    }

    private static DateTimeOffset ReadTimestamp(DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTimeOffset timestamp => timestamp,
            DateTime timestamp => new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)),
            _ => DateTimeOffset.Parse(
                Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
                CultureInfo.InvariantCulture),
        };
    }

    private static async Task<DbConnection> OpenConnectionAsync(
        CaddyUiDbContext context,
        CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        return connection;
    }

    private static async Task ExecuteAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        Action<DbCommand> bind,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        bind(command);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
