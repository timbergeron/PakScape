namespace PakStudio.Core.Validation;

public class ArchiveException : Exception
{
    public ArchiveException(string message) : base(message)
    {
    }
}

public sealed class UnsupportedArchiveFormatException : ArchiveException
{
    public UnsupportedArchiveFormatException(string message) : base(message)
    {
    }
}

public sealed class ArchiveCorruptException : ArchiveException
{
    public ArchiveCorruptException(string message) : base(message)
    {
    }
}

public sealed class ArchivePathConflictException : ArchiveException
{
    public ArchivePathConflictException(string message) : base(message)
    {
    }
}

public sealed class ArchiveValidationException : ArchiveException
{
    public ArchiveValidationException(string message) : base(message)
    {
    }
}
