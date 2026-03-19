namespace PakStudio.Core.Pathing;

public static class PathHelper
{
    public static string NormalizeArchivePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var normalized = path.Replace('\\', '/').Trim();
        var segments = normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return string.Join('/', segments);
    }

    public static string NormalizeArchiveSegment(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Archive names cannot be empty.", nameof(name));
        }

        var normalized = name.Replace('\\', '/').Trim();
        if (normalized.Contains('/'))
        {
            throw new ArgumentException("Archive names cannot contain path separators.", nameof(name));
        }

        return normalized;
    }

    public static IReadOnlyList<string> SplitArchivePath(string? path)
    {
        var normalized = NormalizeArchivePath(path);
        if (string.IsNullOrEmpty(normalized))
        {
            return Array.Empty<string>();
        }

        return normalized.Split('/');
    }

    public static string CombineArchivePath(string parentPath, string name)
    {
        var normalizedParent = NormalizeArchivePath(parentPath);
        var normalizedName = NormalizeArchiveSegment(name);

        if (string.IsNullOrEmpty(normalizedParent))
        {
            return "/" + normalizedName;
        }

        return "/" + normalizedParent + "/" + normalizedName;
    }

    public static string ToRelativeArchivePath(string absoluteOrRelativePath)
    {
        return NormalizeArchivePath(absoluteOrRelativePath);
    }
}
