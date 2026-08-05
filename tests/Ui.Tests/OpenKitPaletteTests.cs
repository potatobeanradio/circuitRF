using System;
using System.IO;
using System.Linq;
using CircuitRF.Core.Pdk;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// A4's stated gate: <b>referencing a kit yields a populated palette with correct pin counts, and a
/// zero-part import states why.</b>
///
/// <para><b>Why this is at the palette and not at the importer.</b> "The importer returned parts"
/// was already true for kits whose palette was empty — a part with no readable symbol is not
/// placeable and is dropped on the way through, so the two counts differ for real reasons. The claim
/// that matters to a user is that a tile appears and can be placed, and only the installer can
/// answer it.</para>
///
/// <para><b>The kit here is SYNTHETIC and is written by this test.</b> It is built in the shape this
/// phase targets — symbols together in one folder, one file per part; the behaviour behind them in a
/// netlist somewhere else entirely; a compiled model library in a third place; and no catalog
/// anywhere. Nothing in it names a supplier, a product or a model family.</para>
/// </summary>
public sealed class OpenKitPaletteTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "crf-openkit-" + Guid.NewGuid().ToString("N")[..8]);

    public OpenKitPaletteTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void Write(string relative, string text)
    {
        string full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    /// <summary>A symbol with <paramref name="pins"/> terminals and a default template.</summary>
    private static string Symbol(string type, string template, params string[] pins)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("v {version=1.2}");
        sb.AppendLine($"K {{type={type}");
        sb.AppendLine($"template=\"{template}\"}}");
        sb.AppendLine("L 4 -20 -20 20 20 {}");
        for (int i = 0; i < pins.Length; i++)
            sb.AppendLine($"B 5 {i * 20 - 2.5} -2.5 {i * 20 + 2.5} 2.5 {{name={pins[i]} dir=inout}}");
        return sb.ToString();
    }

    /// <summary>
    /// Stands up the kit shape this phase exists for. The three kinds of file are deliberately in
    /// three unrelated folders with no naming relationship between them — that is what the shape IS,
    /// and a discovery pass that assumed co-location would find nothing.
    /// </summary>
    private void WriteKit()
    {
        Write("libs.sym/transistor_a.sym", Symbol("nfet", "name=M1 model=fet_a w=1u l=0.13u", "g", "d", "s"));
        Write("libs.sym/transistor_b.sym", Symbol("pfet", "name=M1 model=fet_b w=2u l=0.13u", "g", "d", "s"));
        Write("libs.sym/resistor_a.sym",   Symbol("res",  "name=R1 model=res_a w=1u l=5u", "p", "n"));

        Write("libs.spice/devices.lib", """
            .subckt fet_a g d s w=1u l=0.13u
            M1 d g s s nfet_core w={w} l={l}
            .ends fet_a

            .subckt res_a p n w=1u l=5u
            R1 p n rmod w={w} l={l}
            .ends res_a
            """);

        Write("libs.tech/models.txt", "process description");
    }

    // ── P1 — the gate ─────────────────────────────────────────────────────────

    [Fact]
    public void P1_ReferencingTheKitYieldsAPopulatedPaletteWithCorrectPinCounts()
    {
        WriteKit();

        var report = PdkImporter.Import(_root);
        var outcome = PdkPartInstaller.Install(report);

        // Every symbol became a part, and every part became a placeable palette tile. The two counts
        // are asserted separately because they are different claims: parts can be discovered and
        // still be dropped for having no symbol to place.
        Assert.Equal(3, report.Parts.Count);
        Assert.Equal(3, outcome.Items.Count);
        Assert.Equal(3, outcome.SymbolsInstalled);
        Assert.Equal(0, outcome.OmittedNotPlaceable);

        Assert.Equal(["resistor_a", "transistor_a", "transistor_b"],
                     outcome.Items.Select(i => i.DisplayName).OrderBy(x => x, StringComparer.Ordinal));

        // Pin COUNTS, which is what the phase's gate names — a tile that places a symbol with the
        // wrong number of pins is worse than no tile, because the schematic looks connected.
        var built = outcome.Parts!.ToDictionary(p => p.PartId, StringComparer.Ordinal);
        Assert.Equal(3, built["transistor_a"].Symbol.Pins.Count);
        Assert.Equal(3, built["transistor_b"].Symbol.Pins.Count);
        Assert.Equal(2, built["resistor_a"].Symbol.Pins.Count);

        // …and the kit's own pin NAMES survive, because that is what a user wires by.
        Assert.Equal(["g", "d", "s"], built["transistor_a"].Symbol.Pins.Select(p => p.Name));
        Assert.Equal(["p", "n"],      built["resistor_a"].Symbol.Pins.Select(p => p.Name));
    }

    /// <summary>
    /// Pins must land on exact multiples of the connection grid or a wire will not attach to them.
    /// The rule is shared with the drawing reader rather than reimplemented, and this is what says
    /// so — a symbol from this path and one from a drawing put their pins in the same places.
    /// </summary>
    [Fact]
    public void P2_PinsLandOnTheConnectionGrid()
    {
        WriteKit();

        var outcome = PdkPartInstaller.Install(PdkImporter.Import(_root));
        var part = outcome.Parts!.Single(p => p.PartId == "transistor_a");

        foreach (var pin in part.Symbol.Pins)
        {
            Assert.Equal(0.0, pin.LocalX % DsnSymbolReader.PinGrid, 9);
            Assert.Equal(0.0, pin.LocalY % DsnSymbolReader.PinGrid, 9);
        }
    }

    // ── P3 — the kit's own grouping and interface ─────────────────────────────

    /// <summary>
    /// The palette groups the way the KIT does. The type word is the kit's own, so a user browsing
    /// sees the categories the kit's documentation uses rather than ones circuitRF guessed at.
    /// </summary>
    [Fact]
    public void P3_ThePartCarriesTheKitsOwnTypeWordAndItsOwnDefaults()
    {
        WriteKit();

        var report = PdkImporter.Import(_root);
        var fet = report.Parts.Single(p => p.Id == "transistor_a");

        Assert.Equal("nfet", fet.Category);

        // The symbol's template is the kit stating the interface WITH its defaults, so it outranks
        // a subcircuit matched by name — and it is the one that carries the model selection.
        Assert.Equal(["model", "w", "l"], fet.Parameters!.Select(p => p.Name));
        Assert.Equal("fet_a", fet.Parameters![0].DefaultExpression);
        Assert.True(fet.Parameters![0].IsText);
    }

    /// <summary>
    /// The netlist is in a different folder with no naming relationship to the symbols, and is in a
    /// dialect a separate reader handles. It still supplies a parameter interface for a part whose
    /// symbol declares no template — which is the join between this phase and the netlist reader.
    /// </summary>
    [Fact]
    public void P4_ASymbolWithNoTemplateFallsBackToTheNetlistsInterface()
    {
        Write("libs.sym/res_a.sym", Symbol("res", "name=R1", "p", "n"));
        Write("libs.spice/devices.lib", """
            .subckt res_a p n w=1u l=5u
            R1 p n 1k
            .ends res_a
            """);

        var report = PdkImporter.Import(_root);
        var part = Assert.Single(report.Parts);

        Assert.Equal(["w", "l"], part.Parameters!.Select(p => p.Name));
        Assert.Equal("1E-06", part.Parameters![0].DefaultExpression);
    }

    // ── P5 — a compiled model library is recognised wherever it lives ─────────

    /// <summary>
    /// The behaviour behind these parts is compiled, and it sits in a folder of its own. Recognising
    /// it is what turns "the parts do not simulate" into a message at IMPORT time rather than a
    /// failure at Run.
    /// </summary>
    [Fact]
    public void P5_ACompiledModelLibraryElsewhereInTheTreeIsStillRecognised()
    {
        WriteKit();
        Write("libs.osdi/devices.so", "not really a library, but named as one");

        var report = PdkImporter.Import(_root);

        Assert.Contains(report.Assets, a => a.Kind == PdkAssetKind.ModelData
                                         && a.RelativePath.EndsWith("devices.so", StringComparison.Ordinal));
        Assert.Equal(3, report.Parts.Count);      // and it did not disturb part discovery
    }

    // ── P6 — the zero-part case says why ──────────────────────────────────────

    /// <summary>
    /// A kit whose symbols circuitRF READ, yielding nothing, fails for one reason only: none of them
    /// declares a terminal. Sending the user to look at the folder layout — which is what the
    /// cell-database message does — would be the wrong place entirely.
    /// </summary>
    [Fact]
    public void P6_AZeroPartImportNamesThisShapeSpecifically()
    {
        Write("libs.sym/decoration.sym", "K {type=title}\nL 4 0 0 10 0 {}\n");

        var report = PdkImporter.Import(_root);

        Assert.Empty(report.Parts);
        Assert.Contains(report.Findings, f => f.Summary.Contains("declare no terminals",
                                                                 StringComparison.Ordinal));
    }
}
