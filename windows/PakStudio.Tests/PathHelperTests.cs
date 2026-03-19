using PakStudio.Core.Pathing;
using Xunit;

namespace PakStudio.Tests;

public sealed class PathHelperTests
{
    [Fact]
    public void NormalizeArchivePath_TrimsAndNormalizesSeparators()
    {
        var normalized = PathHelper.NormalizeArchivePath(@"\\maps// e1m1.bsp ");

        Assert.Equal("maps/e1m1.bsp", normalized);
    }

    [Fact]
    public void CombineArchivePath_ProducesAbsoluteStyleArchivePath()
    {
        var combined = PathHelper.CombineArchivePath("/", "maps");

        Assert.Equal("/maps", combined);
    }
}
