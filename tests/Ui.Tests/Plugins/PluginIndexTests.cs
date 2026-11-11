using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using CircuitRF.Design.Plugins;
using Xunit;

namespace CircuitRF.Ui.Tests.Plugins;

/// <summary>
/// brief-plg-6 §6 — the vetted list's own gate: the repository's <c>plugins/index.json</c> reads with no error. A schema
/// error, a duplicate <c>id</c>/<c>version</c>, a missing <c>sha256</c> and an asset not on github.com are each an error.
/// Commercial tool and PDK names are a review item, not a test: a test would need the list of names in the repository.
/// </summary>
public class PluginIndexTests
{
    [Fact]
    public void TheRepositorysList_ReadsWithNoError_AndIsTheOneCircuitRFShips()
    {
        byte[] json = File.ReadAllBytes(Path.Combine(RepoRoot(), "plugins", "index.json"));
        var (index, errors) = PluginIndex.Read(json);
        Assert.True(index is not null, string.Join("\n", errors));
        Assert.Equal(index!.Plugins.Select(p => p.Id), PluginIndex.Shipped().Plugins.Select(p => p.Id));
    }

    [Theory]
    [InlineData("schema", "schema: must be 1")]
    [InlineData("duplicate-id", "a.b: listed twice")]
    [InlineData("duplicate-version", "a.b 1.0.0: listed twice")]
    [InlineData("no-sha", "a.b 1.0.0.assets.any.sha256: missing")]
    [InlineData("not-github", "a.b 1.0.0.assets.any.url: https://example.com/a.zip is not on https://github.com")]
    public void EachBrokenRule_IsAnErrorNamingWhere(string rule, string error)
    {
        string sha = new('a', 64);
        string Asset(string url = "https://github.com/o/r/releases/download/v1/a.zip", bool withSha = true)
            => $$"""{ "any": { "url": "{{url}}"{{(withSha ? $", \"sha256\": \"{sha}\"" : "")}} } }""";
        string Version(string v = "1.0.0", string? asset = null) => $$"""{ "version": "{{v}}", "apiVersion": 1, "assets": {{asset ?? Asset()}} }""";
        string Plugin(string versions) => $$"""{ "id": "a.b", "name": "A", "versions": [ {{versions}} ] }""";

        string plugins = rule switch
        {
            "duplicate-id"      => Plugin(Version()) + "," + Plugin(Version("2.0.0")),
            "duplicate-version" => Plugin(Version() + "," + Version()),
            "no-sha"            => Plugin(Version(asset: Asset(withSha: false))),
            "not-github"        => Plugin(Version(asset: Asset("https://example.com/a.zip"))),
            _                   => Plugin(Version()),
        };
        string json = $$"""{ "schema": {{(rule == "schema" ? 2 : 1)}}, "plugins": [ {{plugins}} ] }""";

        var (index, errors) = PluginIndex.Read(Encoding.UTF8.GetBytes(json));
        Assert.Null(index);
        Assert.Contains(error, errors);
    }

    private static string RepoRoot([CallerFilePath] string here = "")
    {
        string? dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitRF.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir!;
    }
}
