using System.Buffers.Binary;
using PakStudio.Core.Nodes;
using PakStudio.Core.Preview;
using Xunit;

namespace PakStudio.Tests;

public sealed class ArchiveMetadataInspectorTests
{
    [Fact]
    public void DemoSummaryFindsMapsAndIgnoresBrushModels()
    {
        var file = new ArchiveFileNode(
            "run.dem",
            "noise maps/b_shell0.bsp more maps/e1m3.bsp duplicate maps/E1M3.bsp"u8.ToArray());

        var metadata = ArchiveMetadataInspector.Inspect(file);

        Assert.Equal("Map: e1m3", metadata.Summary);
        Assert.Contains("e1m3", metadata.SearchText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BspSummaryReadsWorldspawnDescription()
    {
        var entities = "{\"classname\" \"worldspawn\" \"message\" \"The Slipgate Complex\"}"u8.ToArray();
        var data = new byte[124 + entities.Length];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0, 4), 29);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4, 4), 124);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8, 4), entities.Length);
        entities.CopyTo(data, 124);
        var file = new ArchiveFileNode("e1m1.bsp", data);

        var metadata = ArchiveMetadataInspector.Inspect(file);

        Assert.Equal("Description: The Slipgate Complex", metadata.Summary);
        Assert.Contains(
            metadata.Details,
            detail => detail.Label == "Description" && detail.Value == "The Slipgate Complex");
    }

    [Fact]
    public void CommonImageMetadataProducesConciseDetails()
    {
        byte[] png =
        [
            137, 80, 78, 71, 13, 10, 26, 10,
            0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
            0, 0, 1, 64, 0, 0, 0, 200, 8, 6, 0, 0, 0,
        ];

        var metadata = ArchiveMetadataInspector.Inspect(new ArchiveFileNode("shot.png", png));

        Assert.Contains("Dimensions: 320 × 200 pixels", metadata.Summary);
        Assert.Contains("8-bit", metadata.SearchText);
    }
}
