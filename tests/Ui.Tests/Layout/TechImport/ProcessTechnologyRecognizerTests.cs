// The importer-facing half of C0: a kit carrying process data has to SAY SO, and the one entry
// point that turns it into a technology has to be reachable from both menu surfaces.
//
// The recognisers are exercised directly rather than through PdkFormatRegistry, which is
// process-wide static — registering from a test would leave them installed for every other test in
// the run, and a kit fixture could then classify differently depending on test order.

using System.Runtime.CompilerServices;
using CircuitRF.Core.Pdk;
using CircuitRF.Ui.Layout.TechImport;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout.TechImport;

public class ProcessTechnologyRecognizerTests
{
    private static string ReadRepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root.");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }

    [Fact]
    public void AStackDescriptionIsRecognisedByItsGrammar_NotItsExtension()
    {
        var r = new StackDescriptionRecognizer();

        // Kits spell this file .itf, .dat and .txt — sometimes several ways in one delivery — and
        // those extensions are claimed by unrelated formats elsewhere.
        foreach (string name in new[] { "rc/typical.itf", "extract/typ.dat", "data/stack.txt" })
        {
            var asset = r.Recognize(name, () => ProcessStackReaderTests.Stack);
            Assert.NotNull(asset);
            Assert.Equal(PdkAssetKind.LayerTechnology, asset!.Kind);
            Assert.Equal(PdkAssetSupport.Supported, asset.Support);
            Assert.Contains("Import ▸ Technology", asset.Detail);
        }
    }

    [Fact]
    public void ALayerTableIsRecognisedOnAPartialRead()
    {
        // The importer hands a recogniser the first few kilobytes only, and a truncated XML document
        // does not parse — so this has to be decided on the head, not by parsing.
        string table = LayerPropertiesReaderTests.Table;
        string head  = table[..(table.IndexOf("</source>", StringComparison.Ordinal) + 9)];
        Assert.DoesNotContain("</layer-properties>", head);
        Assert.False(LayerPropertiesReader.LooksLikeLayerPropertiesFile(head),
                     "the strict predicate must reject a truncated document — that is why the " +
                     "head-level one exists");

        var asset = new LayerTableRecognizer().Recognize("tech/table.lyp", () => head);

        Assert.NotNull(asset);
        Assert.Equal(PdkAssetKind.LayerTechnology, asset!.Kind);
        Assert.Equal(PdkAssetSupport.Supported, asset.Support);
    }

    [Fact]
    public void NeitherRecogniserClaimsSomethingItCannotRead()
    {
        var stack = new StackDescriptionRecognizer();
        var table = new LayerTableRecognizer();

        foreach (string text in new[]
                 {
                     "",
                     "just some prose about a TECHNOLOGY and its conductors",
                     ".subckt amp in out\n.ends\n",
                     "<other-document><source>1/0</source></other-document>",
                 })
        {
            Assert.Null(stack.Recognize("a.txt", () => text));
            Assert.Null(table.Recognize("a.xml", () => text));
        }

        // And neither claims the other's format.
        Assert.Null(table.Recognize("a", () => ProcessStackReaderTests.Stack));
        Assert.Null(stack.Recognize("a", () => LayerPropertiesReaderTests.Table));
    }

    [Fact]
    public void TheApplicationRegistersThem()
    {
        // Registration cannot be asserted through the registry without leaving the recognisers
        // installed for the rest of the run, so the call site is pinned instead — the same way this
        // repository already pins other wiring that cannot be constructed headlessly.
        var source = ReadRepoFile("src/Ui/App.axaml.cs");
        Assert.Contains("ProcessTechnologyRecognizers.RegisterOnce()", source);
    }

    [Fact]
    public void ImportTechnologyIsReachableFromBothMenuSurfaces()
    {
        // This repository keeps the in-window Menu and the macOS NativeMenu in step by hand, so an
        // entry added to one and forgotten in the other is invisible on the other platform.
        var xaml = ReadRepoFile("src/Ui/Views/WorkspaceWindow.axaml");

        Assert.Equal(2, CountOccurrences(xaml, "ImportTechnologyCommand"));
        Assert.Contains("<NativeMenuItem Header=\"Technology…\"", xaml);
        Assert.Contains("Header=\"_Technology…\"", xaml);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int n = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            n++;
        return n;
    }
}
