namespace PakStudio.Formats.Pk3;

/// <summary>
/// KEX Engine resource packages use the ZIP container format, just like PK3 archives.
/// Keeping a separate format identity preserves the .kpf extension when saving.
/// </summary>
public sealed class KpfFormatHandler : Pk3FormatHandler
{
    public KpfFormatHandler()
        : base("kpf", "Quake KPF Archive", [".kpf"])
    {
    }
}
