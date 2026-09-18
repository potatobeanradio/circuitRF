// Gate for the four defects a designer hit importing one two-layer board (owner report, 2026-09-17).
// Detail and the reasoning in src/Design/RESOLVED.md and src/Ui/RESOLVED.md; the short form:
//
//   1. A run-together file name identified its BOTTOM-side layers and dropped its TOP-side ones, so
//      the board arrived with one conductor instead of two.
//   2. One conductor means no dielectric, because a skeleton stackup puts one BETWEEN two conductors.
//      The reported symptom was "no dielectric"; the cause was a name that did not match.
//   3. A thickness typed in one display unit came back as a different number after a round trip
//      through another, because the box was formatted at a coarser precision than it was parsed at.
//   4. The whole import refused at the first drill file, whose format was stated in a comment nobody
//      read.
//
// THE FIXTURES NAME NO TOOL, VENDOR OR PRODUCT — only the SHAPE of the names the board used, which is
// the whole content of defect 1. Root CLAUDE.md §"Commercial Vendor References".
//
// COUNTERS AND CONTENT ONLY. No wall-clock assertion anywhere in this file.

using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

public class GerberRunTogetherNamesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("runtogether-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Artwork with NO <c>%TF.FileFunction</c>, deliberately: the name is the only evidence,
    /// which is the rung under test.</summary>
    private static string Artwork(double xMm) =>
        "%FSLAX46Y46*%\n%MOMM*%\n%ADD10C,0.400*%\nD10*\n" +
        $"X{(long)Math.Round(xMm * 1_000_000)}Y1000000D03*\n" + "M02*\n";

    private string Folder(string name)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private GerberImport.ImportResult Import(string dir, string name) =>
        GerberImport.Import(
            [.. Directory.EnumerateFiles(dir).OrderBy(p => p, StringComparer.Ordinal)],
            _root, name, null, 1000, null, null);

    private static string NameOf(GerberImport.ImportResult r, string file) =>
        r.Layers.Single(l => l.FileName == file).LayerName;

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // 1 — the asymmetry itself
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The defect, stated as the thing that made it invisible: the bottom-side files were always
    /// identified, so the set did not look broken — it looked half-annotated.
    ///
    /// <para>"bottom", "mask", "silk", "paste" and "layer" are four or more characters and matched
    /// INSIDE a run-together word; "top" and "bot" are three and could only match as whole words. So
    /// every row naming the top failed and every row naming the bottom passed.</para>
    /// </summary>
    [Fact]
    public void RunTogetherNames_IdentifyTheTopSideLayersToo_NotOnlyTheBottomSideOnes()
    {
        var dir = Folder("asymmetry");
        File.WriteAllText(Path.Combine(dir, "Board-EtchLayer1Top.gdo"),    Artwork(1.0));
        File.WriteAllText(Path.Combine(dir, "Board-SoldermaskTop.gdo"),    Artwork(2.0));
        File.WriteAllText(Path.Combine(dir, "Board-SoldermaskBottom.gdo"), Artwork(3.0));
        File.WriteAllText(Path.Combine(dir, "Board-SilkscreenTop.gdo"),    Artwork(4.0));
        File.WriteAllText(Path.Combine(dir, "Board-SolderPasteTop.gdo"),   Artwork(5.0));

        var result = Import(dir, "asymmetry");

        Assert.Equal("Top Copper",        NameOf(result, "Board-EtchLayer1Top.gdo"));
        Assert.Equal("Soldermask Top",    NameOf(result, "Board-SoldermaskTop.gdo"));
        Assert.Equal("Soldermask Bottom", NameOf(result, "Board-SoldermaskBottom.gdo"));
        Assert.Equal("Silk Top",          NameOf(result, "Board-SilkscreenTop.gdo"));
        Assert.Equal("Paste Top",         NameOf(result, "Board-SolderPasteTop.gdo"));
    }

    /// <summary>
    /// Why the fix is to SPLIT the name rather than to let short words match inside a word.
    ///
    /// <para>"solderstopmask" is a real spelling and it ends the word "stop" with the letters "top".
    /// Any rule that matched a three-letter side word inside a word would read a side off this file,
    /// which states none — so it must come out side-neutral. This is the assertion that stops someone
    /// "simplifying" the fix into the bug it replaced.</para>
    /// </summary>
    [Fact]
    public void ASolderStopMask_IsGivenNoSide_BecauseStopEndsInTop()
    {
        var dir = Folder("stopmask");
        File.WriteAllText(Path.Combine(dir, "board-solderstopmask.gdo"), Artwork(1.0));

        var layer = Import(dir, "stopmask").Layers.Single(l => l.FileName == "board-solderstopmask.gdo");

        // Unidentified, and therefore asked about — NOT quietly given a side. Asserted on the RUNG
        // rather than on the name, because the name a rung-4 file gets is its own stem, which contains
        // the letters this test is about.
        Assert.Equal(GerberLayerRung.Unidentified, layer.Rung);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // 2 — the reported symptom: no dielectric
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The two halves of the reported symptom in one assertion, because they are one fact: the second
    /// copper file has to be recognised AS the bottom before the board has two conductors, and a
    /// skeleton stackup emits a dielectric only BETWEEN two conductors.
    ///
    /// <para>"Layer 2" is the first INNER layer on a board with more than two, and the BOTTOM on a
    /// board with exactly two — which the file name cannot distinguish and the resolved stack can. The
    /// rename is reported, because it is still a guess.</para>
    /// </summary>
    [Fact]
    public void ATwoLayerSetNumberedFromTheTop_HasABottomCopperAndOneDielectric()
    {
        var dir = Folder("twolayer");
        File.WriteAllText(Path.Combine(dir, "Board-EtchLayer1Top.gdo"), Artwork(1.0));
        File.WriteAllText(Path.Combine(dir, "Board-EtchLayer2.gdo"),    Artwork(2.0));

        var result = Import(dir, "twolayer");

        Assert.Equal("Top Copper",    NameOf(result, "Board-EtchLayer1Top.gdo"));
        Assert.Equal("Bottom Copper", NameOf(result, "Board-EtchLayer2.gdo"));

        var tech = TechPersistence.LoadFromFile(result.TechPath!);
        Assert.Equal(2, tech.Stackup.Layers.Count(l => l.Kind == StackupKind.Conductor));
        Assert.Equal(1, tech.Stackup.Layers.Count(l => l.Kind == StackupKind.Dielectric));

        Assert.Contains(result.Messages, m =>
            m.Contains("renamed \"Bottom Copper\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// The bound on that rename. A set that DECLARES its stack positions is never second-guessed —
    /// <c>Copper,L2,Inr</c> on a four-layer board stays an inner layer even though it is not the
    /// bottom-most file in the set, because the rename fires only on a rung-3 guess with a known top
    /// and no declared bottom.
    /// </summary>
    [Fact]
    public void ADeclaredInnerLayer_IsNeverRenamedToBottomCopper()
    {
        const string Header = "%FSLAX46Y46*%\n%MOMM*%\n";
        static string Declared(string fn, double xMm) =>
            Header + $"%TF.FileFunction,{fn}*%\n%ADD10C,0.400*%\nD10*\n" +
            $"X{(long)Math.Round(xMm * 1_000_000)}Y1000000D03*\n" + "M02*\n";

        var dir = Folder("declared");
        File.WriteAllText(Path.Combine(dir, "board.gtl"), Declared("Copper,L1,Top,Signal", 1.0));
        File.WriteAllText(Path.Combine(dir, "board.g2"),  Declared("Copper,L2,Inr,Plane",  2.0));

        var result = Import(dir, "declared");

        Assert.Equal("Inner 1", NameOf(result, "board.g2"));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // 3 — a thickness that survives a change of display unit
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The editable box is formatted at the precision it is PARSED at, so a value neither typed nor
    /// touched cannot drift.
    ///
    /// <para>At <c>LayoutUnits.Format</c>'s four-place default a mil is quantised to 2.54 nm — coarser
    /// than the 1 nm the value is stored at — so 35 um displayed as "1.378" mil and a focus round trip
    /// with nothing typed committed 35.001 um. The reporter saw it "work the second time" because by
    /// then the stored value already WAS the rounded one.</para>
    /// </summary>
    [Fact]
    public void AThicknessSurvivesARoundTripThroughAnotherDisplayUnit()
    {
        var tech = new Technology { Name = "T", DefaultDisplayUnit = LayoutUnit.Um };
        tech.Layers.Add(new LayerDef { Key = new LayerKey(1, 0), Name = "Top", Color = new Rgba(1, 2, 3, 255) });
        tech.Stackup.Layers.Add(new StackupLayer
        {
            Kind = StackupKind.Conductor, Name = "Top Copper",
            ThicknessDbu = 0, SigmaSm = 5.8e7, DrawingLayers = [new LayerKey(1, 0)],
        });

        var vm = new TechEditorViewModel(Path.Combine(_root, "t.ctech"), tech);
        var row = vm.StackupLayers.Single();
        row.StagedThicknessText = "35";
        row.CommitThickness();
        Assert.Equal(35_000, row.Layer.ThicknessDbu);

        vm.DefaultDisplayUnit = LayoutUnit.Mil;
        // A focus round trip on the field, with nothing typed. This is what used to write the display
        // rounding into the design.
        vm.StackupLayers.Single().CommitThickness();

        vm.DefaultDisplayUnit = LayoutUnit.Um;
        var after = vm.StackupLayers.Single();
        Assert.Equal(35_000, after.Layer.ThicknessDbu);
        Assert.Equal("35", after.StagedThicknessText);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // 4 — a drill format stated in a comment
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The refusal is correct policy and the file was not silent — it stated its digit counts in a
    /// comment spelled with a colon and a dot rather than with the one <c>;FILE_FORMAT=2:4</c> spelling
    /// the reader knew. Asserted against the same file WITHOUT that line, so what is pinned is the
    /// comment doing the work and not the inference happening to agree.
    /// </summary>
    [Fact]
    public void ADrillFormatStatedInAComment_SettlesTheDigitsAndNeedsNoOverride()
    {
        const string Body = "M48\nMETRIC,TZ\nFMAT,2\nT01C1.5000\n%\nM71\nG90\nT01\nX272000Y12000\nM30\n";

        var stated = ExcellonReader.Read(
            "; Format  : 3.3 / Absolute / MM / Leading\n" + Body, LayoutUnits.DefaultDbuPerMicron, null);

        Assert.Equal(3, stated.Format.IntegerDigits);
        Assert.Equal(3, stated.Format.DecimalDigits);
        Assert.Equal(DrillFormatEvidence.FormatComment, stated.Format.DigitsEvidence);
        Assert.False(stated.Format.RequiredAGuess);

        // The control: the very same file with the comment removed still has to ASK.
        var silent = ExcellonReader.Read(Body, LayoutUnits.DefaultDbuPerMicron, null);
        Assert.Equal(DrillFormatEvidence.Defaulted, silent.Format.DigitsEvidence);
        Assert.True(silent.Format.RequiredAGuess);
    }
}
