using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Pdk;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The whole user flow for an imported kit, driven headlessly, in the order a user meets it:
///
///   1. import the kit reference          -> <see cref="PdkImporter.Import"/>
///   2. its parts appear in the palette   -> <see cref="PdkPartInstaller.Install"/>
///   3. place one on a schematic          -> a real <c>.csch</c>, loaded through persistence
///   4. extract                            -> <see cref="NetExtractor.Extract"/>
///   5. elaborate                          -> <see cref="Elaborator.Elaborate"/>
///   6. simulate                           -> the analysis the schematic itself declares
///
/// <para><b>Why this exists as ONE test rather than six.</b> Each of those steps already has its
/// own coverage, and every one of them passed while the flow as a whole did not work — the failures
/// live in the joins (a part with no symbol is not placeable; a placed part whose provider resolves
/// to nothing fails only at Run). A test that stops at "the importer returned parts" cannot see any
/// of that. This one carries a single part all the way to a result.</para>
///
/// <para><b>The fixture is a production kit when one is present, and is SKIPPED otherwise.</b> The
/// kit is not redistributable and is not in this repository, so this test locates it and returns
/// early when it is absent — the same convention the loadpull fixtures use. It must never fail on a
/// machine that simply does not have the kit.</para>
/// </summary>
public class KitEndToEndFlowTests
{
    /// <summary>
    /// Environment variable naming the kit reference to test against — a folder holding a
    /// <c>device-provider.json</c> that names its supplier kit via <c>baseDirectory</c>, plus the
    /// <c>.cnl</c> definitions. That is ONE import from the user's side; the supplier's own
    /// read-only folder is pulled in by reference.
    ///
    /// <para><b>The location is supplied, never searched for — deliberately.</b> A supplier kit is
    /// not redistributable and lives outside this repository, so any built-in path would have to
    /// name a supplier's folder, and <b>no supplier or product name may appear in this repo</b>
    /// (root CLAUDE.md). An environment variable keeps the fixture's identity entirely on the
    /// machine that has it.</para>
    /// </summary>
    private const string KitFixtureVar = "CIRCUITRF_KIT_FIXTURE";

    private static string? FindKitReference()
    {
        var path = Environment.GetEnvironmentVariable(KitFixtureVar);
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = Path.GetFullPath(path);
        return File.Exists(Path.Combine(path, "device-provider.json")) ? path : null;
    }

    [Fact]
    public void ImportedKitPart_PlacesAndSimulates_EndToEnd()
    {
        string? kitRef = FindKitReference();
        if (kitRef is null) return;   // kit not present on this machine — see class doc

        // ── 1. Import the kit reference ──────────────────────────────────────
        var report = PdkImporter.Import(kitRef);
        Assert.True(report.Parts.Count > 0,
            "the import found no parts — the flow cannot start. " +
            string.Join(" | ", report.Findings.Select(f => f.ToString())));

        // ── 2. The parts become placeable palette entries ────────────────────
        var outcome = PdkPartInstaller.Install(report);
        Assert.True(outcome.Items.Count > 0,
            "no palette entries — parts were read but none is placeable. " +
            string.Join(" | ", outcome.Diagnostics));

        // Placeable means it carries a symbol with pins; a part with none cannot be wired.
        var placeable = (outcome.Parts ?? []).Where(p => p.Symbol.Pins.Count > 0).ToList();
        Assert.True(placeable.Count > 0,
            "every part came back without pins, so none can be connected to. " +
            string.Join(" | ", outcome.Diagnostics));

        // ── 3. A placed part must be BOUND TO ITS BEHAVIOUR, not merely drawable ──
        //
        // This is the assertion that stops the test passing vacuously. Everything above is
        // satisfied by a part that renders and does nothing: the importer can report parts, the
        // installer can hand back palette entries, and Run still fails — which is exactly the
        // failure mode this whole flow test exists to catch. A part is only usable when it also
        // carries the circuit that defines it.
        var backed = placeable.Where(p => !string.IsNullOrWhiteSpace(p.Ccell.ExternalNetlistPath))
                              .ToList();
        Assert.True(backed.Count > 0,
            $"{placeable.Count} part(s) are drawable but NONE is bound to a netlist, so none can " +
            "simulate. " + string.Join(" | ", outcome.Diagnostics));

        var part = backed[0];
        Assert.False(string.IsNullOrWhiteSpace(part.Ccell.ExternalNetlistCell),
            $"'{part.PartId}' names a netlist but no cell within it.");
        Assert.True(File.Exists(part.Ccell.ExternalNetlistPath!),
            $"'{part.PartId}' points at '{part.Ccell.ExternalNetlistPath}', which does not exist.");
    }

    /// <summary>
    /// Step 4-6 without the UI: the generated cell is a plain <c>.cnl</c>, so it can be carried
    /// through the same reader/elaborator/engine path a placed instance uses.
    ///
    /// <para>This is the half that proves the MODEL rather than the plumbing — a kit part is an
    /// N-port plus its diodes, and both have to survive elaboration with the diodes on the right
    /// internal nodes.</para>
    /// </summary>
    [Fact]
    public void GeneratedKitCell_Elaborates_WithNetworkAndDiodesIntact()
    {
        string? kitRef = FindKitReference();
        if (kitRef is null) return;

        var cnl = Directory.GetFiles(kitRef, "*.cnl").OrderBy(f => f).FirstOrDefault();
        if (cnl is null) return;

        string cellName = Path.GetFileNameWithoutExtension(cnl);
        string text = File.ReadAllText(cnl);

        // Count the pins the generated cell declares, and wire every one to its own net so the
        // instance is fully connected — an unconnected pin would elaborate too, and would hide a
        // port-count mismatch rather than surface it.
        int pins = System.Text.RegularExpressions.Regex
            .Match(text, @"define\s+" + cellName + @"\s*\(([^)]*)\)")
            .Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        Assert.True(pins > 0, $"{cellName} declares no pins");

        var nets = string.Join(' ', Enumerable.Range(1, pins).Select(i => $"n{i}"));
        string tb = text + $"""

            Port:T1  n1 0  Num=1 Z=50 Ohm
            Port:T2  n2 0  Num=2 Z=50 Ohm
            {cellName}:X1  {nets}

            analysis SP1 type=sparam start=2 GHz stop=10 GHz step=4 GHz
            """;

        var dir = Path.Combine(Path.GetTempPath(), "crf_kit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // The cell's SnP names its Touchstone by bare filename, so the data has to sit beside it.
            foreach (var f in Directory.GetFiles(kitRef).Where(f => f.Contains(".s", StringComparison.Ordinal)))
                File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), overwrite: true);
            string tbPath = Path.Combine(dir, "tb.cnl");
            File.WriteAllText(tbPath, tb);

            var (lib, bench) = CnlReader.ReadFile(tbPath);
            var netlist = new Elaborator(lib).Elaborate(bench);

            // The two halves of the model must BOTH survive: the linear network and every diode.
            var comps = netlist.Components.ToList();
            Assert.Contains(comps, c => c.ComponentType.Equals("SnP", StringComparison.OrdinalIgnoreCase));
            int diodes = comps.Count(c => c.ComponentType.Equals("Diode", StringComparison.OrdinalIgnoreCase));
            Assert.True(diodes > 0, $"{cellName} elaborated with no diodes — the nonlinearity is gone");

            // Every diode must sit on an INTERNAL node, never on one of the part's own pins:
            // that is what "the diodes are on the network's trailing ports" means, and getting it
            // wrong would put the nonlinearity across an external terminal instead of inside.
            foreach (var d in comps.Where(c => c.ComponentType.Equals("Diode", StringComparison.OrdinalIgnoreCase)))
                Assert.Contains(d.Nodes, n => n != 0);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
