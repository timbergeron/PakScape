using System.Text;
using PakStudio.Core.Nodes;
using PakStudio.Core.Pathing;

namespace PakStudio.Core.Preview;

/// <summary>Straight RGBA8 pixels, the layout the native model viewer uploads.</summary>
public sealed record ModelTextureData(int Width, int Height, byte[] RgbaPixels);

/// <summary>
/// A skin found in the archive. Quake formats are decoded here; formats the host
/// toolkit already knows how to read are handed back encoded.
/// </summary>
public sealed record ResolvedModelTexture(
    string Path,
    ModelTextureData? Decoded = null,
    byte[]? EncodedImage = null);

/// <summary>
/// Finds the skins an MD3 or MD5 mesh names. Shader paths in the wild are written
/// against the game directory, use either slash, and frequently name an extension
/// the archive does not actually carry, so every plausible spelling is tried.
/// </summary>
public sealed class ModelTextureResolver
{
    private static readonly HashSet<string> CompanionThumbnailExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".md5mesh", ".md5anim", ".mesh", ".anim" };

    private static readonly string[] ImageExtensions =
    [
        ".tga", ".png", ".jpg", ".jpeg", ".pcx", ".lmp", ".bmp",
    ];

    private static readonly HashSet<string> DecodableExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".tga", ".pcx", ".lmp" };

    private static readonly HashSet<string> EncodedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff",
        };

    private readonly ArchiveFolderNode? _root;
    private readonly string _folderPath;
    private readonly string _baseName;

    public ModelTextureResolver(ArchiveFileNode model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var root = model.Parent;
        while (root?.Parent is { } parent)
        {
            root = parent;
        }
        _root = root;
        _folderPath = PathHelper.NormalizeArchivePath(model.Parent?.FullPath);
        _baseName = System.IO.Path.GetFileNameWithoutExtension(model.Name);
    }

    /// <summary>
    /// Finds the exported material image that belongs to an MD5 mesh or animation.
    /// Noesis-style exports commonly use the _00_00 suffix for the first material.
    /// </summary>
    public static ArchiveFileNode? FindCompanionThumbnail(ArchiveFileNode file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!CompanionThumbnailExtensions.Contains(file.Extension) || file.Parent is not { } folder)
        {
            return null;
        }

        var baseName = System.IO.Path.GetFileNameWithoutExtension(file.Name);
        string[] names =
        [
            baseName + "_00_00.png",
            baseName + ".png",
            baseName + "_00_00.lmp",
            baseName + ".lmp",
        ];
        foreach (var name in names)
        {
            var sibling = folder.Files.FirstOrDefault(
                candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            if (sibling is not null)
            {
                return sibling;
            }
        }
        return null;
    }

    /// <summary>Reads a Quake III skin file that remaps surfaces to textures.</summary>
    public IReadOnlyDictionary<string, string> ReadSkinOverrides()
    {
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < 4; index++)
        {
            var file = FindFile(Combine(_folderPath, $"{_baseName}_{index}.skin"));
            if (file is null)
            {
                break;
            }

            foreach (var line in Encoding.UTF8.GetString(file.Data).Split('\n'))
            {
                var separator = line.IndexOf(',');
                if (separator <= 0)
                {
                    continue;
                }
                var surface = line[..separator].Trim();
                var texture = line[(separator + 1)..].Trim().TrimEnd('\r');
                if (surface.Length > 0 && texture.Length > 0)
                {
                    overrides[surface] = texture;
                }
            }
        }
        return overrides;
    }

    public bool TryResolve(string requestedName, out ResolvedModelTexture texture)
    {
        texture = null!;
        if (string.IsNullOrWhiteSpace(requestedName) || _root is null)
        {
            return false;
        }

        foreach (var candidate in Candidates(requestedName))
        {
            var file = FindFile(candidate);
            if (file is null || file.Data.Length == 0)
            {
                continue;
            }

            var extension = file.Extension;
            if (DecodableExtensions.Contains(extension) &&
                QuakePreviewDecoder.TryDecode(file.Name, file.Data, out var bitmap))
            {
                texture = new ResolvedModelTexture(file.FullPath, ToRgba(bitmap));
                return true;
            }
            if (EncodedExtensions.Contains(extension))
            {
                texture = new ResolvedModelTexture(file.FullPath, EncodedImage: file.Data);
                return true;
            }
        }

        return false;
    }

    private IEnumerable<string> Candidates(string requestedName)
    {
        var normalized = PathHelper.NormalizeArchivePath(requestedName);
        if (normalized.Length == 0)
        {
            yield break;
        }

        var withoutExtension = StripImageExtension(normalized);
        var leaf = LastSegment(withoutExtension);

        /* Shader paths are usually rooted at the game directory. */
        foreach (var spelling in Spellings(normalized, withoutExtension))
        {
            yield return spelling;
        }

        /* Some tools export names relative to the model's own folder. */
        if (_folderPath.Length > 0)
        {
            foreach (var spelling in Spellings(
                         Combine(_folderPath, normalized),
                         Combine(_folderPath, withoutExtension)))
            {
                yield return spelling;
            }

            foreach (var spelling in Spellings(
                         Combine(_folderPath, leaf),
                         Combine(_folderPath, leaf)))
            {
                yield return spelling;
            }
        }
    }

    private static IEnumerable<string> Spellings(string exact, string withoutExtension)
    {
        yield return exact;
        foreach (var extension in ImageExtensions)
        {
            yield return withoutExtension + extension;
        }

        /*
         * Some MD5 exporters write the first mesh/material image beside the
         * model as <shader>_00_00 even though the mesh keeps the unsuffixed
         * shader name.
         */
        foreach (var extension in ImageExtensions)
        {
            yield return withoutExtension + "_00_00" + extension;
        }
    }

    private static string StripImageExtension(string path)
    {
        var extension = System.IO.Path.GetExtension(path);
        return extension.Length > 0 && ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            ? path[..^extension.Length]
            : path;
    }

    private static string LastSegment(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator < 0 ? path : path[(separator + 1)..];
    }

    private static string Combine(string folder, string name) =>
        folder.Length == 0 ? name : folder + "/" + name;

    private ArchiveFileNode? FindFile(string path)
    {
        var segments = PathHelper.SplitArchivePath(path);
        if (segments.Count == 0 || _root is not { } folder)
        {
            return null;
        }

        for (var index = 0; index < segments.Count - 1; index++)
        {
            var segment = segments[index];
            var child = folder.Folders.FirstOrDefault(
                candidate => string.Equals(candidate.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (child is null)
            {
                return null;
            }
            folder = child;
        }

        return folder.Files.FirstOrDefault(
            file => string.Equals(file.Name, segments[^1], StringComparison.OrdinalIgnoreCase));
    }

    private static ModelTextureData ToRgba(PreviewBitmap bitmap)
    {
        var pixels = new byte[bitmap.BgraPixels.Length];
        for (var index = 0; index + 3 < bitmap.BgraPixels.Length; index += 4)
        {
            pixels[index] = bitmap.BgraPixels[index + 2];
            pixels[index + 1] = bitmap.BgraPixels[index + 1];
            pixels[index + 2] = bitmap.BgraPixels[index];
            pixels[index + 3] = bitmap.BgraPixels[index + 3];
        }
        return new ModelTextureData(bitmap.Width, bitmap.Height, pixels);
    }
}
