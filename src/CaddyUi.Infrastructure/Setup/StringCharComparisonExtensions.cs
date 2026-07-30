namespace CaddyUi.Infrastructure.Setup;

internal static class StringCharComparisonExtensions
{
    public static bool StartsWith(
        this string value,
        char prefix,
        StringComparison comparisonType)
    {
        return value.StartsWith(prefix.ToString(), comparisonType);
    }

    public static bool EndsWith(
        this string value,
        char suffix,
        StringComparison comparisonType)
    {
        return value.EndsWith(suffix.ToString(), comparisonType);
    }
}
