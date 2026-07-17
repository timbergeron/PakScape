namespace PakStudio.Core.Validation;

public static class ArchiveSafetyLimits
{
    public const int MaximumEntryCount = 50_000;
    public const int MaximumPathDepth = 256;
    public const long MaximumFileSize = 1L * 1024 * 1024 * 1024;
    public const long MaximumTotalSize = 2L * 1024 * 1024 * 1024;

    public static void EnsureEntryCount(int count, string description)
    {
        if (count < 0 || count > MaximumEntryCount)
        {
            throw new ArchiveValidationException(
                $"{description} contains more than {MaximumEntryCount:N0} entries.");
        }
    }

    public static void EnsureFileSize(long length, string description)
    {
        if (length < 0 || length > MaximumFileSize)
        {
            throw new ArchiveValidationException(
                $"{description} exceeds the 1 GiB per-file safety limit.");
        }
    }

    public static void EnsurePathDepth(int depth, string description)
    {
        if (depth < 1 || depth > MaximumPathDepth)
        {
            throw new ArchiveValidationException(
                $"{description} exceeds the {MaximumPathDepth}-component path-depth safety limit.");
        }
    }

    public static void EnsureTotalSize(long currentSize, long additionalSize, string description)
    {
        if (currentSize < 0 || additionalSize < 0 ||
            currentSize > MaximumTotalSize - additionalSize)
        {
            throw new ArchiveValidationException(
                $"{description} exceeds the 2 GiB total-size safety limit.");
        }
    }
}
