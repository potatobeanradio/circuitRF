// ================================================================
//  PcbViaSpanImportTests.cs — brief-via-span-import.md's own gates.
//
//  The export half of "a via's span is the technology's to state" landed first (ViaSpanTests); this is
//  the read half. Two defects, and only the first was about blind vias:
//
//    (a) PcbReader.ReadVia IDENTIFIED a blind/buried via and then discarded the layer pair it had just
//        read, placing the via on its top span layer with a Degraded count.
//    (b) PcbStackupMapping.Build emitted no StackupKind.Via entry at ALL, so an imported board's
//        technology had zero via entries and EVERY imported via — through vias included — resolved no
//        span. Re-exporting one wrote it as an unspanned through via and put its pad on no copper.
//
//  (b) is the common case and the bigger defect, so most of what is asserted here is about ordinary
//  through vias rather than about blind ones.
//
//  These tests assert through ViaSpanResolver, never through a layer NAME. A test that looked up
//  "Drill F.Cu-In1.Cu" would pass on a coincidence of naming while the resolver still answered null,
//  which is precisely the state the whole change exists to leave behind.
// ================================================================

using System.Diagnostics;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Tests;

public sealed class PcbViaSpanImportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("via-span-import-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ } }

    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    private static string FixturePath(string name)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "testdata", "pcb-samples", name);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException($"Fixture not found: {name}");
    }

    private PcbImport.ImportResult Import(string fixture, Technology? destTech = null)
    {
        using var stream = File.OpenRead(FixturePath(fixture));
        return PcbImport.Import(stream, _dir, Path.GetFileNameWithoutExtension(fixture), destTech, Dbu);
    }

    /// <summary>
    /// The technology the import actually lands in, assembled exactly as the two shipping appliers
    /// assemble it — <c>WorkspaceViewModel.ApplyImportToTechnology</c> and
    /// <c>LayoutConvert.MintTechnology</c>. <b>The via entries apply even when the whole stackup does
    /// not</b>, which is §3's one real obstacle and the reason they travel in a field of their own.
    /// </summary>
    private static Technology Applied(PcbImport.ImportResult r, Technology? destTech = null)
    {
        var tech = destTech is null
            ? new Technology { Name = "imported" }
            : TechPersistence.Deserialize(TechPersistence.Serialize(destTech));

        foreach (var def in r.LayersToAdd)
            if (!tech.Layers.Any(l => l.Key == def.Key)) tech.Layers.Add(def);
        if (r.Stackup is not null && tech.Stackup.Layers.Count == 0) tech.Stackup = r.Stackup;
        foreach (var entry in r.ViaEntries)
            if (!tech.Stackup.Layers.Any(l => l.Kind == StackupKind.Via
                                              && l.DrawingLayers.Any(k => entry.DrawingLayers.Contains(k))))
                tech.Stackup.Layers.Add(entry);
        return tech;
    }

    private static LayoutView BoardView(PcbImport.ImportResult r)
    {
        string cellDir = r.BoardCellDir!;
        var layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(cellDir)) + ".clay";
        return LayoutPersistence.LoadFromFile(Path.Combine(layoutDir, name));
    }

    private static IReadOnlyList<ViaShape> Vias(LayoutView view) => [.. view.Shapes.OfType<ViaShape>()];

    // ── Gate 1: a blind via imports as a blind via ───────────────────────────────────────────────

    /// <summary>
    /// §5 gate 1, and the whole point of the brief: Top → In1 in the file becomes Top → In1 in the
    /// technology. <b>Asserted through the resolver</b> — the same call <c>DrcConnectivity</c>,
    /// <c>PlanarExtractor.BuildVias</c> and every writer make — because a via drawn on a layer whose
    /// name reads correctly and which no via entry claims resolves to nothing at all.
    /// </summary>
    [Fact]
    public void Gate1_ABlindVia_ImportsToAViaWhoseLayerResolvesToTheSameSpan()
    {
        var r = Import("via-blind.kicad_pcb");
        var tech = Applied(r);
        var view = BoardView(r);

        var blind = Assert.Single(Vias(view), v => v.DrillSize == 300_000 && v.X == 3_000_000);
        var span = ViaSpanResolver.Resolve(blind.Layer, tech);

        Assert.NotNull(span);
        Assert.Equal("F.Cu", span!.Top.Name);
        Assert.Equal("In1.Cu", span.Bottom.Name);
        Assert.False(ViaSpanResolver.IsThrough(span, tech));
    }

    /// <summary>The through via on the SAME board resolves too, and to the outer pair — §2(b)'s half
    /// of the defect, which had nothing to do with blind vias and hit every import ever made.</summary>
    [Fact]
    public void Gate1b_AThroughViaOnTheSameBoard_ResolvesToTheOuterPair()
    {
        var r = Import("via-blind.kicad_pcb");
        var tech = Applied(r);

        var through = Assert.Single(Vias(BoardView(r)), v => v.DrillSize == 400_000);
        var span = ViaSpanResolver.Resolve(through.Layer, tech);

        Assert.NotNull(span);
        Assert.True(ViaSpanResolver.IsThrough(span!, tech));
        Assert.Equal("F.Cu", span!.Top.Name);
        Assert.Equal("B.Cu", span.Bottom.Name);
    }

    // ── Gate 2: the round trip ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// §5 gate 2. The import, re-exported through the real <c>circuitrf convert</c>, writes the same
    /// <c>(via …)</c> lines the source file carries — <b>byte for byte</b>, kind word and layer pair
    /// alike. <c>ViaSpanTests.AWrittenBlindVia_IsReadBackWithItsSpan…</c> is the other half; this
    /// closes the cycle, and it closes it through the SHIPPING path rather than a test-local
    /// reassembly of it, so a graft that works only in <c>WorkspaceViewModel</c> cannot pass.
    /// </summary>
    [Fact]
    public void Gate2_AnImportedBoardReExported_WritesTheSameViaLines()
    {
        string source = FixturePath("via-blind.kicad_pcb");
        string target = Path.Combine(_dir, "round-trip.kicad_pcb");

        var (code, _, stderr) = RunCli("convert", source, "-o", target);
        Assert.True(code == 0, stderr);

        var expected = ViaLines(File.ReadAllText(source));
        Assert.Equal(3, expected.Count);            // two blind and one through — never a vacuous pass
        Assert.Equal(expected, ViaLines(File.ReadAllText(target)));
    }

    /// <summary>Every <c>(via …)</c> line, trimmed — the format writes one per line at both ends, and
    /// nothing else on this board shares the token.</summary>
    private static List<string> ViaLines(string text) =>
        [.. text.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("(via ", StringComparison.Ordinal))];

    // ── Gate 3: the common case must not accumulate junk ─────────────────────────────────────────

    /// <summary>
    /// §5 gate 3. A board of ordinary through vias landing in a technology that already declares one
    /// outer-to-outer via entry adds NOTHING — no second entry, no second drill layer. Without this
    /// every import of every board grows the technology by one duplicate, which is the failure mode a
    /// "just synthesize an entry per span" implementation has.
    /// </summary>
    [Fact]
    public void Gate3_AllThroughVias_IntoATechnologyWithAPthEntry_AddNoViaEntry()
    {
        var dest = TwoLayerBoardTech();
        int before = dest.Stackup.Layers.Count(l => l.Kind == StackupKind.Via);

        var r = Import("via.kicad_pcb", dest);
        Assert.Empty(r.ViaEntries);

        var tech = Applied(r, dest);
        Assert.Equal(before, tech.Stackup.Layers.Count(l => l.Kind == StackupKind.Via));

        // …and the vias were still moved onto the existing entry's own drawing layer, which is what
        // makes "added nothing" different from "did nothing".
        foreach (var via in Vias(BoardView(r)))
            Assert.True(ViaSpanResolver.IsThrough(ViaSpanResolver.Resolve(via.Layer, tech)!, tech));
    }

    /// <summary>The same technology, a board whose blind span it does NOT declare: the through vias
    /// reuse the PTH entry and only the blind span mints one. A destination stackup is never replaced,
    /// so the new entry names the DESTINATION's conductors ("Top"/"Inner 1"), not the file's.</summary>
    [Fact]
    public void AnUndeclaredSpan_MintsOneEntry_NamingTheDestinationsOwnConductors()
    {
        var dest = FourLayerBoardTech();
        var r = Import("via-blind.kicad_pcb", dest);

        var entry = Assert.Single(r.ViaEntries);
        Assert.Equal("Top", entry.SpanFromLayer);
        Assert.Equal("Inner 1", entry.SpanToLayer);

        var tech = Applied(r, dest);
        var blind = Assert.Single(Vias(BoardView(r)), v => v.DrillSize == 300_000 && v.X == 3_000_000);
        var span = ViaSpanResolver.Resolve(blind.Layer, tech);
        Assert.Equal("Top", span!.Top.Name);
        Assert.Equal("Inner 1", span.Bottom.Name);

        // The board's own 7-layer stackup was refused (the destination already declares one) and the
        // via entry applied anyway — §3's one real obstacle, in one assertion.
        Assert.Equal(FourLayerBoardTech().Stackup.Layers.Count + 1, tech.Stackup.Layers.Count);
    }

    // ── Gate 4: one entry per SPAN, not per via ──────────────────────────────────────────────────

    /// <summary>§5 gate 4. The fixture has two vias sharing one blind span and one through via, so a
    /// per-via implementation produces three entries and this asserts two.</summary>
    [Fact]
    public void Gate4_TwoViasSharingASpan_ProduceOneEntryAndOneDrawingLayer()
    {
        var r = Import("via-blind.kicad_pcb");

        Assert.Equal(2, r.ViaEntries.Count);
        Assert.Equal(2, r.ViaEntries.SelectMany(e => e.DrawingLayers).Distinct().Count());

        var vias = Vias(BoardView(r));
        Assert.Equal(3, vias.Count);

        // The two blind vias landed on ONE layer; the through via on a different one.
        var blind = vias.Where(v => v.DrillSize == 300_000).Select(v => v.Layer).Distinct().ToList();
        Assert.Single(blind);
        Assert.DoesNotContain(vias.Single(v => v.DrillSize == 400_000).Layer, blind);
    }

    /// <summary>Every drawing layer a minted entry binds is also a layer the technology DECLARES. An
    /// entry pointing at an undeclared key resolves a span the renderer and the layer list know
    /// nothing about, and the layer would only ever be added by <c>ApplyReconciliation</c>, which adds
    /// a layer only when some shape was already on it — which no shape is, until the vias move.</summary>
    [Fact]
    public void EveryMintedDrillLayer_ReachesTheTechnology()
    {
        var r = Import("via-blind.kicad_pcb");
        var tech = Applied(r);

        var minted = r.ViaEntries.SelectMany(e => e.DrawingLayers).ToList();
        Assert.NotEmpty(minted);
        foreach (var key in minted)
            Assert.Contains(tech.Layers, l => l.Key.Equals(key));

        // …and so does the conductor a span names but no artwork sits on. In2.Cu is untouched by this
        // board's geometry; In1.Cu is named only by the blind via's own span.
        var spanned = tech.Stackup.Layers
            .Where(l => l.Kind == StackupKind.Conductor && l.DrawingLayers.Count > 0)
            .Select(l => l.Name);
        Assert.Contains("In1.Cu", spanned);
        foreach (var l in tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor && l.DrawingLayers.Count > 0))
            Assert.Contains(tech.Layers, d => d.Key.Equals(l.DrawingLayers[0]));
    }

    // ── Gate 5: the measurable form of §2(b) ─────────────────────────────────────────────────────

    /// <summary>
    /// §5 gate 5. Before this, an imported board re-exported to Gerber reported one
    /// <c>UnspannedViaPads</c> per via — the count that says a via's pad flash went into the DRILL
    /// layer's own file, which is copper etched where the hole belongs.
    /// </summary>
    [Fact]
    public void Gate5_AnImportedBoardExportedToGerber_ReportsNoUnspannedViaPads()
    {
        var r = Import("via-blind.kicad_pcb");
        var tech = Applied(r);
        var view = BoardView(r);

        var plan = GerberExport.Analyze(r.BoardCellDir!, tech, Dbu, view, null);
        Assert.Equal(0, plan.UnspannedViaPads);
    }

    /// <summary>The board-format writer's own counterpart: nothing was written as an unspanned through
    /// via, and the one span that is not outer-to-outer is written as blind.</summary>
    [Fact]
    public void AnImportedBoardExportedBack_WritesNoUnspannedVia()
    {
        var r = Import("via-blind.kicad_pcb");
        var plan = PcbExport.Analyze(r.BoardCellDir!, Applied(r), Dbu);

        Assert.Equal(0, plan.Summary.UnspannedVias);
        Assert.Equal(2, plan.Summary.BlindOrBuriedVias);
    }

    // ── Declining is still a legitimate outcome (§4.5) ───────────────────────────────────────────

    /// <summary>
    /// §4.5: today's behaviour must stay REACHABLE, not become unreachable. A board with no stackup
    /// section, imported into a technology with no stackup either, has nothing to name a span against —
    /// so the vias stay on the drill layer, resolve nothing, and the import SAYS so rather than
    /// inventing conductors to hang an entry off.
    /// </summary>
    [Fact]
    public void ABoardWithNoStackup_AddsNoViaEntry_AndSaysWhy()
    {
        var r = Import("via.kicad_pcb");

        Assert.Empty(r.ViaEntries);
        Assert.Contains(r.Messages, m => m.Contains("could not be expressed", StringComparison.Ordinal)
                                      && m.Contains("no conductor stackup layers", StringComparison.Ordinal));
        Assert.Null(ViaSpanResolver.Resolve(Vias(BoardView(r))[0].Layer, Applied(r)));
    }

    // ── Fixtures ────────────────────────────────────────────────────────────────────────────────

    private static readonly LayerKey TopCu = new(1, 0);
    private static readonly LayerKey In1Cu = new(2, 0);
    private static readonly LayerKey In2Cu = new(3, 0);
    private static readonly LayerKey BotCu = new(4, 0);
    private static readonly LayerKey PthDrill = new(9, 0);

    /// <summary>A destination technology whose copper layers carry the board-format aliases, so an
    /// import's F.Cu/B.Cu reconcile onto them rather than minting synthetic layers — the ordinary case
    /// of importing a board into a workspace already set up for that process.</summary>
    private static LayerDef Copper(LayerKey key, string name, string alias) => new()
    {
        Key = key,
        Name = name,
        Color = new Rgba(0xC8, 0x7A, 0x3E),
        Interchange = new InterchangeMapping(null, null, null, null, null, alias),
    };

    private static Technology TwoLayerBoardTech() => new()
    {
        Name = "2L",
        Layers =
        [
            Copper(TopCu, "Top", "F.Cu"),
            Copper(BotCu, "Bottom", "B.Cu"),
            new LayerDef { Key = PthDrill, Name = "PTH Drill", Color = new Rgba(0x20, 0x20, 0x20), Purpose = "drill" },
        ],
        Stackup = new Stackup
        {
            Layers =
            [
                new StackupLayer { Kind = StackupKind.Conductor, Name = "Top", ThicknessDbu = 35_000, SigmaSm = 5.8e7, DrawingLayers = [TopCu] },
                new StackupLayer { Kind = StackupKind.Dielectric, Name = "Core", ThicknessDbu = 1_500_000, Epsr = 4.5 },
                new StackupLayer { Kind = StackupKind.Conductor, Name = "Bottom", ThicknessDbu = 35_000, SigmaSm = 5.8e7, DrawingLayers = [BotCu] },
                new StackupLayer { Kind = StackupKind.Via, Name = "PTH", DrawingLayers = [PthDrill],
                                   SpanFromLayer = "Top", SpanToLayer = "Bottom" },
            ],
        },
    };

    private static Technology FourLayerBoardTech()
    {
        var tech = new Technology
        {
            Name = "4L",
            Layers =
            [
                Copper(TopCu, "Top", "F.Cu"),
                Copper(In1Cu, "Inner 1", "In1.Cu"),
                Copper(In2Cu, "Inner 2", "In2.Cu"),
                Copper(BotCu, "Bottom", "B.Cu"),
                new LayerDef { Key = PthDrill, Name = "PTH Drill", Color = new Rgba(0x20, 0x20, 0x20), Purpose = "drill" },
            ],
        };
        tech.Stackup.Layers.AddRange(
        [
            new StackupLayer { Kind = StackupKind.Conductor, Name = "Top", ThicknessDbu = 35_000, SigmaSm = 5.8e7, DrawingLayers = [TopCu] },
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "Prepreg 1", ThicknessDbu = 200_000, Epsr = 4.5 },
            new StackupLayer { Kind = StackupKind.Conductor, Name = "Inner 1", ThicknessDbu = 18_000, SigmaSm = 5.8e7, DrawingLayers = [In1Cu] },
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "Core", ThicknessDbu = 1_130_000, Epsr = 4.5 },
            new StackupLayer { Kind = StackupKind.Conductor, Name = "Inner 2", ThicknessDbu = 18_000, SigmaSm = 5.8e7, DrawingLayers = [In2Cu] },
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "Prepreg 2", ThicknessDbu = 200_000, Epsr = 4.5 },
            new StackupLayer { Kind = StackupKind.Conductor, Name = "Bottom", ThicknessDbu = 35_000, SigmaSm = 5.8e7, DrawingLayers = [BotCu] },
            new StackupLayer { Kind = StackupKind.Via, Name = "PTH", DrawingLayers = [PthDrill],
                               SpanFromLayer = "Top", SpanToLayer = "Bottom" },
        ]);
        return tech;
    }

    // ── The real CLI, as ConvertCliVerbTests runs it ─────────────────────────────────────────────

    private (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory       = _dir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(PcbViaSpanImportTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }
}
