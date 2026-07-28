namespace CaddyUi.Infrastructure.Security;

public sealed record LoginProtectionDecision(bool Allowed, TimeSpan RetryAfter, string Reason)
{
    public static LoginProtectionDecision Permit { get; } = new(true, TimeSpan.Zero, string.Empty);
}

public sealed class LoginProtectionService
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private readonly AuthenticationStore _store;

    public LoginProtectionService(AuthenticationStore store)
    {
        _store = store;
    }

    public async Task<LoginProtectionDecision> EvaluateAsync(
        string scope,
        string identity,
        string remoteAddress,
        CancellationToken cancellationToken = default)
    {
        var block = await _store.GetActiveLoginBlockAsync(
            scope,
            identity,
            remoteAddress,
            cancellationToken);
        if (block is null)
        {
            return LoginProtectionDecision.Permit;
        }

        var retryAfter = block.ExpiresAt - DateTimeOffset.UtcNow;
        return new LoginProtectionDecision(
            false,
            retryAfter > TimeSpan.Zero ? retryAfter : TimeSpan.FromSeconds(1),
            block.Reason);
    }

    public async Task RecordFailureAsync(
        string scope,
        string identity,
        string remoteAddress,
        string reason,
        CancellationToken cancellationToken = default)
    {
        await _store.RecordLoginAttemptAsync(
            scope,
            identity,
            remoteAddress,
            succeeded: false,
            reason,
            cancellationToken);

        var failures = await _store.CountRecentFailuresAsync(
            scope,
            identity,
            remoteAddress,
            DateTimeOffset.UtcNow.Subtract(Window),
            cancellationToken);
        var duration = failures switch
        {
            >= 40 => TimeSpan.FromHours(24),
            >= 20 => TimeSpan.FromHours(1),
            >= 10 => TimeSpan.FromMinutes(15),
            _ => TimeSpan.Zero,
        };

        if (duration > TimeSpan.Zero)
        {
            await _store.AddLoginBlockAsync(
                scope,
                identity,
                remoteAddress,
                $"Progressive login protection after {failures} failed attempts.",
                DateTimeOffset.UtcNow.Add(duration),
                cancellationToken);
        }
    }

    public async Task RecordSuccessAsync(
        string scope,
        string identity,
        string remoteAddress,
        CancellationToken cancellationToken = default)
    {
        await _store.RecordLoginAttemptAsync(
            scope,
            identity,
            remoteAddress,
            succeeded: true,
            reason: string.Empty,
            cancellationToken);
        await _store.ClearLoginBlocksAsync(
            scope,
            identity,
            remoteAddress,
            cancellationToken);
    }
}
