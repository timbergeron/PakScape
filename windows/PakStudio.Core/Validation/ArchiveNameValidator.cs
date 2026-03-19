namespace PakStudio.Core.Validation;

public static class ArchiveNameValidator
{
    private static readonly char[] InvalidCharacters = ['/', '\\', '\0'];

    public static void ValidateNodeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArchiveValidationException("Names cannot be empty.");
        }

        if (name.IndexOfAny(InvalidCharacters) >= 0)
        {
            throw new ArchiveValidationException("Names cannot contain path separators.");
        }
    }
}
