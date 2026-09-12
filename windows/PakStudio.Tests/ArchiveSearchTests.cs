using PakStudio.Core.Pathing;
using Xunit;

namespace PakStudio.Tests;

public sealed class ArchiveSearchTests
{
    [Theory]
    [InlineData("*.BSP", "start.bsp", true)]
    [InlineData("maps/e?m?.bsp", "maps/E1M2.BSP", true)]
    [InlineData("maps/*.bsp", "maps/episode/start.bsp", true)]
    [InlineData("e?m?.bsp", "e1m10.bsp", false)]
    [InlineData("start.*", "restart.bsp", false)]
    [InlineData("*a?b", "aaacb", true)]
    [InlineData("*a?b", "aaacbc", false)]
    [InlineData("file[1].*", "file[1].txt", true)]
    [InlineData("file[1].*", "file1.txt", false)]
    [InlineData("***", "", true)]
    [InlineData("?", "", false)]
    [InlineData("", "", true)]
    [InlineData("", "x", false)]
    public void WildcardsMatchWholeNamesAndPaths(string pattern, string value, bool expected)
    {
        Assert.Equal(expected, ArchiveSearch.MatchesWildcard(pattern, value));
    }

    [Fact]
    public void RepeatedWildcardsDoNotCauseRegexBacktrackingTimeouts()
    {
        var pattern = string.Concat(Enumerable.Repeat("*a", 32)) + "b";
        var value = new string('a', 256);

        Assert.False(ArchiveSearch.MatchesWildcard(pattern, value));
        Assert.True(ArchiveSearch.MatchesWildcard(pattern, value + "b"));
    }
}
