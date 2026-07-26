namespace LumenMedia.Application.Common;

/// <summary>Escapes user input for use inside SQL LIKE patterns (not SQLi — EF parameterizes).</summary>
public static class LikePattern
{
    public static string Contains(string term)
    {
        var escaped = term
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
        return $"%{escaped}%";
    }

    public const string EscapeChar = "\\";
}
