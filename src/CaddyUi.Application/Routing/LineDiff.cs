namespace CaddyUi.Application.Routing;

public enum DiffLineKind
{
    Unchanged,
    Added,
    Removed,
}

public sealed record DiffLine(DiffLineKind Kind, string Text, int? OldLine, int? NewLine);

public static class LineDiff
{
    public static IReadOnlyList<DiffLine> Create(string? previous, string? candidate)
    {
        var left = Normalize(previous).Split('\n');
        var right = Normalize(candidate).Split('\n');
        var lengths = new int[left.Length + 1, right.Length + 1];
        for (var leftIndex = left.Length - 1; leftIndex >= 0; leftIndex--)
        {
            for (var rightIndex = right.Length - 1; rightIndex >= 0; rightIndex--)
            {
                lengths[leftIndex, rightIndex] = string.Equals(left[leftIndex], right[rightIndex], StringComparison.Ordinal)
                    ? lengths[leftIndex + 1, rightIndex + 1] + 1
                    : Math.Max(lengths[leftIndex + 1, rightIndex], lengths[leftIndex, rightIndex + 1]);
            }
        }

        var result = new List<DiffLine>();
        var i = 0;
        var j = 0;
        var oldLine = 1;
        var newLine = 1;
        while (i < left.Length && j < right.Length)
        {
            if (string.Equals(left[i], right[j], StringComparison.Ordinal))
            {
                result.Add(new DiffLine(DiffLineKind.Unchanged, left[i], oldLine++, newLine++));
                i++;
                j++;
            }
            else if (lengths[i + 1, j] >= lengths[i, j + 1])
            {
                result.Add(new DiffLine(DiffLineKind.Removed, left[i++], oldLine++, null));
            }
            else
            {
                result.Add(new DiffLine(DiffLineKind.Added, right[j++], null, newLine++));
            }
        }

        while (i < left.Length)
        {
            result.Add(new DiffLine(DiffLineKind.Removed, left[i++], oldLine++, null));
        }

        while (j < right.Length)
        {
            result.Add(new DiffLine(DiffLineKind.Added, right[j++], null, newLine++));
        }

        return result;
    }

    private static string Normalize(string? value)
    {
        return (value ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
    }
}
