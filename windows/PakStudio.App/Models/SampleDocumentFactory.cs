using PakStudio.Core.Documents;
using PakStudio.Core.Operations;

namespace PakStudio.App.Models;

public static class SampleDocumentFactory
{
    public static ArchiveDocument Create()
    {
        var document = new ArchiveDocument
        {
            FormatId = "pak",
        };

        ArchiveTreeBuilder.AddFile(document.Root, "maps/e1m1.bsp", [0x42, 0x53, 0x50]);
        ArchiveTreeBuilder.AddFile(document.Root, "maps/start.bsp", [0x42, 0x53, 0x50]);
        ArchiveTreeBuilder.AddFile(document.Root, "progs/player.mdl", [0x49, 0x44, 0x50, 0x4F]);
        ArchiveTreeBuilder.AddFile(document.Root, "gfx/pop.lmp", [0x10, 0x20, 0x30, 0x40]);
        ArchiveTreeBuilder.AddFile(document.Root, "sound/items/pkup.wav", [0x52, 0x49, 0x46, 0x46]);
        ArchiveTreeBuilder.SortRecursively(document.Root);
        return document;
    }
}
