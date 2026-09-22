// ================================================================
//  RecognitionTests.cs — the gate for brief-lvs-14-recognition.md §6.
//
//  ── WHAT IS BEING PINNED ──────────────────────────────────────────────────────────────────────
//
//  Tier 3: devices read out of COPPER, for artwork that carries no instances. It ships OFF, it is
//  subordinate to the instance reading wherever the two could both speak, and everything it
//  declined to do is said out loud.
//
//  Every value assertion is against HAND ARITHMETIC laid out in the fixture — never against
//  another circuitRF path, which would only prove the two agree. The NiCr body is 100 µm by
//  20 µm on a 50 Ω/□ sheet, so R = 50 × 100/20 = 250 Ω exactly, and the MIM plate is 60 µm square
//  on 400 µF/m², so C = 400e-6 × (60e-6)² = 1.44 pF exactly.
//
//  ── TWO READINGS OF THE BRIEF, BOTH RECORDED WHERE THEY BITE ──────────────────────────────────
//
//  1. §6 gate 8 asks for "a `check` error, by id". The deck's findings are `TechValidation`
//     problems, which is what `check` reports and all it reports — one id, `check.tech.problem`,
//     already gated end to end by CheckAndExplainCliVerbTests. Asserting them here against the
//     validator is the same fact one layer down and does not launch a process to learn it.
//  2. §3 R-lvs14-3c's ambiguous axis is judged PER FORMULA, not per candidate. A MIM capacitor is
//     square by construction and its C = CapDensity·Area does not care which way is along; only a
//     formula that actually reads Length or Width is affected. Withholding on every square body
//     would report a fault on the deck's own second example.
//
//  Fixture paths are anonymized to the SHAPE of a path — a temp folder and invented cell names.
// ================================================================

using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Drc;
using CircuitRF.Design.Layout.Lvs;
using CircuitRF.Design.Symbol;
using CircuitRF.Design.Theming;
using Symbol = CircuitRF.Design.Symbol.Symbol;

namespace CircuitRF.Ui.Tests.Lvs;

public sealed class RecognitionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-lvs14-" + Guid.NewGuid().ToString("N")[..12]);

    public RecognitionTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static long Um(double v) => (long)Math.Round(v * Dbu);

    private static readonly LayerKey M1  = new(1, 0);   // Metal1  — a conductor
    private static readonly LayerKey Res = new(7, 0);   // NiCr    — drawn, and NOT in the stackup
    private static readonly LayerKey Mim = new(8, 0);   // MIM top plate — a conductor
    private static readonly LayerKey Nit = new(9, 0);   // Nitride — drawn, and NOT in the stackup

    /// <summary>The hand arithmetic, once. 50 Ω per square, 400 µF/m².</summary>
    private const double SheetRho = 50.0;
    private const double CapDensity = 400e-6;

    // ══ 1 — it ships OFF ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 1.</b> The identical board reads identically with the deck present and absent, as
    /// long as nothing asked for recognition (R-lvs14-1d).
    /// </summary>
    /// <remarks>
    /// <b>The second half is the oracle.</b> "The deck changed nothing" is also what a deck that
    /// matched nothing would say, so the same board is read a third time WITH <c>--recognize</c> and
    /// must differ — otherwise this test would pass over a recognition pass that never ran.
    /// </remarks>
    [Fact]
    public void RecognitionIsOffUnlessTheRunAsksForIt()
    {
        var withDeck = Board(Deck(), b => HandDrawnResistor(b, Um(0), Um(0)));
        var without  = Board(NoDeck(), b => HandDrawnResistor(b, Um(0), Um(0)));

        Assert.Equal(Signature(Read(without, recognize: false)),
                     Signature(Read(withDeck, recognize: false)));

        Assert.NotEqual(Signature(Read(withDeck, recognize: false)),
                        Signature(Read(withDeck, recognize: true)));
    }

    // ══ 2, 3 — what a rule recognises ═══════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 2.</b> A hand-drawn NiCr resistor: a body, two terminals on two nets, and
    /// <c>R = 250 Ω</c> from the formula against hand arithmetic.
    /// </summary>
    /// <remarks>
    /// <b>The two terminals must be on DIFFERENT nets</b>, and that is not incidental: the NiCr the
    /// body is drawn on is not a stackup conductor, so nothing joins the two pads at DC. A
    /// technology that declared its resistive layer a plain conductor would short every resistor it
    /// recognised into one net — worth knowing, and recorded in <c>src/Design/Layout/Lvs/RESOLVED.md</c>.
    /// </remarks>
    [Fact]
    public void AHandDrawnNiCrResistorIsRecognisedWithItsValue()
    {
        var netlist = Read(Board(Deck(), b => HandDrawnResistor(b, Um(0), Um(0))), recognize: true);

        var device = Assert.Single(netlist.Devices);
        Assert.Equal(DeviceKind.Resistor, device.Type.Kind);
        Assert.StartsWith("R@", device.Path, StringComparison.Ordinal);

        // R-lvs14-3e: nothing named it, so it can only ever be matched structurally.
        Assert.Equal("", device.Designator);
        Assert.Equal("", device.AnchorId);

        Assert.Equal(2, device.Terminals.Count);
        Assert.NotEqual(device.Terminals[0].NetIndex, device.Terminals[1].NetIndex);

        Assert.Equal(SheetRho * 100.0 / 20.0, Assert.IsType<double>(device.Parameters["R"]), 6);

        // R-lvs10-4a's own meaning, and it is literally true here.
        Assert.True(device.ParameterFacts["R"].Computed);
    }

    /// <summary>
    /// <b>Gate 3.</b> A MIM capacitor, whose two terminals are on two DIFFERENT drawing layers
    /// (R-lvs14-3b) — and whose body is square, which its area formula does not care about.
    /// </summary>
    [Fact]
    public void AMimCapacitorIsRecognisedWithTerminalsOnTwoLayers()
    {
        var netlist = Read(Board(Deck(), b => HandDrawnMimCap(b, Um(0), Um(0))), recognize: true);

        var device = Assert.Single(netlist.Devices);
        Assert.Equal(DeviceKind.Capacitor, device.Type.Kind);
        Assert.Equal(2, device.Terminals.Count);
        Assert.NotEqual(device.Terminals[0].NetIndex, device.Terminals[1].NetIndex);

        // Scaled to picofarads before comparing: the absolute value is 1.44e-12 and xUnit's
        // decimal-places overload stops at 15, which would hold it to nothing at all.
        Assert.Equal(CapDensity * 60e-6 * 60e-6 * 1e12,
                     Assert.IsType<double>(device.Parameters["C"]) * 1e12, 9);

        // A square body, and NOT a withheld parameter: the formula never reads Length or Width.
        Assert.DoesNotContain(netlist.Notes, n => n.Id == "lvs.recognize.ambiguous-axis");
    }

    // ══ 4 — the instance always wins ════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 4.</b> A board carrying a PLACED resistor and a hand-drawn one yields exactly two
    /// devices, not three (R-lvs14-3d, R-lvs14-4a).
    /// </summary>
    /// <remarks>
    /// <b>The placed cell draws the identical artwork</b>, on the identical layers, so a
    /// recognition pass that wandered into instance copper would find it and the count would be
    /// three. That is the whole test: the instance is the better evidence, because the file SAYS
    /// what that part is.
    /// </remarks>
    [Fact]
    public void RecognitionDoesNotRunInsideAnInstance()
    {
        string cell = ResistorCell();
        var board = Board(Deck(), b =>
        {
            b.View.Instances.Add(new LayoutInstance
            {
                CellRef = Path.GetRelativePath(b.LayoutDir, cell),
                X = Um(500), Y = 0, Mag = 1.0, RefDes = "R1",
            });
            HandDrawnResistor(b, Um(0), Um(0));
        });

        var netlist = Read(board, recognize: true);

        Assert.Equal(2, netlist.Devices.Count);
        Assert.Single(netlist.Devices, d => d.Path == "R1");
        Assert.Single(netlist.Devices, d => d.Path.StartsWith("R@", StringComparison.Ordinal));
    }

    // ══ 5, 6, 7 — nothing is dropped in silence ═════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 5.</b> A body within a few percent of square is reported and NOT guessed at
    /// (R-lvs14-3c) — the device still exists, and it claims no <c>R</c>.
    /// </summary>
    /// <remarks>
    /// <b>The device is emitted rather than dropped</b>, which is R-lvs3-5a's rule applied here: a
    /// device the comparison cannot fully handle must still appear in the count, or the two sides
    /// disagree about how many parts there are for a reason the report never gave. What is withheld
    /// is the one thing the shape cannot say.
    /// </remarks>
    [Fact]
    public void AnAmbiguousAxisIsReportedAndNotGuessed()
    {
        var netlist = Read(
            Board(Deck(), b => HandDrawnResistor(b, Um(0), Um(0), bodyLength: 40, bodyWidth: 40)),
            recognize: true);

        var device = Assert.Single(netlist.Devices);
        Assert.Equal(2, device.Terminals.Count);
        Assert.Empty(device.Parameters);
        Assert.Contains(netlist.Notes, n => n.Id == "lvs.recognize.ambiguous-axis");
    }

    /// <summary>
    /// <b>Gates 6 and 7.</b> One good body, one with a single terminal and one with three: one
    /// device, and a reported reason for each of the two that were rejected (R-lvs14-3b,
    /// R-lvs14-4c).
    /// </summary>
    /// <remarks>
    /// <b>Two rejects and two lines, counted rather than merely "at least one".</b> A recognition
    /// pass that quietly dropped half the devices would make a design read as clean, which is the
    /// failure this rule exists to prevent — and a test asserting only that SOMETHING was said
    /// would pass over it.
    /// </remarks>
    [Fact]
    public void EveryRejectedCandidateIsReportedWithAReason()
    {
        var netlist = Read(Board(Deck(), b =>
        {
            HandDrawnResistor(b, Um(0), Um(0));                       // two terminals — a device
            HandDrawnResistor(b, Um(0), Um(400), terminals: 1);       // one   — rejected
            HandDrawnResistor(b, Um(0), Um(800), terminals: 3);       // three — rejected
        }), recognize: true);

        Assert.Single(netlist.Devices);
        Assert.Equal(2, netlist.Notes.Count(n => n.Id == "lvs.recognize.terminal-count"));
    }

    /// <summary>
    /// A rule naming a layer the TECHNOLOGY does not define is reported as an unusable rule, not as
    /// a body with no terminals (R-lvs14-4c).
    /// </summary>
    /// <remarks>
    /// <b>The two look identical from the outside and have entirely different fixes.</b> Left as a
    /// terminal count, a misspelled terminal layer produces one warning per body and never names
    /// the cause — which is the shape of every "reported, but not usefully" failure this series is
    /// built to avoid.
    /// </remarks>
    [Fact]
    public void ARuleNamingAnUndefinedLayerSaysSo()
    {
        var tech = Deck();
        tech.DeviceRules[0].Terminals = ["3/0"];   // nothing in this technology is layer 3

        var netlist = Read(Board(tech, b => HandDrawnResistor(b, Um(0), Um(0))), recognize: true);

        Assert.Contains(netlist.Notes, n => n.Id == "lvs.recognize.rule-invalid");
        Assert.DoesNotContain(netlist.Notes, n => n.Id == "lvs.recognize.terminal-count");
        Assert.Empty(netlist.Devices);
    }

    // ══ 8, 10 — the deck is checked before it is run ════════════════════════════════════════════

    /// <summary>
    /// <b>Gates 8 and 10.</b> A malformed deck is a validation problem before any run: an unknown
    /// <c>Kind</c> LISTS the real ones, an unreadable region names the rule, a formula naming
    /// nothing says so, and a cyclic constant is caught by the expression engine's own cycle
    /// detection (R-lvs14-2c, R-lvs14-2d, R-lvs14-2b).
    /// </summary>
    /// <remarks>
    /// <b>Every one of these is what <c>check</c> prints</b> — <c>TechValidation.Analyze</c> is its
    /// whole answer for a `.ctech`, and the mapping to <c>check.tech.problem</c> is gated end to end
    /// by <c>CheckAndExplainCliVerbTests</c>. A rule living only in the verb would be a rule the
    /// application does not enforce, which is what puts the deck in this table rather than in the
    /// run.
    /// </remarks>
    [Fact]
    public void AMalformedDeckIsAValidationErrorBeforeAnyRun()
    {
        var tech = NoDeck();
        tech.Constants.Add(new TechConstant { Name = "A", Expression = "B" });
        tech.Constants.Add(new TechConstant { Name = "B", Expression = "A" });
        tech.DeviceRules.Add(new DeviceRule
        {
            Name = "Bad", Kind = "Thermistor", Body = "not(a valid expression",
            Terminals = ["1/0"], Parameters = { ["R"] = "Lenght * 2" },
        });

        string all = string.Join("\n", TechValidation.Analyze(tech).Select(p => p.Message));

        Assert.Contains("Thermistor", all, StringComparison.Ordinal);
        Assert.Contains("Resistor", all, StringComparison.Ordinal);      // the real ones, listed
        Assert.Contains("body region", all, StringComparison.Ordinal);
        Assert.Contains("Lenght", all, StringComparison.Ordinal);        // the typo, named
        Assert.Contains("A → B → A", all, StringComparison.Ordinal);     // the engine's own sentence
    }

    // ══ 9 — one layer grammar, and it is the DRC's ══════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 9, first half.</b> Nothing under <c>src/Design</c> outside the DRC parses a layer
    /// expression a second way (R-lvs14-2a).
    /// </summary>
    /// <remarks>
    /// Comment-stripped, for <c>LayoutNetlistTests</c>' gate-15 reason: the files that reuse the one
    /// parser say so in prose, and a scan a comment could defeat would have to be silenced the
    /// first time somebody documented the rule.
    /// </remarks>
    [Fact]
    public void TheDeckUsesTheOneLayerExpressionParser()
    {
        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(
                     Path.Combine(RepoRoot(), "src", "Design"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.Combine("Layout", "Drc"), StringComparison.Ordinal)) continue;

            string source = StripComments(File.ReadAllText(file));
            if (Regex.IsMatch(source, @"\bclass\s+\w*LayerExpr\w*Parser\b")) offenders.Add(file);
        }

        Assert.Empty(offenders);
    }

    /// <summary>
    /// <b>Gate 9, second half.</b> A DRC rule's region and a <c>Body</c> with the same text produce
    /// the same region — measured, not asserted structurally.
    /// </summary>
    /// <remarks>
    /// <b>The oracle is a MinArea rule set just above the body's own area.</b> It fires on exactly
    /// the shapes the recognition called bodies, and its marker is their outline — so if the two
    /// readings of <c>7/0</c> differed in any way that matters, the count or the box would differ
    /// with them. Two hand-drawn resistors, so a rule that happened to fire once by coincidence
    /// does not pass.
    /// </remarks>
    [Fact]
    public void ADrcRegionAndABodyWithTheSameTextAreTheSameRegion()
    {
        var tech = Deck();
        tech.DrcRules.Add(new DrcRule
        {
            Name = "body area", Kind = DrcRuleKind.MinArea, Layer = Res, RegionA = "7/0",
            ValueDbu = Um(100) * Um(20) + 1,        // just above the body, so every body fails it
        });

        var board = Board(tech, b =>
        {
            HandDrawnResistor(b, Um(0), Um(0));
            HandDrawnResistor(b, Um(0), Um(400));
        });

        var recognised = Read(board, recognize: true).Devices;
        var violations = DrcEngine.Run(board.View.Shapes, tech).Violations;

        Assert.Equal(2, recognised.Count);
        Assert.Equal(recognised.Count, violations.Count);

        // Each recognised body sits at the centre of one violation's marker, and nowhere else.
        Assert.All(recognised, d => Assert.Single(
            violations, v => v.Marker.Contains(d.Provenance.X, d.Provenance.Y)));
    }

    // ══ 11 — it never claims more than it is ════════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 11.</b> <c>lvs.recognize.in-use</c> is reported whenever recognition contributed, and
    /// only then (R-lvs14-5a).
    /// </summary>
    /// <remarks>
    /// <b>Unconditional, and for <c>lvs.ground.reference-undrawn</c>'s reason.</b> There is no
    /// instance to select and nothing in the drawing that says "this rectangle was read as a
    /// resistor", so being told is the only way anyone knows the comparison rested on a recognition
    /// at all — and a clean report must not be mistaken for the stronger claim that the process
    /// actually makes this device.
    /// </remarks>
    [Fact]
    public void RecognitionInUseIsReportedWheneverItContributed()
    {
        var board = Board(Deck(), b => HandDrawnResistor(b, Um(0), Um(0)));

        Assert.Contains(Read(board, recognize: true).Notes, n => n.Id == "lvs.recognize.in-use");
        Assert.DoesNotContain(Read(board, recognize: false).Notes, n => n.Id == "lvs.recognize.in-use");

        // And a technology with no deck recognises nothing however the run is set — R-lvs14-1d's
        // other half, which is not an error and says nothing.
        var bare = Board(NoDeck(), b => HandDrawnResistor(b, Um(0), Um(0)));
        Assert.Empty(Read(bare, recognize: true).Devices);
        Assert.DoesNotContain(Read(bare, recognize: true).Notes, n => n.Id == "lvs.recognize.in-use");
    }

    // ── Fixtures ────────────────────────────────────────────────────────────────────────────────

    private sealed record Fixture(LayoutView View, string Clay, string CellDir, string LayoutDir, Technology Tech);

    private static LvsNetlist Read(Fixture f, bool recognize) =>
        LayoutRead.Read(f.View, f.Clay, f.CellDir, f.Tech, null, null,
                        new LvsHierarchyContext { Recognize = recognize });

    /// <summary>Everything a comparison would read, as one string — see
    /// <c>LayoutNetlistTests.Signature</c>, whose shape this is.</summary>
    private static string Signature(LvsNetlist netlist)
    {
        var sb = new StringBuilder();
        foreach (var d in netlist.Devices)
        {
            sb.Append(d.Path).Append('|').Append(d.Type.Kind).Append(':');
            foreach (var t in d.Terminals) sb.Append(t.Port).Append('@').Append(t.NetIndex).Append(',');
            foreach (var (k, v) in d.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
                sb.Append(k).Append('=').Append(v).Append(';');
            sb.AppendLine();
        }
        foreach (var n in netlist.Nets) sb.Append(n.Index).Append('/').Append(n.Pins.Count).AppendLine();
        foreach (var note in netlist.Notes) sb.AppendLine(note.Id);
        return sb.ToString();
    }

    private int _boards;

    private Fixture Board(Technology tech, Action<Fixture> build)
    {
        string name = "Die" + _boards++;
        string cellDir = CellFolder.CreateCellFolder(_root, name);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        Directory.CreateDirectory(layoutDir);

        var view = new LayoutView { DbuPerMicron = Dbu };
        string clay = Path.Combine(layoutDir, name + ".clay");
        var fixture = new Fixture(view, clay, cellDir, layoutDir, tech);

        build(fixture);
        LayoutPersistence.SaveToFile(clay, view);
        return fixture;
    }

    /// <summary>
    /// A NiCr body with Metal1 pads at its ends — the artwork the deck's first rule describes.
    /// </summary>
    /// <param name="terminals">How many pads to draw. Two is a device; one and three are the
    /// rejections gate 6 is about.</param>
    private static void HandDrawnResistor(
        Fixture f, long x, long y, double bodyLength = 100, double bodyWidth = 20, int terminals = 2)
    {
        long l = Um(bodyLength), w = Um(bodyWidth), pad = Um(30);

        f.View.Shapes.Add(Rect(Res, x, y, x + l, y + w));

        // Each pad overlaps its end of the body by 5 µm, which is how the artwork is actually drawn
        // and is what makes the touch test meaningful rather than a test of the 1 DBU dilation.
        if (terminals >= 1) f.View.Shapes.Add(Rect(M1, x - pad, y - Um(5), x + Um(5), y + w + Um(5)));
        if (terminals >= 2) f.View.Shapes.Add(Rect(M1, x + l - Um(5), y - Um(5), x + l + pad, y + w + Um(5)));

        // A third pad on the body's own flank: a real candidate, a real reject, and nothing about
        // it is malformed — which is exactly the case that must not be absorbed in silence.
        if (terminals >= 3)
            f.View.Shapes.Add(Rect(M1, x + l / 2 - Um(5), y + w - Um(2), x + l / 2 + Um(5), y + w + pad));
    }

    /// <summary>A MIM plate under a nitride window, with a Metal1 bottom plate — the deck's second
    /// rule. The body is <c>and(8/0, 9/0)</c>, 60 µm square.</summary>
    private static void HandDrawnMimCap(Fixture f, long x, long y)
    {
        long s = Um(60), over = Um(10);

        f.View.Shapes.Add(Rect(M1,  x - over, y - over, x + s + over, y + s + over));
        f.View.Shapes.Add(Rect(Nit, x - over, y - over, x + s + over, y + s + over));
        f.View.Shapes.Add(Rect(Mim, x, y, x + s, y + s));
    }

    /// <summary>A resistor as a placed CELL, drawing the identical artwork — gate 4's whole
    /// point.</summary>
    private string ResistorCell()
    {
        string cellDir = CellFolder.CreateCellFolder(_root, "NiCrR");
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        if (File.Exists(Path.Combine(layoutDir, "NiCrR.clay"))) return cellDir;

        SymbolPersistence.SaveToFile(
            Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Symbol), "NiCrR.csym"),
            new Symbol([], [new SymbolPin(0, 0, 1, "A"), new SymbolPin(0, 100, 2, "B")], 2));

        var view = new LayoutView { DbuPerMicron = Dbu };
        var cell = new Fixture(view, "", cellDir, layoutDir, NoDeck());
        HandDrawnResistor(cell, 0, 0);

        foreach (var (pin, px) in new[] { ("A", -Um(15)), ("B", Um(115)) })
        {
            view.Pins.Add(new LayoutPin { Name = pin, X = px, Y = Um(10), WidthDbu = Um(20), Layer = M1 });
            foreach (var shape in view.Shapes)
                if (shape.Layer == M1 && LayoutGeometry.BboxOf(shape).Contains(px, Um(10)))
                    shape.Pin = pin;
        }

        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, "NiCrR.clay"), view);

        string ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.NumPorts = 2;
        CellPersistence.SaveToFile(ccellPath, ccell);

        return cellDir;
    }

    private static RectShape Rect(LayerKey layer, long x1, long y1, long x2, long y2) =>
        new() { Layer = layer, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };

    // ── The technology ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A two-metal MMIC process. <b>NiCr and Nitride are DRAWN and are not in the stackup</b>, which
    /// is what they are: a resistive film and a dielectric window, neither of which joins anything
    /// at DC. That is why a recognised resistor's two terminals are two nets.
    /// </summary>
    private static Technology NoDeck()
    {
        var tech = new Technology { Name = "MMIC 2LM" };
        tech.Layers =
        [
            new LayerDef { Key = M1,  Name = "Metal1",    ZOrder = 0, Color = new Rgba(200, 80, 40, 255) },
            new LayerDef { Key = Res, Name = "NiCr",      ZOrder = 1, Color = new Rgba(90, 160, 90, 255) },
            new LayerDef { Key = Mim, Name = "MIM Metal", ZOrder = 2, Color = new Rgba(160, 160, 60, 255) },
            new LayerDef { Key = Nit, Name = "Nitride",   ZOrder = 3, Color = new Rgba(120, 120, 220, 255) },
        ];
        tech.Stackup.Layers =
        [
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "MIM", ThicknessDbu = Um(1), SigmaSm = 4.1e7,
                DrawingLayers = [Mim],
            },
            new StackupLayer
            {
                Kind = StackupKind.Dielectric, Name = "SiN", ThicknessDbu = Um(1), Epsr = 7.0, TanD = 0.001,
            },
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "Metal1", ThicknessDbu = Um(2), SigmaSm = 4.1e7,
                DrawingLayers = [M1], IsGroundReference = true,
            },
        ];
        return tech;
    }

    /// <summary>The same process, with the deck of §2 — the brief's own two rules, spelled in the
    /// one layer grammar.</summary>
    private static Technology Deck()
    {
        var tech = NoDeck();
        tech.Constants =
        [
            new TechConstant { Name = "SheetRho",   Expression = "50",     Unit = "Ohm" },
            new TechConstant { Name = "CapDensity", Expression = "400e-6" },
        ];
        tech.DeviceRules =
        [
            new DeviceRule
            {
                Name = "NiCr resistor", Kind = "Resistor", Body = "7/0", Terminals = ["1/0"],
                Parameters = { ["R"] = "SheetRho * Length / Width" },
            },
            new DeviceRule
            {
                Name = "MIM capacitor", Kind = "Capacitor", Body = "and(8/0, 9/0)",
                Terminals = ["1/0", "8/0"],
                Parameters = { ["C"] = "CapDensity * Area" },
            },
        ];
        return tech;
    }

    private static string StripComments(string src)
    {
        src = Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(src, @"//[^\n]*", "");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
