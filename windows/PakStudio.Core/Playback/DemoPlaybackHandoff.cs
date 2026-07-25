using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using PakStudio.Core.Preview;

namespace PakStudio.Core.Playback;

/// <summary>One file the player is allowed to fetch: the demo, or an archive holding its maps.</summary>
public sealed record DemoPlaybackAsset(string FileName, byte[] Data);

public sealed class DemoPlaybackException : Exception
{
    public DemoPlaybackException(string message) : base(message)
    {
    }
}

/// <summary>
/// Hands a demo to the q1tools web player instead of embedding a game engine.
/// </summary>
/// <remarks>
/// The demo never leaves the machine. PakScape serves it from a loopback socket, and the
/// page — which the browser downloads from the player's own origin — fetches it back from
/// 127.0.0.1. Only paths registered for a handoff are served, so there is no document root
/// to traverse, and every session expires on its own.
/// </remarks>
public static class DemoPlaybackHandoff
{
    /// <summary>The published player. Kept as one constant so pinning a fork is a one-line change.</summary>
    public const string PlayerUrl = "https://q1tools.github.io/demo/play/";

    /// <summary>Total bytes one handoff may publish, covering the demo and any archive with its maps.</summary>
    public const int MaximumSessionBytes = 256 * 1024 * 1024;

    /// <summary>Long enough to restart playback, short enough that a forgotten window stops listening.</summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(15);

    /// <summary>The origin allowed to read the served assets, derived from <see cref="PlayerUrl"/>.</summary>
    public static string PlayerOrigin { get; } = Uri.TryCreate(PlayerUrl, UriKind.Absolute, out var uri)
        ? uri.GetLeftPart(UriPartial.Authority)
        : "null";

    /// <summary>Publishes the demo and any packages, then returns the URL to open in a browser.</summary>
    public static Uri BuildLaunchUri(
        DemoPlaybackAsset demo,
        IReadOnlyList<DemoPlaybackAsset> packages,
        QuakeDemoSummary? summary,
        LoopbackAssetServer server)
    {
        ArgumentNullException.ThrowIfNull(demo);
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(server);

        var assets = new List<DemoPlaybackAsset>(packages.Count + 1) { demo };
        assets.AddRange(packages);

        long total = 0;
        foreach (var asset in assets)
        {
            total += asset.Data.Length;
        }
        if (total > MaximumSessionBytes)
        {
            throw new DemoPlaybackException(
                $"This demo and its archive are larger than the {MaximumSessionBytes / (1024 * 1024)} MB playback limit.");
        }

        var sources = server.Publish(assets, SessionLifetime);
        return BuildLaunchUri(demo, packages, summary, sources);
    }

    /// <summary>The URL half of the handoff, separated so it can be checked without a socket.</summary>
    public static Uri BuildLaunchUri(
        DemoPlaybackAsset demo,
        IReadOnlyList<DemoPlaybackAsset> packages,
        QuakeDemoSummary? summary,
        IReadOnlyList<Uri> sources)
    {
        ArgumentNullException.ThrowIfNull(demo);
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(sources);

        if (sources.Count != packages.Count + 1)
        {
            throw new DemoPlaybackException("The player needs one source for the demo and each package.");
        }

        var maps = OrderedMaps(summary);
        var query = new StringBuilder();
        Append(query, "source", sources[0].AbsoluteUri);
        Append(query, "file", VirtualFileName(demo.FileName));

        var title = PlayerTitle(demo.FileName, summary);
        if (title.Length > 0)
        {
            Append(query, "title", title);
        }
        if (maps.Count > 0)
        {
            Append(query, "maps", string.Join(",", maps));
        }
        if (summary is { Duration: > 0 })
        {
            Append(query, "duration", summary.Duration.ToString("F2", CultureInfo.InvariantCulture));
        }
        if (packages.Count > 0)
        {
            var descriptors = new JsonArray();
            for (var index = 0; index < packages.Count; index++)
            {
                var mapNames = new JsonArray();
                foreach (var map in maps)
                {
                    mapNames.Add(map);
                }
                descriptors.Add(new JsonObject
                {
                    ["file"] = VirtualFileName(packages[index].FileName),
                    ["source"] = sources[index + 1].AbsoluteUri,
                    ["maps"] = mapNames,
                });
            }
            Append(query, "packages", descriptors.ToJsonString());
        }

        return new Uri(PlayerUrl + "?" + query, UriKind.Absolute);
    }

    /// <summary>
    /// The player builds its own virtual paths from these names, so anything that could climb
    /// out of its filesystem is replaced rather than escaped.
    /// </summary>
    public static string VirtualFileName(string name)
    {
        var value = name ?? string.Empty;
        var lastSeparator = value.LastIndexOfAny(['/', '\\', ':']);
        if (lastSeparator >= 0)
        {
            value = value[(lastSeparator + 1)..];
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            var safe = character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '.' or '_' or '-' or '+';
            builder.Append(safe ? character : '_');
        }

        var cleaned = builder.ToString().Trim('.');
        return cleaned.Length == 0 ? "demo.dem" : cleaned;
    }

    private static List<string> OrderedMaps(QuakeDemoSummary? summary)
    {
        var maps = new List<string>();
        if (summary is null)
        {
            return maps;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in summary.Segments)
        {
            if (segment.Map.Length > 0 && seen.Add(segment.Map))
            {
                maps.Add(segment.Map);
            }
        }
        return maps;
    }

    private static string PlayerTitle(string fileName, QuakeDemoSummary? summary)
    {
        var stem = VirtualFileName(fileName);
        var dot = stem.LastIndexOf('.');
        if (dot > 0)
        {
            stem = stem[..dot];
        }

        var levelName = summary?.Segments.FirstOrDefault(segment => segment.LevelName.Length > 0)?.LevelName;
        if (levelName is null)
        {
            return stem;
        }
        return stem.Length == 0 ? levelName : $"{stem} — {levelName}";
    }

    private static void Append(StringBuilder query, string name, string value)
    {
        if (query.Length > 0)
        {
            query.Append('&');
        }
        query.Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
    }
}
