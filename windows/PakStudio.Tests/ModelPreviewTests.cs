using System.Buffers.Binary;
using System.Runtime.InteropServices;
using PakStudio.Core.Models;
using PakStudio.Core.Nodes;
using PakStudio.Core.Operations;
using PakStudio.Core.Preview;
using Xunit;

namespace PakStudio.Tests;

public sealed class ModelPreviewTests
{
    [Fact]
    public void ModelsArePreviewedWithTheInteractiveViewer()
    {
        var root = ArchiveFolderNode.CreateRoot();
        ArchiveTreeBuilder.AddFile(root, "progs/armor.mdl", TestModels.CreateMdl());

        var preview = ArchivePreviewBuilder.Build(root.Folders[0].Files[0]);

        Assert.Equal(ArchivePreviewKind.Model, preview.Kind);
        Assert.NotNull(preview.Model);
        Assert.Equal(".mdl", preview.Model!.Extension);
    }

    [Fact]
    public void ThumbnailsKeepTheFlatSkinPreview()
    {
        var root = ArchiveFolderNode.CreateRoot();
        ArchiveTreeBuilder.AddFile(root, "progs/armor.mdl", TestModels.CreateMdl());

        var preview = ArchivePreviewBuilder.Build(
            root.Folders[0].Files[0],
            includeInteractiveModels: false);

        Assert.Equal(ArchivePreviewKind.Bitmap, preview.Kind);
        Assert.NotNull(preview.Bitmap);
    }

    [Fact]
    public void ModelsAndLmpImagesOpenInQuickPreviewOnDoubleClick()
    {
        var root = ArchiveFolderNode.CreateRoot();
        ArchiveTreeBuilder.AddFile(root, "progs/armor.mdl", TestModels.CreateMdl());
        ArchiveTreeBuilder.AddFile(root, "progs/pixel.lmp", TestModels.CreatePalettedImage(1, 1, 0));
        ArchiveTreeBuilder.AddFile(root, "progs/readme.txt", "hello"u8.ToArray());
        ArchiveTreeBuilder.AddFile(root, "progs/empty.md3", []);

        var models = root.Folders[0];
        Assert.True(ArchivePreviewBuilder.OpensInQuickPreview(
            models.Files.First(file => file.Name == "armor.mdl")));
        Assert.True(ArchivePreviewBuilder.OpensInQuickPreview(
            models.Files.First(file => file.Name == "pixel.lmp")));
        Assert.False(ArchivePreviewBuilder.OpensInQuickPreview(
            models.Files.First(file => file.Name == "readme.txt")));
        Assert.False(ArchivePreviewBuilder.OpensInQuickPreview(
            models.Files.First(file => file.Name == "empty.md3")));
        Assert.False(ArchivePreviewBuilder.OpensInQuickPreview(models));
        Assert.False(ArchivePreviewBuilder.OpensInQuickPreview(null));
    }

    [Fact]
    public void ViewerReportsGeometryAndRendersTheModel()
    {
        using var viewer = NativeModelViewer.Create(TestModels.CreateMdl(), ".mdl");

        Assert.Equal(ModelFormat.Mdl, viewer.Statistics.Format);
        Assert.Equal(1, viewer.Statistics.TriangleCount);
        Assert.Equal(1, viewer.Statistics.SkinCount);
        Assert.Empty(viewer.TextureRequests);
        Assert.True(viewer.ShowInteractionPrompt);

        const int width = 64;
        const int height = 64;
        var pixels = new byte[width * height * 4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            Assert.True(viewer.Render(handle.AddrOfPinnedObject(), width, height, width * 4));
        }
        finally
        {
            handle.Free();
        }

        var lit = 0;
        for (var index = 0; index + 3 < pixels.Length; index += 4)
        {
            if ((pixels[index] + pixels[index + 1] + pixels[index + 2]) / 3 > 90)
            {
                lit++;
            }
        }
        Assert.True(lit > 40, $"expected the framed model to cover the view, saw {lit} lit pixels");
    }

    [Fact]
    public void OrbitingChangesTheCameraAndResetRestoresIt()
    {
        using var viewer = NativeModelViewer.Create(TestModels.CreateMdl(), ".mdl");
        viewer.AutoRotate = false;
        Settle(viewer);
        var framed = Render(viewer);

        viewer.BeginInteraction();
        viewer.Orbit(40, 10);
        viewer.EndInteraction();
        Settle(viewer);
        Assert.NotEqual(framed, Render(viewer));
        Assert.False(viewer.ShowInteractionPrompt);

        viewer.Reset();
        Settle(viewer);
        Assert.Equal(framed, Render(viewer));
        Assert.False(viewer.Advance(1.0 / 60.0));
    }

    [Fact]
    public void Md3ShadersResolveSkinsFromTheArchive()
    {
        var root = ArchiveFolderNode.CreateRoot();
        ArchiveTreeBuilder.AddFile(root, "models/test/body.md3", TestModels.CreateMd3("models/test/body"));
        ArchiveTreeBuilder.AddFile(root, "models/test/body.lmp", TestModels.CreatePalettedImage(8, 8, 15));

        var preview = ArchivePreviewBuilder.Build(
            root.Folders[0].Folders[0].Files.First(file => file.Name == "body.md3"));
        Assert.Equal(ArchivePreviewKind.Model, preview.Kind);

        using var session = ModelPreviewSession.Create(preview.Model!, decodeEncodedImage: null);

        Assert.Equal(1, session.Statistics.TextureRequestCount);
        Assert.Equal(1, session.Statistics.TexturedSurfaceCount);
        Assert.Contains("Quake III MD3", session.StatusLine);
        Assert.DoesNotContain("not in this archive", session.StatusLine);
    }

    [Fact]
    public void SkinFilesOverrideShaderNames()
    {
        var root = ArchiveFolderNode.CreateRoot();
        ArchiveTreeBuilder.AddFile(root, "models/test/body.md3", TestModels.CreateMd3("models/test/missing"));
        ArchiveTreeBuilder.AddFile(root, "models/test/replacement.lmp", TestModels.CreatePalettedImage(8, 8, 15));
        ArchiveTreeBuilder.AddFile(
            root,
            "models/test/body_0.skin",
            "body,models/test/replacement\n"u8.ToArray());

        var preview = ArchivePreviewBuilder.Build(
            root.Folders[0].Folders[0].Files.First(file => file.Name == "body.md3"));
        using var session = ModelPreviewSession.Create(preview.Model!, decodeEncodedImage: null);

        Assert.Equal(1, session.Statistics.TexturedSurfaceCount);
    }

    [Fact]
    public void MissingSkinsAreReportedInTheStatusLine()
    {
        var root = ArchiveFolderNode.CreateRoot();
        ArchiveTreeBuilder.AddFile(root, "models/test/body.md3", TestModels.CreateMd3("models/test/absent"));

        var preview = ArchivePreviewBuilder.Build(root.Folders[0].Folders[0].Files[0]);
        using var session = ModelPreviewSession.Create(preview.Model!, decodeEncodedImage: null);

        Assert.Equal(0, session.Statistics.TexturedSurfaceCount);
        Assert.Contains("not in this archive", session.StatusLine);
    }

    [Fact]
    public void Md5MeshesLoadTheirBindPose()
    {
        using var viewer = NativeModelViewer.Create(TestModels.CreateMd5("models/test/body"), ".md5mesh");

        Assert.Equal(ModelFormat.Md5, viewer.Statistics.Format);
        Assert.Equal(3, viewer.Statistics.VertexCount);
        Assert.Equal(1, viewer.Statistics.TriangleCount);
        Assert.Equal("models/test/body", Assert.Single(viewer.TextureRequests).Name);
    }

    [Fact]
    public void Md5ShadersResolveNumberedExportTextures()
    {
        var root = ArchiveFolderNode.CreateRoot();
        ArchiveTreeBuilder.AddFile(
            root,
            "models/test/body.md5mesh",
            TestModels.CreateMd5("body"));
        ArchiveTreeBuilder.AddFile(
            root,
            "models/test/body_00_00.lmp",
            TestModels.CreatePalettedImage(8, 8, 15));

        var preview = ArchivePreviewBuilder.Build(
            root.Folders[0].Folders[0].Files.First(file => file.Name == "body.md5mesh"));
        using var session = ModelPreviewSession.Create(preview.Model!, decodeEncodedImage: null);

        Assert.Equal(1, session.Statistics.TexturedSurfaceCount);
        Assert.DoesNotContain("not in this archive", session.StatusLine);
    }

    [Fact]
    public void Md5MeshAndAnimationFindTheirCompanionThumbnail()
    {
        var root = ArchiveFolderNode.CreateRoot();
        ArchiveTreeBuilder.AddFile(root, "models/v_nail2.md5mesh", TestModels.CreateMd5("v_nail2"));
        ArchiveTreeBuilder.AddFile(root, "models/v_nail2.md5anim", "MD5Version 10\n"u8.ToArray());
        ArchiveTreeBuilder.AddFile(
            root,
            "models/v_nail2_00_00.lmp",
            TestModels.CreatePalettedImage(8, 8, 15));
        ArchiveTreeBuilder.AddFile(root, "models/v_nail2_00_00.png", [1, 2, 3, 4]);

        var files = root.Folders[0].Files;
        var mesh = files.First(file => file.Extension == ".md5mesh");
        var animation = files.First(file => file.Extension == ".md5anim");

        Assert.Equal(
            "v_nail2_00_00.png",
            ModelTextureResolver.FindCompanionThumbnail(mesh)?.Name);
        Assert.Equal(
            "v_nail2_00_00.png",
            ModelTextureResolver.FindCompanionThumbnail(animation)?.Name);
    }

    [Theory]
    [InlineData(".mdl")]
    [InlineData(".md3")]
    [InlineData(".md5mesh")]
    public void CorruptModelsAreRejectedWithAReason(string extension)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => NativeModelViewer.Create([1, 2, 3, 4, 5, 6, 7, 8], extension));

        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
    }

    [Fact]
    public void TruncatedModelsAreRejected()
    {
        var model = TestModels.CreateMdl();
        for (var size = 1; size < model.Length; size += 13)
        {
            Assert.Throws<InvalidOperationException>(
                () => NativeModelViewer.Create(model[..size], ".mdl"));
        }
    }

    private static void Settle(NativeModelViewer viewer)
    {
        for (var step = 0; step < 240; step++)
        {
            viewer.Advance(1.0 / 60.0);
        }
    }

    private static byte[] Render(NativeModelViewer viewer)
    {
        const int width = 48;
        const int height = 48;
        var pixels = new byte[width * height * 4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            Assert.True(viewer.Render(handle.AddrOfPinnedObject(), width, height, width * 4));
        }
        finally
        {
            handle.Free();
        }
        return pixels;
    }
}

/// Minimal but valid models, small enough to reason about in assertions.
internal static class TestModels
{
    public static byte[] CreateMdl()
    {
        var bytes = new List<byte>();
        const int skinWidth = 4;
        const int skinHeight = 4;

        AddInt32(bytes, 0x4F504449); // "IDPO"
        AddInt32(bytes, 6);
        AddSingle(bytes, 1);
        AddSingle(bytes, 1);
        AddSingle(bytes, 1);
        AddSingle(bytes, 0);
        AddSingle(bytes, 0);
        AddSingle(bytes, 0);
        AddSingle(bytes, 128);
        AddSingle(bytes, 0);
        AddSingle(bytes, 0);
        AddSingle(bytes, 0);
        AddInt32(bytes, 1);
        AddInt32(bytes, skinWidth);
        AddInt32(bytes, skinHeight);
        AddInt32(bytes, 3);
        AddInt32(bytes, 1);
        AddInt32(bytes, 1);
        AddInt32(bytes, 0);
        AddInt32(bytes, 0);
        AddSingle(bytes, 0);

        AddInt32(bytes, 0); // single skin
        bytes.AddRange(Enumerable.Repeat((byte)15, skinWidth * skinHeight));

        foreach (var (s, t) in new[] { (0, 0), (3, 0), (0, 3) })
        {
            AddInt32(bytes, 0);
            AddInt32(bytes, s);
            AddInt32(bytes, t);
        }

        AddInt32(bytes, 1); // faces front
        AddInt32(bytes, 0);
        AddInt32(bytes, 1);
        AddInt32(bytes, 2);

        AddInt32(bytes, 0); // single frame
        bytes.AddRange(new byte[8]);
        AddName(bytes, "frame", 16);
        foreach (var position in new[] { (10, 10, 10), (200, 20, 10), (20, 200, 180) })
        {
            bytes.Add((byte)position.Item1);
            bytes.Add((byte)position.Item2);
            bytes.Add((byte)position.Item3);
            bytes.Add(0);
        }
        return [.. bytes];
    }

    public static byte[] CreateMd3(string shader)
    {
        var bytes = new List<byte>();

        AddInt32(bytes, 0x33504449); // "IDP3"
        AddInt32(bytes, 15);
        AddName(bytes, "test", 64);
        AddInt32(bytes, 0);
        AddInt32(bytes, 1);   // frames
        AddInt32(bytes, 0);   // tags
        AddInt32(bytes, 1);   // surfaces
        AddInt32(bytes, 0);   // skins
        AddInt32(bytes, 108); // frame offset
        AddInt32(bytes, 164);
        AddInt32(bytes, 164); // surface offset
        AddInt32(bytes, 400);

        for (var index = 0; index < 3; index++)
        {
            AddSingle(bytes, -64);
        }
        for (var index = 0; index < 3; index++)
        {
            AddSingle(bytes, 64);
        }
        for (var index = 0; index < 3; index++)
        {
            AddSingle(bytes, 0);
        }
        AddSingle(bytes, 64);
        AddName(bytes, "frame", 16);

        AddInt32(bytes, 0x33504449);
        AddName(bytes, "body", 64);
        AddInt32(bytes, 0);
        AddInt32(bytes, 1);   // frames
        AddInt32(bytes, 1);   // shaders
        AddInt32(bytes, 3);   // vertices
        AddInt32(bytes, 1);   // triangles
        AddInt32(bytes, 176); // triangles
        AddInt32(bytes, 108); // shaders
        AddInt32(bytes, 188); // texture coordinates
        AddInt32(bytes, 212); // vertices
        AddInt32(bytes, 236); // end

        AddName(bytes, shader, 64);
        AddInt32(bytes, 0);

        AddInt32(bytes, 0);
        AddInt32(bytes, 1);
        AddInt32(bytes, 2);

        foreach (var (s, t) in new[] { (0f, 0f), (1f, 0f), (0f, 1f) })
        {
            AddSingle(bytes, s);
            AddSingle(bytes, t);
        }

        foreach (var position in new[] { (-512, -512, -512), (512, -512, -512), (-512, 512, 512) })
        {
            AddInt16(bytes, (short)position.Item1);
            AddInt16(bytes, (short)position.Item2);
            AddInt16(bytes, (short)position.Item3);
            bytes.Add(64);
            bytes.Add(32);
        }
        return [.. bytes];
    }

    public static byte[] CreateMd5(string shader) =>
        System.Text.Encoding.UTF8.GetBytes(
            "MD5Version 10\n" +
            "numJoints 1\n" +
            "numMeshes 1\n" +
            "joints {\n" +
            "\t\"origin\" -1 ( 0 0 0 ) ( 0 0 0 )\n" +
            "}\n" +
            "mesh {\n" +
            $"\tshader \"{shader}\"\n" +
            "\tnumverts 3\n" +
            "\tvert 0 ( 0 0 ) 0 1\n" +
            "\tvert 1 ( 1 0 ) 1 1\n" +
            "\tvert 2 ( 0 1 ) 2 1\n" +
            "\tnumtris 1\n" +
            "\ttri 0 0 1 2\n" +
            "\tnumweights 3\n" +
            "\tweight 0 0 1 ( 0 0 0 )\n" +
            "\tweight 1 0 1 ( 16 0 0 )\n" +
            "\tweight 2 0 1 ( 0 16 12 )\n" +
            "}\n");

    /// A Quake LMP image: width, height, then palette indexes.
    public static byte[] CreatePalettedImage(int width, int height, byte paletteIndex)
    {
        var bytes = new List<byte>();
        AddInt32(bytes, width);
        AddInt32(bytes, height);
        bytes.AddRange(Enumerable.Repeat(paletteIndex, width * height));
        return [.. bytes];
    }

    private static void AddInt32(List<byte> bytes, int value)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        bytes.AddRange(buffer);
    }

    private static void AddInt16(List<byte> bytes, short value)
    {
        var buffer = new byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(buffer, value);
        bytes.AddRange(buffer);
    }

    private static void AddSingle(List<byte> bytes, float value)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(buffer, value);
        bytes.AddRange(buffer);
    }

    private static void AddName(List<byte> bytes, string value, int length)
    {
        for (var index = 0; index < length; index++)
        {
            bytes.Add(index < value.Length ? (byte)value[index] : (byte)0);
        }
    }
}
