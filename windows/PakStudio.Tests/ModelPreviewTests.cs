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
    public void ViewerExposesTheSelectedMdlSkinForCopying()
    {
        using var viewer = NativeModelViewer.Create(TestModels.CreateMdl(), ".mdl");

        var skin = Assert.IsType<ModelSkin>(viewer.GetSkin(0));

        Assert.Equal(4, skin.Width);
        Assert.Equal(4, skin.Height);
        Assert.Equal(4 * 4 * 4, skin.RgbaPixels.Length);
        Assert.Equal(255, skin.RgbaPixels[3]);
        Assert.Null(viewer.GetSkin(1));
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

    [Fact]
    public void SpritesOpenInTheInteractiveViewer()
    {
        var root = ArchiveFolderNode.CreateRoot();
        ArchiveTreeBuilder.AddFile(root, "progs/s_explod.spr", TestModels.CreateSpr(3));

        var file = root.Folders[0].Files[0];
        Assert.True(ArchivePreviewBuilder.OpensInQuickPreview(file));

        var preview = ArchivePreviewBuilder.Build(file);
        Assert.Equal(ArchivePreviewKind.Model, preview.Kind);

        using var session = ModelPreviewSession.Create(preview.Model!, decodeEncodedImage: null);
        Assert.Equal(ModelFormat.Spr, session.Statistics.Format);
        Assert.Equal(3, session.Statistics.FrameCount);
        Assert.Equal(0, session.SkinCount);
        Assert.Equal("Quake sprite • 3 frames", session.StatusLine);
    }

    [Fact]
    public void SpriteThumbnailsKeepTheFlatFirstFrame()
    {
        var root = ArchiveFolderNode.CreateRoot();
        ArchiveTreeBuilder.AddFile(root, "progs/s_explod.spr", TestModels.CreateSpr(2));

        var preview = ArchivePreviewBuilder.Build(
            root.Folders[0].Files[0],
            includeInteractiveModels: false);

        Assert.Equal(ArchivePreviewKind.Bitmap, preview.Kind);
        Assert.Equal(16, preview.Bitmap?.Width);
    }

    [Fact]
    public void SpritePlaybackRedrawsWhileTheCameraSitsStill()
    {
        using var viewer = NativeModelViewer.Create(TestModels.CreateSpr(3), ".spr");
        Settle(viewer);
        var playing = Render(viewer);

        var redraws = 0;
        for (var step = 0; step < 12; step++)
        {
            if (viewer.Advance(1.0 / 60.0))
            {
                redraws++;
            }
        }

        Assert.True(redraws > 0, "sprite playback should keep asking for redraws");
        Assert.NotEqual(playing, Render(viewer));
    }

    [Fact]
    public void BrushModelBspsOpenInTheInteractiveViewer()
    {
        var root = ArchiveFolderNode.CreateRoot();
        ArchiveTreeBuilder.AddFile(
            root,
            "maps/b_shell0.bsp",
            TestModels.CreateBsp(TestModels.BrushModelEntities));

        var file = root.Folders[0].Files[0];
        Assert.True(ArchivePreviewBuilder.SupportsInteractiveModel(file));
        Assert.True(ArchivePreviewBuilder.OpensInQuickPreview(file));

        var preview = ArchivePreviewBuilder.Build(file);
        Assert.Equal(ArchivePreviewKind.Model, preview.Kind);

        using var session = ModelPreviewSession.Create(preview.Model!, decodeEncodedImage: null);
        Assert.Equal(ModelFormat.Bsp, session.Statistics.Format);
        Assert.Equal(12, session.Statistics.TriangleCount);
        Assert.Equal(1, session.Statistics.TexturedSurfaceCount);
        Assert.Contains("Quake brush model", session.StatusLine);

        using var viewer = NativeModelViewer.Create(file.Data, ".bsp");
        Assert.Equal(1, viewer.EmbeddedTextureCount);
        var texture = Assert.IsType<EmbeddedModelTexture>(viewer.GetEmbeddedTexture(0));
        Assert.Equal("crate_top", texture.Name);
        Assert.Equal(8, texture.Width);
        Assert.Equal(8, texture.Height);
        Assert.Equal(8 * 8 * 4, texture.RgbaPixels.Length);
        Assert.Equal(255, texture.RgbaPixels[3]);
    }

    [Fact]
    public void LevelBspsKeepTheirFlatOverview()
    {
        var root = ArchiveFolderNode.CreateRoot();
        ArchiveTreeBuilder.AddFile(
            root,
            "maps/start.bsp",
            TestModels.CreateBsp(TestModels.LevelEntities));

        var file = root.Folders[0].Files[0];
        Assert.False(ArchivePreviewBuilder.SupportsInteractiveModel(file));
        Assert.False(ArchivePreviewBuilder.OpensInQuickPreview(file));
        Assert.NotEqual(ArchivePreviewKind.Model, ArchivePreviewBuilder.Build(file).Kind);
    }

    [Fact]
    public void LevelBspOverviewMarksImportantPickups()
    {
        var entities =
            "{\n\"classname\" \"worldspawn\"\n}\n" +
            "{\n\"classname\" \"info_player_start\"\n\"origin\" \"8 8 16\"\n}\n" +
            "{\n\"classname\" \"item_armor3\"\n\"origin\" \"0 0 16\"\n}\n";
        var preview = ArchivePreviewBuilder.Build(
            new ArchiveFileNode("arena.bsp", TestModels.CreateBsp(entities)),
            bspOptions: BspLevelPreviewOptions.All);

        var bitmap = Assert.IsType<PreviewBitmap>(preview.Bitmap);
        var foundRedArmorBadge = false;
        for (var y = bitmap.Height / 2 - 16; y <= bitmap.Height / 2 + 16; y++)
        {
            for (var x = bitmap.Width / 2 - 16; x <= bitmap.Width / 2 + 16; x++)
            {
                var offset = y * bitmap.Stride + x * 4;
                if (bitmap.BgraPixels[offset] == 52 &&
                    bitmap.BgraPixels[offset + 1] == 52 &&
                    bitmap.BgraPixels[offset + 2] == 205)
                {
                    foundRedArmorBadge = true;
                    break;
                }
            }
        }

        Assert.True(foundRedArmorBadge);
    }

    [Fact]
    public void LevelBspOverviewHidesMarkersByDefault()
    {
        var entities =
            "{\n\"classname\" \"worldspawn\"\n}\n" +
            "{\n\"classname\" \"info_player_start\"\n\"origin\" \"8 8 16\"\n}\n" +
            "{\n\"classname\" \"item_armor3\"\n\"origin\" \"0 0 16\"\n}\n";
        var preview = ArchivePreviewBuilder.Build(
            new ArchiveFileNode("arena.bsp", TestModels.CreateBsp(entities)));

        var bitmap = Assert.IsType<PreviewBitmap>(preview.Bitmap);
        for (var offset = 0; offset < bitmap.BgraPixels.Length; offset += 4)
        {
            Assert.False(
                bitmap.BgraPixels[offset] == 52 &&
                bitmap.BgraPixels[offset + 1] == 52 &&
                bitmap.BgraPixels[offset + 2] == 205);
        }
    }

    [Fact]
    public void BrushModelDetectionReadsTheFileRatherThanTheName()
    {
        Assert.True(NativeModelViewer.IsBspBrushModel(
            TestModels.CreateBsp(TestModels.BrushModelEntities)));

        /* Any spawn point, visibility data, or extra hull means it is a level. */
        Assert.False(NativeModelViewer.IsBspBrushModel(
            TestModels.CreateBsp(TestModels.LevelEntities)));
        Assert.False(NativeModelViewer.IsBspBrushModel(
            TestModels.CreateBsp(TestModels.BrushModelEntities, visibility: true)));
        Assert.False(NativeModelViewer.IsBspBrushModel(
            TestModels.CreateBsp(TestModels.BrushModelEntities, modelCount: 3)));
        Assert.False(NativeModelViewer.IsBspBrushModel([1, 2, 3, 4]));
        Assert.False(NativeModelViewer.IsBspBrushModel([]));
    }

    [Theory]
    [InlineData(".mdl")]
    [InlineData(".md3")]
    [InlineData(".md5mesh")]
    [InlineData(".spr")]
    [InlineData(".bsp")]
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

    /// <summary>
    /// A one-brush BSP: a textured cube written the way qbsp writes a brush model.
    /// Passing entities with a spawn point makes it read as a level instead.
    /// </summary>
    public static byte[] CreateBsp(string entities, bool visibility = false, int modelCount = 1)
    {
        const int textureSize = 8;

        var corners = new[]
        {
            (-16f, -16f, 0f), (16f, -16f, 0f), (16f, 16f, 0f), (-16f, 16f, 0f),
            (-16f, -16f, 32f), (16f, -16f, 32f), (16f, 16f, 32f), (-16f, 16f, 32f),
        };
        var quads = new[]
        {
            new[] { 0, 1, 2, 3 }, new[] { 7, 6, 5, 4 }, new[] { 0, 4, 5, 1 },
            new[] { 2, 6, 7, 3 }, new[] { 1, 5, 6, 2 }, new[] { 3, 7, 4, 0 },
        };
        var normals = new[]
        {
            (0f, 0f, -1f), (0f, 0f, 1f), (0f, -1f, 0f), (0f, 1f, 0f), (1f, 0f, 0f), (-1f, 0f, 0f),
        };

        var planes = new List<byte>();
        foreach (var (x, y, z) in normals)
        {
            AddSingle(planes, x);
            AddSingle(planes, y);
            AddSingle(planes, z);
            AddSingle(planes, 16);
            AddInt32(planes, 0);
        }

        var vertexes = new List<byte>();
        foreach (var (x, y, z) in corners)
        {
            AddSingle(vertexes, x);
            AddSingle(vertexes, y);
            AddSingle(vertexes, z);
        }

        var edges = new List<byte>();
        var surfedges = new List<byte>();
        AddInt16(edges, 0); // edge zero is unused, as in a real BSP
        AddInt16(edges, 0);
        var nextEdge = 1;
        foreach (var quad in quads)
        {
            for (var corner = 0; corner < 4; corner++)
            {
                AddInt16(edges, (short)quad[corner]);
                AddInt16(edges, (short)quad[(corner + 1) % 4]);
                AddInt32(surfedges, nextEdge++);
            }
        }

        var texinfo = new List<byte>();
        foreach (var component in new[] { 1f, 0f, 0f, 0f, 0f, 0f, -1f, 0f })
        {
            AddSingle(texinfo, component);
        }
        AddInt32(texinfo, 0); // miptex
        AddInt32(texinfo, 0); // flags

        var faces = new List<byte>();
        for (var face = 0; face < 6; face++)
        {
            AddInt16(faces, (short)face); // plane
            AddInt16(faces, 0);           // side
            AddInt32(faces, face * 4);    // first surfedge
            AddInt16(faces, 4);           // edges
            AddInt16(faces, 0);           // texinfo
            faces.AddRange(Enumerable.Repeat((byte)255, 4));
            AddInt32(faces, -1); // no lightmap
        }

        var textures = new List<byte>();
        AddInt32(textures, 1);
        AddInt32(textures, 8); // offset of the one miptex, from the lump
        AddName(textures, "crate_top", 16);
        AddInt32(textures, textureSize);
        AddInt32(textures, textureSize);
        AddInt32(textures, 40); // pixels follow the four mip offsets
        for (var mip = 1; mip < 4; mip++)
        {
            AddInt32(textures, 0);
        }
        textures.AddRange(Enumerable.Repeat((byte)15, textureSize * textureSize));

        var models = new List<byte>();
        for (var model = 0; model < modelCount; model++)
        {
            for (var component = 0; component < 3; component++)
            {
                AddSingle(models, -16);
            }
            for (var component = 0; component < 3; component++)
            {
                AddSingle(models, 32);
            }
            for (var component = 0; component < 3; component++)
            {
                AddSingle(models, 0);
            }
            for (var hull = 0; hull < 4; hull++)
            {
                AddInt32(models, 0);
            }
            AddInt32(models, 1); // visleafs
            AddInt32(models, 0); // first face
            AddInt32(models, 6); // faces
        }

        var entityBytes = new List<byte>(System.Text.Encoding.ASCII.GetBytes(entities)) { 0 };
        var visibilityBytes = new List<byte>(Enumerable.Repeat((byte)0, visibility ? 64 : 0));
        var empty = new List<byte>();

        /* Lumps in the order the header lists them. */
        var lumps = new[]
        {
            entityBytes, planes, textures, vertexes, visibilityBytes, empty, texinfo,
            faces, empty, empty, empty, empty, edges, surfedges, models,
        };

        var bytes = new List<byte>();
        AddInt32(bytes, 29);
        var cursor = 4 + lumps.Length * 8;
        foreach (var lump in lumps)
        {
            AddInt32(bytes, cursor);
            AddInt32(bytes, lump.Count);
            cursor += lump.Count;
        }
        foreach (var lump in lumps)
        {
            bytes.AddRange(lump);
        }
        return [.. bytes];
    }

    public const string BrushModelEntities =
        "{\n\"wad\" \"gfx/items.wad\"\n\"classname\" \"worldspawn\"\n}\n";

    public const string LevelEntities =
        "{\n\"wad\" \"gfx/base.wad\"\n\"classname\" \"worldspawn\"\n}\n" +
        "{\n\"classname\" \"info_player_start\"\n\"origin\" \"0 0 24\"\n}\n";

    /// A sprite whose frames differ in size, so playback shows up in a render.
    public static byte[] CreateSpr(int frames)
    {
        var bytes = new List<byte>();

        AddInt32(bytes, 0x50534449); // "IDSP"
        AddInt32(bytes, 1);
        AddInt32(bytes, 2); // view parallel
        AddSingle(bytes, 32);
        AddInt32(bytes, 32); // canvas width
        AddInt32(bytes, 32); // canvas height
        AddInt32(bytes, frames);
        AddSingle(bytes, 0); // beam length
        AddInt32(bytes, 0);  // sync type

        for (var frame = 0; frame < frames; frame++)
        {
            var size = 16 + frame * 8;
            AddInt32(bytes, 0); // a lone frame rather than a group
            AddInt32(bytes, -size / 2);
            AddInt32(bytes, size / 2);
            AddInt32(bytes, size);
            AddInt32(bytes, size);
            bytes.AddRange(Enumerable.Repeat((byte)15, size * size));
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
