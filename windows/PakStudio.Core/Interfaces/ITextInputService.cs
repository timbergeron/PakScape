namespace PakStudio.Core.Interfaces;

public interface ITextInputService
{
    string? Prompt(string title, string message, string initialValue);
}
