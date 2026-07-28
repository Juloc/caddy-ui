using System.Globalization;

namespace CaddyUi.Application.Analytics;

public static class PathCardinalityNormalizer
{
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/")
        {
            return "/";
        }

        var segments = path
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeSegment);
        var result = $"/{string.Join('/', segments)}";
        return result.Length <= 256 ? result : result[..256];
    }

    private static string NormalizeSegment(string segment)
    {
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(segment);
        }
        catch (UriFormatException)
        {
            return segment;
        }
        if (Guid.TryParse(decoded, out _))
        {
            return "{guid}";
        }

        if (long.TryParse(decoded, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return "{number}";
        }

        if (decoded.Length >= 16 &&
            decoded.All(character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '-' or '_'))
        {
            return "{token}";
        }

        return segment;
    }
}
