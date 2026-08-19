using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using CircuitRF.WBond.Mom;
using RfCore;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// brief-wbond-mom-w2 §7.1/§7.2/§7.5 — the <b>Model</b> option on the Touchstone export, and the two
/// things that must not drift: the port map the two engines publish on, and the lumped file's own bits.
/// </summary>
public class WBondMomExportTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-wbond-mom-" + Guid.NewGuid().ToString("N")[..8]);

    public WBondMomExportTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static WBondDesign Design(int arrays = 1, int wires = 2)
    {
        long loopNm = WBondUnits.ToNm(20.0, WBondUnit.Mil);
        long diameterNm = WBondUnits.ToNm(1.0, WBondUnit.Mil);

        var design = new WBondDesign();
        for (int a = 0; a < arrays; a++)
        {
            var array = new WireArray { Name = $"G{a + 1}" };
            for (int w = 0; w < wires; w++)
            {
                double y = a * 40.0 + w * 8.0;
                array.Wires.Add(LoopShape.CreateSeedWire(
                    Point3.Mils(0, y, 4), Point3.Mils(80, y, 2), diameterNm, "Gold", loopHeightNm: loopNm));
            }
            design.Arrays.Add(array);
        }
        return design;
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine([dir!.FullName, .. parts]));
    }

    // ---------------------------------------------------------------- the port map

    /// <summary>
    /// <b>WM-1 §9.9's deferred assertion.</b> The two engines publish on the same terminals, in the same
    /// order, or every comparison in this tranche is comparing two different things and the exported
    /// files disagree about which port is which.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void TheMeshAndTheExport_NameTheSamePorts_InTheSameOrder(int arrays)
    {
        var design = Design(arrays);

        Assert.Equal(
            WBondTouchstoneExport.PortNames(design, WBondPortBasis.Terminals).ToArray(),
            WireMomMesh.TerminalNamesFor(design));
    }

    // ---------------------------------------------------------------- the file

    [Fact]
    public void ADistributedExport_WritesAReadableTouchstone_WithTheRightPortLabels()
    {
        var design = Design(arrays: 2, wires: 1);
        var options = new WBondTouchstoneExport.Options(
            StartHz: 1e9, StopHz: 10e9, Points: 2,
            Model: WBondNetworkModel.Distributed, SegmentsPerWire: 12);

        string baseName = Path.Combine(_root, "distributed");
        var result = WBondTouchstoneExport.Export(design, options, baseName);

        string path = Assert.Single(result.WrittenPaths);
        Assert.EndsWith(".s4p", path, StringComparison.OrdinalIgnoreCase);

        var snp = TouchstoneIO.ReadFile(path);
        Assert.Equal(4, snp.Ports);
        Assert.Equal(2, snp.FrequencyCount);

        string text = File.ReadAllText(path);
        foreach (string name in new[] { "G1.i", "G1.o", "G2.i", "G2.o" })
            Assert.Contains(name, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// §7.2 — the header says which engine wrote the file, and with what. A <c>.snp</c> outlives the
    /// session that made it.
    /// </summary>
    [Fact]
    public void TheHeader_SaysWhichModelWroteIt_AndAtWhatSize()
    {
        var design = Design(arrays: 1, wires: 2);

        var distributed = WBondTouchstoneExport.HeaderComments(design, new WBondTouchstoneExport.Options(
            Model: WBondNetworkModel.Distributed, SegmentsPerWire: 12));
        int unknowns = WireMomMesh.Predict(
            design, WireMomSettings.Default with { TargetSegmentsPerWire = 12 }).Segments;

        string line = Assert.Single(distributed, l => l.StartsWith("Model: distributed", StringComparison.Ordinal));
        Assert.Contains("12 segments per wire", line, StringComparison.Ordinal);
        Assert.Contains($"{unknowns} current unknowns", line, StringComparison.Ordinal);

        var lumped = WBondTouchstoneExport.HeaderComments(design, new WBondTouchstoneExport.Options());
        Assert.Contains(lumped, l => l.StartsWith("Model: lumped (analytic)", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- the refusal

    [Fact]
    public void Distributed_OnTheArrayPairBasis_Refuses_AndSaysWhy()
    {
        var design = Design();
        var options = new WBondTouchstoneExport.Options(
            PortBasis: WBondPortBasis.ArrayPairs, Model: WBondNetworkModel.Distributed);

        var ex = Assert.Throws<InvalidOperationException>(
            () => WBondTouchstoneExport.BuildNetwork(design, [1e9], options));

        Assert.Contains("terminal basis only", ex.Message, StringComparison.Ordinal);
        Assert.Contains("floating pair", ex.Message, StringComparison.Ordinal);

        // And the lumped model on the same basis is still perfectly legal — the refusal is about the
        // combination, not about either half.
        WBondTouchstoneExport.BuildNetwork(design, [1e9], options with { Model = WBondNetworkModel.Lumped });
    }

    // ---------------------------------------------------------------- the lumped path is untouched

    /// <summary>
    /// <b>The lumped path must be bit-identical to what it was.</b> Its code is not shared with the
    /// distributed one and is not to be — a refactor that keeps the round-trip tests passing while
    /// changing the last bits is the kind of change nobody catches for a year. So the default option
    /// record still selects it, and the default file is byte-for-byte what the old default wrote apart
    /// from the one added header line.
    /// </summary>
    [Fact]
    public void TheDefaultModelIsLumped_AndItsNumbersAreUnchanged()
    {
        var design = Design(arrays: 2, wires: 2);
        Assert.Equal(WBondNetworkModel.Lumped, new WBondTouchstoneExport.Options().Model);

        var freqs = new[] { 1e9, 5e9, 20e9 };
        var viaOptions = WBondTouchstoneExport.BuildNetwork(design, freqs, new WBondTouchstoneExport.Options());
        var direct = WBondTouchstoneExport.TerminalAdmittances(design, freqs);

        for (int fi = 0; fi < freqs.Length; fi++)
        {
            var s = RFNetwork.YToS(direct[fi], new System.Numerics.Complex(50.0, 0.0));
            for (int i = 0; i < s.RowCount; i++)
                for (int j = 0; j < s.ColCount; j++)
                    Assert.Equal(s[i, j], viaOptions.Matrices[fi][i, j]);
        }
    }

    // ---------------------------------------------------------------- both menu trees

    /// <summary>
    /// <b>Both</b> menu trees carry the item, or it appears on one platform only —
    /// <c>WBondMenuView.axaml</c> hand-mirrors a <c>NativeMenu</c> (macOS) and an in-window
    /// <c>Menu</c> (everywhere else), and adding to one is the classic miss.
    ///
    /// <para>Comments are stripped first: a source-scan test in this repository has been fooled by
    /// commented-out markup before.</para>
    /// </summary>
    [Fact]
    public void BothMenuTrees_CarryTheCompareItem()
    {
        var doc = XDocument.Parse(ReadRepoFile("src", "Ui", "Views", "WBond", "WBondMenuView.axaml"));
        doc.DescendantNodes().OfType<XComment>().ToList().ForEach(c => c.Remove());

        var headers = doc.Descendants()
            .Where(e => e.Name.LocalName is "NativeMenuItem" or "MenuItem")
            .Select(e => (Name: e.Name.LocalName, Header: (string?)e.Attribute("Header") ?? "",
                          Command: (string?)e.Attribute("Command") ?? ""))
            .ToList();

        Assert.Contains(headers, h => h.Name == "NativeMenuItem"
                                      && h.Header.Contains("Compare Distributed Model", StringComparison.Ordinal)
                                      && h.Command.Contains("CompareDistributedModelCommand", StringComparison.Ordinal));

        Assert.Contains(headers, h => h.Name == "MenuItem"
                                      && h.Header.Replace("_", "").Contains("Compare Distributed Model", StringComparison.Ordinal)
                                      && h.Command.Contains("CompareDistributedModelCommand", StringComparison.Ordinal));

        // AND THE NAME BOTH TREES BIND TO ACTUALLY EXISTS. A source scan alone would pass on a typo:
        // Avalonia resolves a missing command to nothing and the menu item is simply dead, with no
        // error anywhere. The generated command and the hook are asserted here instead.
        var vm = typeof(WBondMenuViewModel);
        Assert.NotNull(vm.GetProperty("CompareDistributedModelCommand"));
        Assert.NotNull(vm.GetProperty("CompareDistributedModelHook"));

        bool invoked = false;
        var menus = new WBondMenuViewModel { CompareDistributedModelHook = () => invoked = true };
        menus.CompareDistributedModelCommand.Execute(null);
        Assert.True(invoked);
    }

    /// <summary>
    /// The editor view carries its own entry point too — the standalone shell's menu is not reachable
    /// from circuitRF, where the wBond editor is a document tab under the workspace's own menu bar.
    /// </summary>
    [Fact]
    public void TheEditorToolbar_CarriesTheCompareEntryPoint_SoBothBinariesHaveOne()
    {
        string xaml = ReadRepoFile("src", "Ui", "Views", "WBond", "WBondEditorView.axaml");
        var doc = XDocument.Parse(xaml);
        doc.DescendantNodes().OfType<XComment>().ToList().ForEach(c => c.Remove());

        Assert.Contains(doc.Descendants().Where(e => e.Name.LocalName == "Button"),
            b => ((string?)b.Attribute("Click") ?? "").Contains("OnCompareDistributedModel", StringComparison.Ordinal));

        string code = Regex.Replace(
            ReadRepoFile("src", "Ui", "Views", "WBond", "WBondEditorView.Touchstone.cs"),
            @"//.*?$|/\*.*?\*/", "", RegexOptions.Multiline | RegexOptions.Singleline);

        Assert.Contains("CompareDistributedModelAsync", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The bug the owner reported twice.</b> A <c>.clay</c> with a <c>.wBond</c> beside it (WB40) is
    /// how a wirebond is normally worked on inside circuitRF, and <b>there is no
    /// <c>WBondEditorView</c> anywhere in that document</b> — the wire tools live in
    /// <c>LayoutEditorView</c>'s own toolbar. That group ended at Transform, so neither Export
    /// Touchstone nor Compare was reachable from the editor most of this work happens in.
    /// </summary>
    [Fact]
    public void TheHostedLayoutWireGroup_CarriesExportAndCompare_ToTheRightOfTransform()
    {
        var doc = XDocument.Parse(ReadRepoFile("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml"));
        doc.DescendantNodes().OfType<XComment>().ToList().ForEach(c => c.Remove());

        var buttons = doc.Descendants()
            .Where(e => e.Name.LocalName is "Button" or "ToggleButton")
            .Select(e => (Name: (string?)e.Attribute("{http://schemas.microsoft.com/winfx/2006/xaml}Name") ?? "",
                          Click: (string?)e.Attribute("Click") ?? ""))
            .ToList();

        int transform = buttons.FindIndex(b => b.Name == "WireTransformBtn");
        int export = buttons.FindIndex(b => b.Name == "WireExportTouchstoneBtn");
        int compare = buttons.FindIndex(b => b.Name == "WireCompareModelBtn");

        Assert.True(transform >= 0, "WireTransformBtn is gone — this test's anchor no longer exists.");
        Assert.True(export > transform, "Export Touchstone must sit to the RIGHT of Transform Wires.");
        Assert.True(compare > export, "Compare must sit to the right of Export Touchstone.");

        Assert.Equal("OnWireExportTouchstone", buttons[export].Click);
        Assert.Equal("OnWireCompareDistributedModel", buttons[compare].Click);
    }

    /// <summary>
    /// <b>A button added to that group without being added to its gate is visible on every ordinary
    /// layout in the application.</b> The group is shown only on a wirebond cell, from code-behind, one
    /// assignment per control — so a new member is exactly one line away from being permanently on
    /// screen, with nothing failing.
    /// </summary>
    [Fact]
    public void TheNewWireButtons_AreGatedWithTheRestOfTheWireGroup()
    {
        string code = Regex.Replace(
            ReadRepoFile("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml.cs"),
            @"//.*?$|/\*.*?\*/", "", RegexOptions.Multiline | RegexOptions.Singleline);

        int start = code.IndexOf("private void UpdateWirePanelButtonStates()", StringComparison.Ordinal);
        Assert.True(start > 0, "UpdateWirePanelButtonStates is gone — the wire group's gate moved.");
        string body = code[start..code.IndexOf("SubscribeToPanelVisibility", start, StringComparison.Ordinal)];

        foreach (string name in new[] { "WireTransformBtn", "WireExportTouchstoneBtn", "WireCompareModelBtn" })
            Assert.Contains($"{name}.IsVisible = show;", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both editors run <b>one</b> implementation. The file-picker flow, the extension handling and the
    /// refusal reporting are subtle enough that two copies would drift, and the repository's own rule is
    /// to route every entry point through the same accessor.
    /// </summary>
    [Fact]
    public void BothEditors_RouteThroughTheSamePublishCommands()
    {
        foreach (var parts in new[]
        {
            new[] { "src", "Ui", "Views", "Layout", "LayoutEditorView.axaml.cs" },
            ["src", "Ui", "Views", "WBond", "WBondEditorView.Touchstone.cs"],
        })
        {
            string code = Regex.Replace(ReadRepoFile(parts), @"//.*?$|/\*.*?\*/", "",
                                        RegexOptions.Multiline | RegexOptions.Singleline);

            Assert.Contains("WBondPublishCommands.ExportTouchstoneAsync", code, StringComparison.Ordinal);
            Assert.Contains("WBondPublishCommands.CompareDistributedModelAsync", code, StringComparison.Ordinal);

            // The write itself must exist in exactly ONE place, and this is not it. (A bare
            // SaveFilePickerAsync check would be wrong here: LayoutEditorView legitimately has its own,
            // for GDSII and DXF.)
            Assert.DoesNotContain("WBondTouchstoneExport.Export(", code, StringComparison.Ordinal);
            Assert.DoesNotContain("WBondMomCompareDialog.ShowAsync", code, StringComparison.Ordinal);
        }
    }
}
