using System.Linq;
using CircuitRF.Design.Plugins;
using Xunit;

namespace CircuitRF.Ui.Tests.Plugins;

/// <summary>brief-plg-2 gate 1 — the layout tree: every validation refusal, the unknown kind that is NOT a refusal,
/// the per-placement size hint, and the default panel for a manifest with no <c>ui</c>.</summary>
public class PluginLayoutTests
{
    private const string Params = """
        "params": [
          { "name": "f0", "kind": "quantity", "unit": "Hz", "default": "1 GHz", "chars": 12 },
          { "name": "n",  "kind": "int", "default": 3 },
          { "name": "out", "kind": "readout" }
        ]
        """;

    private static string Manifest(string? ui) => $$"""
        { "apiVersion": 1, "id": "t.layout", "name": "Layout Test", "version": "1",
          "run": { "python": { "entry": "x.py" } }, "kind": "panel",
          {{Params}}{{(ui is null ? "" : ", \"ui\": " + ui)}} }
        """;

    private static PluginManifest Bare() => PluginManifestReader.Read(Manifest(null)).Manifest!;

    /// <summary>The manifest reader refuses the panel, and the refusal names the place in the tree.</summary>
    private static string Refused(string layout, string where)
    {
        var (manifest, d) = PluginManifestReader.Read(Manifest($$"""{ "layout": {{layout}} }"""));
        Assert.Null(manifest);
        var error = Assert.Single(d, x => x.IsError);
        Assert.StartsWith(where, error.Field);
        return error.Message;
    }

    [Fact]
    public void AParamLeafNamingNoDeclaredParameterIsRefused()
        => Assert.Contains("'f1' is not a declared parameter", Refused("""{ "column": [ { "param": "f1" } ] }""", "ui.layout.column[0].param"));

    [Fact]
    public void AParameterPlacedTwiceIsRefused()
        => Assert.Contains("placed twice", Refused("""{ "column": [ { "param": "n" }, { "param": "n" } ] }""", "ui.layout.column[1].param"));

    [Fact]
    public void AReadoutNamingAParameterThatIsNotAReadoutIsRefused()
        => Assert.Contains("not a readout", Refused("""{ "column": [ { "readout": "n" } ] }""", "ui.layout.column[0].readout"));

    [Fact]
    public void TwoViewsWithOneNameAreRefused()
        => Assert.Contains("view 'r' is placed twice",
            Refused("""{ "tabs": [ { "title": "A", "view": "r", "kind": "plot" }, { "title": "B", "view": "r", "kind": "smith" } ] }""", "ui.layout.tabs[1]"));

    [Fact]
    public void ASplitWhoseSizesDifferFromItsChildrenIsRefused()
        => Assert.Contains("2 sizes for 3 children",
            Refused("""{ "split": "horizontal", "sizes": [1, 2], "children": [ { "note": "a" }, { "note": "b" }, { "note": "c" } ] }""", "ui.layout.sizes"));

    [Theory]
    [InlineData("0")]
    [InlineData("2.5")]
    [InlineData("\"wide\"")]
    public void ACharsOnAParamLeafThatIsNotAWholeNumberFromOneIsRefused(string chars)
        => Assert.Contains("whole number", Refused($$"""{ "column": [ { "param": "f0", "chars": {{chars}} } ] }""", "ui.layout.column[0].chars"));

    [Fact]
    public void AnUnknownKindIsAnEmptyFrameNotARefusalOfThePanel()
    {
        var (manifest, d) = PluginManifestReader.Read(Manifest("""{ "layout": { "column": [ { "param": "f0" }, { "dial": "f0" }, { "view": "v", "kind": "hologram" } ] } }"""));
        Assert.NotNull(manifest);
        Assert.All(d, x => Assert.False(x.IsError));
        var (layout, _) = PluginLayoutReader.Read(manifest!);
        Assert.Equal("dial", Assert.Single(layout!.Nodes.OfType<PluginUnknownNode>()).What);
        Assert.False(Assert.Single(layout.Views).IsKnownKind);
    }

    [Fact]
    public void AParamLeafsCharsOverridesTheDeclarationsForThatPlacementOnly()
    {
        var (manifest, _) = PluginManifestReader.Read(Manifest("""{ "layout": { "column": [ { "param": "f0", "chars": 4 }, { "param": "n" } ] } }"""));
        var leaves = PluginLayoutReader.Read(manifest!).Layout!.Nodes.OfType<PluginParamLeaf>().ToList();
        Assert.Equal(4, leaves[0].ClampedChars);
        Assert.Equal(12, manifest!.Params[0].Chars);          // the declaration keeps its own
    }

    [Fact]
    public void WithNoUiTheDefaultPanelIsBuilt()
    {
        var (layout, d) = PluginLayoutReader.Read(Bare());
        Assert.Empty(d);
        Assert.True(layout!.IsDefault);
        var split = Assert.IsType<PluginSplitNode>(layout.Root);
        var column = Assert.IsType<PluginStackNode>(split.Children[0]);
        Assert.Collection(column.Children,
            n => Assert.Equal("f0", Assert.IsType<PluginParamLeaf>(n).Param.Name),
            n => Assert.Equal("n", Assert.IsType<PluginParamLeaf>(n).Param.Name),
            n => Assert.Equal("out", Assert.IsType<PluginReadoutLeaf>(n).Param.Name),
            n => Assert.IsType<PluginMessagesLeaf>(n),
            n => Assert.True(Assert.IsType<PluginButtonLeaf>(n).IsCommit));
        Assert.True(Assert.IsType<PluginTabsNode>(split.Children[1]).FromFirstReply);
    }

    [Fact]
    public void TheCalculatorExampleReadsAsAPanel()
    {
        var entry = PluginTestKit.Example("calculator");
        Assert.True(entry.Manifest is not null, string.Join("; ", entry.Diagnostics));
        var (layout, d) = PluginLayoutReader.Read(entry.Manifest!);
        Assert.Empty(d);
        Assert.False(layout!.IsDefault);
        Assert.Equal((460.0, 600.0), layout.Size);
    }
}
