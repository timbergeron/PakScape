using PakStudio.Core.Nodes;

namespace PakStudio.Core.Preview;

/// <summary>
/// The six sibling images that make up a Quake skybox. Engines append the
/// two-letter face name directly to the sky name, so both "desertrt.tga" and
/// "desert_rt.tga" are supported.
/// </summary>
public sealed class SkyboxFaceSet
{
    public enum Face
    {
        Right,
        Back,
        Left,
        Front,
        Up,
        Down,
    }

    private static readonly IReadOnlyDictionary<Face, string> FaceSuffixes =
        new Dictionary<Face, string>
        {
            [Face.Right] = "rt",
            [Face.Back] = "bk",
            [Face.Left] = "lf",
            [Face.Front] = "ft",
            [Face.Up] = "up",
            [Face.Down] = "dn",
        };

    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".bmp", ".jpeg", ".jpg", ".pcx", ".png", ".tga",
        };

    private SkyboxFaceSet(string name, IReadOnlyDictionary<Face, ArchiveFileNode> faces)
    {
        Name = name;
        Faces = faces;
    }

    public string Name { get; }

    public IReadOnlyDictionary<Face, ArchiveFileNode> Faces { get; }

    public static SkyboxFaceSet? Find(ArchiveNode? selected)
    {
        if (selected is not ArchiveFileNode selectedFile ||
            selectedFile.Parent is not { } parent ||
            !SupportedExtensions.Contains(selectedFile.Extension))
        {
            return null;
        }

        var stem = Path.GetFileNameWithoutExtension(selectedFile.Name);
        var selectedFace = FaceSuffixes.FirstOrDefault(pair =>
            stem.EndsWith(pair.Value, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(selectedFace.Value))
        {
            return null;
        }

        var baseName = stem[..^selectedFace.Value.Length];
        if (baseName.Length == 0)
        {
            return null;
        }

        var matches = new Dictionary<Face, ArchiveFileNode>();
        foreach (var (face, suffix) in FaceSuffixes)
        {
            var wantedStem = baseName + suffix;
            var candidates = parent.Files.Where(file =>
                SupportedExtensions.Contains(file.Extension) &&
                Path.GetFileNameWithoutExtension(file.Name)
                    .Equals(wantedStem, StringComparison.OrdinalIgnoreCase));
            var match = candidates.FirstOrDefault(file =>
                    file.Extension.Equals(selectedFile.Extension, StringComparison.OrdinalIgnoreCase))
                ?? candidates.FirstOrDefault();
            if (match is null ||
                match.Size > ArchivePreviewBuilder.MaximumFileSize)
            {
                return null;
            }
            matches[face] = match;
        }

        long totalSize = 0;
        foreach (var file in matches.Values)
        {
            if (file.Size > ArchivePreviewBuilder.MaximumSelectionSize - totalSize)
            {
                return null;
            }
            totalSize += file.Size;
        }

        return new SkyboxFaceSet(baseName.Trim('_', '-', ' '), matches);
    }
}
