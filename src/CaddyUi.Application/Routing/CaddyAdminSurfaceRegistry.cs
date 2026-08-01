namespace CaddyUi.Application.Routing;

public static class CaddyAdminSurfaceRegistry
{
    private static string _publicOrigin = string.Empty;

    public static string PublicOrigin => Volatile.Read(ref _publicOrigin);

    public static void Configure(string? publicOrigin)
    {
        Interlocked.Exchange(
            ref _publicOrigin,
            string.IsNullOrWhiteSpace(publicOrigin) ? string.Empty : publicOrigin.Trim());
    }
}
