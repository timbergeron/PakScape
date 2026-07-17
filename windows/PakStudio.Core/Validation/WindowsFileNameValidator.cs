namespace PakStudio.Core.Validation;

public static class WindowsFileNameValidator
{
    private static readonly HashSet<string> ReservedBaseNames = new(
        [
            "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        ],
        StringComparer.OrdinalIgnoreCase);

    public static void Validate(string name)
    {
        ArchiveNameValidator.ValidateNodeName(name);

        if (name.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0)
        {
            throw new ArchiveValidationException(
                $"'{name}' contains characters that cannot be written as a Windows file name.");
        }

        if (name.EndsWith(' ') || name.EndsWith('.'))
        {
            throw new ArchiveValidationException(
                $"'{name}' ends with a space or period and cannot be written safely on Windows.");
        }

        var periodIndex = name.IndexOf('.');
        var baseName = periodIndex >= 0 ? name[..periodIndex] : name;
        if (ReservedBaseNames.Contains(baseName))
        {
            throw new ArchiveValidationException(
                $"'{name}' is a reserved Windows device name and cannot be exported.");
        }
    }
}
