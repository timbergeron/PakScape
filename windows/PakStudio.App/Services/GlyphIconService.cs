using PakStudio.Core.Interfaces;
using PakStudio.Core.Nodes;
using PakStudio.Core.Preview;

namespace PakStudio.App.Services;

public sealed class GlyphIconService : IIconService
{
    public string GetGlyphForNode(ArchiveNode node)
    {
        return node switch
        {
            ArchiveFolderNode => "\uE8B7",
            ArchiveFileNode file when string.Equals(file.Extension, ".bsp", StringComparison.OrdinalIgnoreCase) => "\uE7C3",
            ArchiveFileNode file when string.Equals(file.Extension, ".mdl", StringComparison.OrdinalIgnoreCase) => "\uEC17",
            ArchiveFileNode file when ArchivePreviewBuilder.SupportsAudioExtension(file.Extension) => "\uE8D6",
            _ => "\uE8A5",
        };
    }
}
