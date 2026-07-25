using System.Net.Http;
using System.Text.Json.Nodes;
using PakStudio.Core.Playback;
using PakStudio.Core.Preview;
using Xunit;

namespace PakStudio.Tests;

public sealed class DemoPlaybackHandoffTests
{
    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("demos/run 1.dem", "run_1.dem")]
    [InlineData(@"C:\demos\run.dem", "run.dem")]
    [InlineData("...", "demo.dem")]
    [InlineData("", "demo.dem")]
    [InlineData("e1m3+extra-v2.dem", "e1m3+extra-v2.dem")]
    public void VirtualFileNameStripsPathsAndUnsafeCharacters(string input, string expected)
    {
        Assert.Equal(expected, DemoPlaybackHandoff.VirtualFileName(input));
    }

    [Fact]
    public void LaunchUriCarriesDemoMetadata()
    {
        var summary = new QuakeDemoSummary(
            "15",
            [new QuakeDemoSegment("e1m3", "the Necropolis", 74.5)],
            string.Empty,
            1,
            0,
            74.5,
            975,
            [],
            true,
            false);
        var demo = new DemoPlaybackAsset("demo1.dem", [1, 2, 3]);
        var source = new Uri("http://127.0.0.1:5555/token/demo1.dem");

        var uri = DemoPlaybackHandoff.BuildLaunchUri(demo, [], summary, new[] { source });
        var query = ParseQuery(uri);

        Assert.StartsWith(DemoPlaybackHandoff.PlayerUrl, uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal(source.AbsoluteUri, query["source"]);
        Assert.Equal("demo1.dem", query["file"]);
        Assert.Equal("e1m3", query["maps"]);
        Assert.Equal("74.50", query["duration"]);
        Assert.Equal("demo1 — the Necropolis", query["title"]);
        Assert.False(query.ContainsKey("packages"));
    }

    [Fact]
    public void LaunchUriDescribesArchivePackages()
    {
        var summary = new QuakeDemoSummary(
            "666",
            [
                new QuakeDemoSegment("start", "Entrance", 10),
                new QuakeDemoSegment("e1m1", "Slipgate", 20),
            ],
            "quoth",
            1,
            0,
            30,
            100,
            [],
            true,
            false);
        var demo = new DemoPlaybackAsset("run.dem", [1]);
        var archive = new DemoPlaybackAsset("pak0.pak", [2]);
        var demoSource = new Uri("http://127.0.0.1:5555/a/run.dem");
        var packageSource = new Uri("http://127.0.0.1:5555/b/pak0.pak");

        var uri = DemoPlaybackHandoff.BuildLaunchUri(
            demo,
            [archive],
            summary,
            new[] { demoSource, packageSource });
        var query = ParseQuery(uri);

        Assert.Equal("start,e1m1", query["maps"]);

        var packages = JsonNode.Parse(query["packages"])!.AsArray();
        Assert.Single(packages);
        Assert.Equal("pak0.pak", (string?)packages[0]!["file"]);
        Assert.Equal(packageSource.AbsoluteUri, (string?)packages[0]!["source"]);
        Assert.Equal(
            new[] { "start", "e1m1" },
            packages[0]!["maps"]!.AsArray().Select(node => (string?)node).ToArray());
    }

    [Fact]
    public void LaunchUriRejectsPayloadsOverTheSessionLimit()
    {
        using var server = new LoopbackAssetServer();
        var demo = new DemoPlaybackAsset("huge.dem", new byte[8]);
        var oversized = new DemoPlaybackAsset(
            "big.pak",
            new byte[DemoPlaybackHandoff.MaximumSessionBytes]);

        Assert.Throws<DemoPlaybackException>(() =>
            DemoPlaybackHandoff.BuildLaunchUri(demo, [oversized], null, server));
        Assert.False(server.IsRunning);
    }

    [Fact]
    public async Task ServerReturnsPublishedAssetAndNothingElse()
    {
        using var server = new LoopbackAssetServer();
        var payload = "demo bytes"u8.ToArray();
        var urls = server.Publish(
            [new DemoPlaybackAsset("run.dem", payload)],
            TimeSpan.FromMinutes(1));

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var response = await client.GetAsync(
            urls[0],
            TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(
            payload,
            await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            DemoPlaybackHandoff.PlayerOrigin,
            string.Join(string.Empty, response.Headers.GetValues("Access-Control-Allow-Origin")));

        // Nothing but the published token path is reachable; there is no document root.
        using var missing = await client.GetAsync(
            new Uri($"http://127.0.0.1:{server.BoundPort}/run.dem"),
            TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task StoppingTheServerRevokesPublishedAssets()
    {
        var server = new LoopbackAssetServer();
        var urls = server.Publish(
            [new DemoPlaybackAsset("run.dem", [9])],
            TimeSpan.FromMinutes(1));
        Assert.True(server.IsRunning);

        server.Stop();
        Assert.False(server.IsRunning);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var response = await client.GetAsync(
                urls[0],
                TestContext.Current.CancellationToken);
            Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        }
        catch (HttpRequestException)
        {
            // Refusing the connection outright is the expected result.
        }
    }

    private static Dictionary<string, string> ParseQuery(Uri uri)
    {
        // Split before unescaping, so a value containing & or = stays intact.
        var query = uri.GetComponents(UriComponents.Query, UriFormat.UriEscaped);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0)
            {
                result[Uri.UnescapeDataString(pair[..separator])] =
                    Uri.UnescapeDataString(pair[(separator + 1)..]);
            }
        }
        return result;
    }
}
