namespace PakStudio.Core.Pathing;

public static class ArchiveSearch
{
    /// <summary>Matches a whole name or path using case-insensitive * and ? wildcards.</summary>
    public static bool MatchesWildcard(string pattern, string value)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(value);

        var patternIndex = 0;
        var valueIndex = 0;
        var lastStar = -1;
        var retryValueIndex = 0;

        // Retry only the most recent star. Regex backtracking can explore an
        // exponential number of alternatives and throw from the search box.
        while (valueIndex < value.Length)
        {
            if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                lastStar = patternIndex++;
                retryValueIndex = valueIndex;
            }
            else if (patternIndex < pattern.Length &&
                     (pattern[patternIndex] == '?' ||
                      char.ToUpperInvariant(pattern[patternIndex]) == char.ToUpperInvariant(value[valueIndex])))
            {
                patternIndex++;
                valueIndex++;
            }
            else if (lastStar >= 0)
            {
                patternIndex = lastStar + 1;
                valueIndex = ++retryValueIndex;
            }
            else
            {
                return false;
            }
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
        {
            patternIndex++;
        }
        return patternIndex == pattern.Length;
    }
}
