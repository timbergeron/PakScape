using PakStudio.Core.Nodes;

namespace PakStudio.Core.Interfaces;

public interface IIconService
{
    string GetGlyphForNode(ArchiveNode node);
}
