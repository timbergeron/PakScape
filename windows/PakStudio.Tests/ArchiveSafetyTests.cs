using PakStudio.Core.Validation;
using Xunit;

namespace PakStudio.Tests;

public sealed class ArchiveSafetyTests
{
    [Fact]
    public void LimitsRejectAnAdditionalEntryPastTheMaximum()
    {
        Assert.Throws<ArchiveValidationException>(() =>
            ArchiveSafetyLimits.EnsureEntryCount(
                ArchiveSafetyLimits.MaximumEntryCount + 1,
                "The archive"));
    }

    [Theory]
    [InlineData("con")]
    [InlineData("COM1.txt")]
    [InlineData("readme.txt.")]
    [InlineData("stream:payload")]
    public void WindowsFileNamesRejectUnsafeFilesystemTargets(string name)
    {
        Assert.Throws<ArchiveValidationException>(() => WindowsFileNameValidator.Validate(name));
    }

    [Theory]
    [InlineData("readme.txt")]
    [InlineData("configuration")]
    [InlineData("my archive.pk3")]
    public void WindowsFileNamesAcceptOrdinaryNames(string name)
    {
        WindowsFileNameValidator.Validate(name);
    }
}
