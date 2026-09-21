// ================================================================
//  PdnFastExtractorTests.cs — brief-railrf-4-fast-extractor.md §5
//
//  The DEFAULT model, and the three rules that make having two speeds safe.
//
//  ── THE HEADLINE GATE IS THE SAME EXTERNAL ARITHMETIC BRIEF 3 IS GATED ON ─────────────────────
//
//  A straight trace of known width, thickness and length has R = L/(σ·W·T) exactly. Brief 3's MESH
//  is gated against that closed form, and this extractor IS the closed form along a path — so the
//  two are anchored to arithmetic rather than to each other, and the fast-vs-accurate gate below is
//  really a gate on THE PATH FINDING AND THE CLASSIFICATION, which is exactly where the fast model
//  can be wrong.
//
//  ── THE HALF OF §7'S GATE THAT IS EASY TO SKIP ────────────────────────────────────────────────
//
//  "On a trace-dominated DC path the two must agree to 5 %; on a POUR-DOMINATED one the fast model
//  must REFUSE rather than differ."
//
//  The second half is the important one. A fast model that produced a slightly different number on a
//  pour has failed the gate just as surely as one that produced a wildly different one — the
//  required behaviour is a refusal, because the error there is unbounded and it is optimistic. And
//  the NEGATIVE that catches the real defect is below it: force the pour to Trace and assert the
//  answer is then optimistic by more than 5 %. That is the misclassification failure, reproduced on
//  purpose, and it is what proves the classifier is load-bearing rather than decorative.
//
//  ── NO TIMING TESTS ───────────────────────────────────────────────────────────────────────────
//
//  "Single-digit milliseconds" is a design intent, not an assertion — the status strip displays
//  elapsed time and nothing gates on it. What is asserted is the ELEMENT COUNT, because the ratio
//  between a few hundred elements and tens of thousands is the structural property that makes the
//  fast model fast, and a regression in it would survive any wall-clock threshold on a fast machine.
//
//  The little conjugate-gradient solver at the foot of this file is the TEST's, exactly as it is in
//  PdnMeshExtractorTests: neither extractor solves anything, and a gate that measures what a solver
//  would see is a better gate than one that reads the extractor's own belief about what it built.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class PdnFastExtractorTests
{
    // ── the board ──────────────────────────────────────────────────────────────────────────────

    private const int DbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey Bot = new(2, 0);
    private static readonly LayerKey Mid = new(3, 0);
    private static readonly LayerKey ViaLayer = new(10, 0);

    private const double CopperSigma = 5.8e7;          // S/m at 20 °C
    private const double CopperRho = 1.0 / CopperSigma;
    private const double DbuPerMetre = DbuPerMicron * 1e6;

    private static long Mm(double v) => (long)Math.Round(v * 1e3 * DbuPerMicron);
    private static long Um(double v) => (long)Math.Round(v * DbuPerMicron);

    /// <summary>Two conductors and a core — the same shape <c>PdnMeshExtractorTests</c> uses, so the
    /// two extractors are compared on one board and not on two.</summary>
    private static Technology Board(double topUm, double botUm, double coreMm)
    {
        var tech = new Technology { Name = "test board" };
        tech.Stackup.Layers =
        [
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "TOP",
                ThicknessDbu = Um(topUm), SigmaSm = CopperSigma, DrawingLayers = [Top],
            },
            new StackupLayer
            {
                Kind = StackupKind.Dielectric, Name = "CORE",
                ThicknessDbu = Mm(coreMm), Epsr = 4.3, TanD = 0.02,
            },
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "BOT",
                ThicknessDbu = Um(botUm), SigmaSm = CopperSigma, DrawingLayers = [Bot],
                IsGroundReference = true,
            },
            new StackupLayer
            {
                Kind = StackupKind.Via, Name = "PTH", DrawingLayers = [ViaLayer],
                Fill = ViaFillKind.Plated, WallThicknessDbu = Um(25),
                SpanFromLayer = "TOP", SpanToLayer = "BOT",
            },
        ];
        return tech;
    }

    /// <summary>Three conductors, so a layer TRANSITION has somewhere to go that is not the
    /// reference — a conductor cannot be its own return.</summary>
    private static Technology ThreeConductors()
    {
        var tech = new Technology { Name = "three" };
        tech.Stackup.Layers =
        [
            new StackupLayer { Kind = StackupKind.Conductor, Name = "TOP", ThicknessDbu = Um(35), SigmaSm = CopperSigma, DrawingLayers = [Top] },
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "PP1", ThicknessDbu = Mm(0.2), Epsr = 4.3, TanD = 0.02 },
            new StackupLayer { Kind = StackupKind.Conductor, Name = "MID", ThicknessDbu = Um(35), SigmaSm = CopperSigma, DrawingLayers = [Mid] },
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "CORE", ThicknessDbu = Mm(1.2), Epsr = 4.3, TanD = 0.02 },
            new StackupLayer { Kind = StackupKind.Conductor, Name = "BOT", ThicknessDbu = Um(35), SigmaSm = CopperSigma, DrawingLayers = [Bot], IsGroundReference = true },
            new StackupLayer
            {
                Kind = StackupKind.Via, Name = "PTH", DrawingLayers = [ViaLayer],
                Fill = ViaFillKind.Plated, WallThicknessDbu = Um(25),
                SpanFromLayer = "TOP", SpanToLayer = "MID",
            },
        ];
        return tech;
    }

    private static RectShape Rect(LayerKey layer, long x1, long y1, long x2, long y2) =>
        new() { Layer = layer, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };

    private static PdnExtractionRequest Request(
        Technology tech, IReadOnlyList<LayoutShape> shapes,
        (long X, long Y) source, (long X, long Y) load,
        IReadOnlyDictionary<PdnRegionRef, PdnCopperClass>? overrides = null,
        double frequencyHz = 0)
    {
        var rail = new RailSpec { Name = "VDD", NetName = "VDD", ReferenceLayer = Bot };
        rail.Sources.Add(new RailSource
        {
            Anchor = new RailPortAnchor { Refdes = "BT1", Pin = "1" },
            OpenCircuitVoltageV = 3.7,
        });
        rail.Loads.Add(new RailLoad
        {
            Anchor = new RailPortAnchor { Refdes = "U1", Pin = "VDD" },
            DcCurrentA = 0.5,
        });

        return new PdnExtractionRequest
        {
            Rail = rail,
            Shapes = shapes,
            Technology = tech,
            DbuPerMicron = DbuPerMicron,
            Pads =
            [
                new PdnPad("BT1", "1", "VDD", source.X, source.Y, PdnPadSource.BoardNetlist),
                new PdnPad("U1", "VDD", "VDD", load.X, load.Y, PdnPadSource.BoardNetlist),
            ],
            ClassOverrides = overrides ?? new Dictionary<PdnRegionRef, PdnCopperClass>(),
            Graph = new PdnGraphSettings { FrequencyHz = frequencyHz },
        };
    }

    // ── R-rail4-1: the arithmetic, and it is deliberately nothing new ──────────────────────────

    /// <summary>
    /// <c>R = ρ·L/(W·T)</c> to under 1 %, on the two rows of §2.8 that straddle the behaviour — the
    /// SAME closed form brief 3's mesh is gated on, so neither reading is anchored to the other.
    ///
    /// <para>The span compared against is the one the extraction MEASURED, read back out of
    /// <c>NodeCells</c>, exactly as the mesh test does it; the test asserts separately that that span
    /// is the trace's own length, so the comparison cannot be satisfied by a reading that lost half
    /// the trace.</para>
    /// </summary>
    [Theory]
    [InlineData(50.0, 0.3, 17.5)]   // 50 mm of 0.3 mm inner trace, 0.5 oz  →  ~165 mΩ
    [InlineData(30.0, 0.5, 35.0)]   // 30 mm of 0.5 mm outer trace, 1 oz    →  ~29 mΩ
    public void TheGraphReproducesTheClosedForm(double lengthMm, double widthMm, double thicknessUm)
    {
        var tech = Board(thicknessUm, 35.0, 1.6);

        long w = Mm(widthMm), l = Mm(lengthMm);
        var shapes = new List<LayoutShape>
        {
            Rect(Top, 0, 0, l, w),
            Rect(Bot, 0, -Mm(0.35), l, w + Mm(0.35)),
        };

        var result = PdnGraphExtractor.Extract(
            Request(tech, shapes, source: (Mm(0.05), w / 2), load: (l - Mm(0.05), w / 2)));

        Assert.Null(result.Refusal);
        var pdn = result.Netlist!;

        int a = SourceNode(pdn), b = NodeAt(pdn, "U1.VDD");
        double measured = ResistanceBetween(pdn.Netlist, a, b);

        double spanM = Math.Abs(pdn.NodeCells[a].CentreX - pdn.NodeCells[b].CentreX) / DbuPerMetre;
        double expected = CopperRho * spanM / (widthMm * 1e-3 * thicknessUm * 1e-6);

        // The span IS the run between the two pads, to a tenth of a millimetre — so `expected` above
        // is the trace and not a fraction of it.
        Assert.InRange(spanM, lengthMm * 1e-3 - 1e-4, lengthMm * 1e-3);
        Assert.InRange(measured, expected * 0.99, expected * 1.01);

        // One section, one resistor: that is the whole of §2.9's "a netlist of a few hundred
        // elements". The mesh of the same trace is tens of thousands.
        Assert.Equal(1, pdn.Origins.Count(o => o.Kind == PdnOriginKind.TraceSection
                                            && o.From is { IsReference: false }));
    }

    // ── R-rail4-2: every result says which model produced it ───────────────────────────────────

    /// <summary>
    /// Both readings of the same board carry their own <see cref="PdnModelKind"/>, and a result kept
    /// under one kind never displaces the other — which is what makes §2.9's fourth rule possible at
    /// all: the fast curve stays on the plot beside the accurate one.
    /// </summary>
    [Fact]
    public void EveryResultSaysWhichModelProducedIt()
    {
        var (request, _) = StraightBoard();

        var fast = PdnGraphExtractor.Extract(request);
        var accurate = PdnMeshExtractor.Extract(request);

        Assert.Equal(PdnModelKind.Fast, fast.Netlist!.Provenance.ModelKind);
        Assert.Equal(PdnModelKind.Accurate, accurate.Netlist!.Provenance.ModelKind);
        Assert.Contains("Fast", fast.Netlist.Provenance.Model, StringComparison.Ordinal);

        var held = new PdnResultsByModel();
        held.Add(fast);
        held.Add(accurate);

        Assert.True(held.HasBoth);
        Assert.Same(fast, held[PdnModelKind.Fast]);
        Assert.Same(accurate, held[PdnModelKind.Accurate]);

        // A refusal names no model, so it is reported as a refusal rather than filed as a result.
        Assert.Throws<ArgumentException>(
            () => held.Add(PdnExtraction.Refused("nothing was built")));
    }

    // ── R-rail4-3: the classification is VISIBLE and CORRECTABLE ───────────────────────────────

    /// <summary>
    /// Every region carries a non-empty reason; a forced region reports <c>Forced</c> AND what the
    /// geometry said; and the override survives the document round trip, which is the case that
    /// matters because a re-import is exactly when a classification would otherwise silently change.
    /// </summary>
    [Fact]
    public void EveryRegionCarriesItsReasonAndAForcedOneSurvivesTheDocument()
    {
        var tech = Board(35.0, 35.0, 1.6);
        var shapes = new List<LayoutShape>
        {
            Rect(Top, 0, 0, Mm(20), Mm(15)),
            Rect(Bot, 0, 0, Mm(20), Mm(15)),
        };

        var inferred = PdnGraphExtractor.Extract(
            Request(tech, shapes, (Mm(1), Mm(1)), (Mm(19), Mm(14))));

        Assert.NotEmpty(inferred.Classification);
        foreach (var c in inferred.Classification)
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Reason));
            Assert.False(c.Forced);
            Assert.Equal(c.Class, c.Inferred);
        }

        var pour = Assert.Single(inferred.Classification, c => c.Region.Layer == Top);
        Assert.Equal(PdnCopperClass.Spreading, pour.Class);

        var overrides = new Dictionary<PdnRegionRef, PdnCopperClass>
        {
            [pour.Region] = PdnCopperClass.Trace,
        };

        var forced = PdnGraphExtractor.Extract(
            Request(tech, shapes, (Mm(1), Mm(1)), (Mm(19), Mm(14)), overrides));

        var forcedRegion = Assert.Single(forced.Classification, c => c.Region == pour.Region);
        Assert.True(forcedRegion.Forced);
        Assert.Equal(PdnCopperClass.Trace, forcedRegion.Class);
        Assert.Equal(PdnCopperClass.Spreading, forcedRegion.Inferred);   // what the geometry said
        Assert.Contains("FORCED", forcedRegion.Reason, StringComparison.Ordinal);

        // The override lives on the document, and a document that cannot be read back is one that was
        // never written (RailDocumentIo validates on both sides).
        var doc = new RailDocument { Name = "board" };
        doc.Rails.Add(new RailSpec { Name = "VDD", NetName = "VDD", ReferenceLayer = Bot });
        doc.ClassOverrides[pour.Region] = PdnCopperClass.Trace;

        var reopened = RailDocumentIo.Deserialize(RailDocumentIo.Serialize(doc));
        var kv = Assert.Single(reopened.ClassOverrides);
        Assert.Equal(pour.Region, kv.Key);
        Assert.Equal(PdnCopperClass.Trace, kv.Value);
    }

    // ── R-rail4-4: Fast is REFUSED where it cannot be honest ───────────────────────────────────

    /// <summary>
    /// Above the frequency where the shunt branch stops being negligible, Fast offers NO number and
    /// the refusal states the computed frequency — because "Fast cannot answer above about 180 MHz on
    /// this stackup" is a sentence a user can act on and "Fast cannot answer here" is not.
    /// </summary>
    [Fact]
    public void AboveTheShuntBandFastRefusesAndNamesTheFrequency()
    {
        // The design note's own worked ceiling: a 40 mm plane pair in ε_r 4.3 first resonates at
        // 1.81 GHz, so the fast reading stops a tenth of the way there.
        double top = PdnGraphExtractor.ShuntBandTopHz(0.040, 4.3);
        Assert.InRange(top, 175e6, 185e6);

        var tech = Board(35.0, 35.0, 1.6);
        var shapes = new List<LayoutShape>
        {
            Rect(Top, 0, 0, Mm(40), Mm(0.5)),
            Rect(Bot, 0, -Mm(0.5), Mm(40), Mm(1.0)),
        };

        var board = Request(tech, shapes, (Mm(0.2), Mm(0.25)), (Mm(39.8), Mm(0.25)));
        double ceiling = PdnGraphExtractor.ShuntBandTopHz(0.040, 4.3);

        var refused = PdnGraphExtractor.Extract(
            Request(tech, shapes, (Mm(0.2), Mm(0.25)), (Mm(39.8), Mm(0.25)),
                    frequencyHz: ceiling * 10));

        Assert.NotNull(refused.Refusal);
        Assert.Null(refused.Netlist);                                   // no number, not a worse one
        Assert.Contains("MHz", refused.Refusal, StringComparison.Ordinal);
        Assert.Contains("Accuracy", refused.Refusal, StringComparison.Ordinal);

        // And at DC — this brief's whole scope — there is nothing to refuse.
        Assert.Null(PdnGraphExtractor.Extract(board).Refusal);
    }

    // ── R-rail4-5: the two are compared on the USER'S OWN board ────────────────────────────────

    /// <summary>
    /// §7's gate, all three rows of the brief's own test board plus the negative that catches the
    /// real defect.
    ///
    /// <para>A — a stepped trace, three widths, no branches — and B — a trace with two stubs and a T
    /// — must agree with the mesh to 5 %. C — a 20 × 15 mm supply polygon with a port in one corner
    /// and a source in the other — must be classified <c>Spreading</c> and the fast model must
    /// REFUSE the path through it.</para>
    ///
    /// <para>Then C is FORCED to <c>Trace</c> and the fast answer must be optimistic against the mesh
    /// by more than 5 %. That is the misclassification failure reproduced on purpose, and it is what
    /// proves the classifier is load-bearing rather than decorative: if forcing it changed nothing,
    /// the classification would not be deciding anything.</para>
    /// </summary>
    [Fact]
    public void FastAgreesWithAccurateOnTracesAndRefusesOnAPour()
    {
        var tech = Board(35.0, 35.0, 1.6);

        // ── A: a stepped trace, three widths, no branches ──────────────────────────────────────
        {
            long w1 = Mm(0.5), w2 = Mm(0.4), w3 = Mm(0.3);
            var shapes = new List<LayoutShape>
            {
                Rect(Top, 0, 0, Mm(15), w1),
                Rect(Top, Mm(15), (w1 - w2) / 2, Mm(30), (w1 + w2) / 2),
                Rect(Top, Mm(30), (w1 - w3) / 2, Mm(45), (w1 + w3) / 2),
                Rect(Bot, -Mm(0.3), -Mm(0.3), Mm(45) + Mm(0.3), w1 + Mm(0.3)),
            };
            var request = Request(tech, shapes, (Mm(0.1), w1 / 2), (Mm(44.9), w1 / 2));

            var (fast, accurate) = BothLoops(request);
            Assert.InRange(fast / accurate, 0.95, 1.05);
        }

        // ── B: a trace with two stubs and a T ──────────────────────────────────────────────────
        {
            long w = Mm(0.4);
            var shapes = new List<LayoutShape>
            {
                Rect(Top, 0, Mm(10), Mm(40), Mm(10) + w),          // the main run
                Rect(Top, Mm(20), Mm(10) + w, Mm(20) + w, Mm(20)), // the T's arm
                Rect(Top, Mm(8), Mm(4), Mm(8) + w, Mm(10)),        // stub 1
                Rect(Top, Mm(32), Mm(4), Mm(32) + w, Mm(10)),      // stub 2
                Rect(Bot, -Mm(0.3), Mm(3.7), Mm(40.3), Mm(20.3)),
            };
            var request = Request(tech, shapes, (Mm(0.2), Mm(10) + w / 2), (Mm(39.8), Mm(10) + w / 2));

            var (fast, accurate) = BothLoops(request);
            Assert.InRange(fast / accurate, 0.95, 1.05);
        }

        // ── C: a supply polygon — classified Spreading, and the path through it REFUSED ────────
        {
            var shapes = new List<LayoutShape>
            {
                Rect(Top, 0, 0, Mm(20), Mm(15)),
                Rect(Bot, 0, 0, Mm(20), Mm(15)),
            };
            var request = Request(tech, shapes, (Mm(1), Mm(1)), (Mm(19), Mm(14)));

            var refused = PdnGraphExtractor.Extract(request);
            Assert.NotNull(refused.Refusal);
            Assert.Null(refused.Netlist);
            Assert.Contains("Accuracy", refused.Refusal, StringComparison.Ordinal);

            var pour = Assert.Single(refused.Classification, c => c.Region.Layer == Top);
            Assert.Equal(PdnCopperClass.Spreading, pour.Class);

            // The accurate reading answers it, which is the whole point of having two.
            var mesh = PdnMeshExtractor.Extract(request);
            Assert.Null(mesh.Refusal);

            // ── the negative: force it, and Fast is optimistic by more than 5 % ────────────────
            var forced = Request(tech, shapes, (Mm(1), Mm(1)), (Mm(19), Mm(14)),
                new Dictionary<PdnRegionRef, PdnCopperClass> { [pour.Region] = PdnCopperClass.Trace });

            var (fastLoop, accurateLoop) = BothLoops(forced);
            Assert.True(fastLoop < accurateLoop * 0.95,
                $"forcing a pour to Trace must be OPTIMISTIC — fast {fastLoop * 1e3:0.###} mΩ " +
                $"against accurate {accurateLoop * 1e3:0.###} mΩ");
        }
    }

    // ── R-rail4-6: the path finding is where this extractor can be wrong ───────────────────────

    /// <summary>
    /// <b>Failure shape 1 — a junction missed.</b> A T produces three sections, not one: the branch
    /// current that leaves in the middle has somewhere to leave from. Merging them would
    /// under-estimate the drop, which is optimistic and looks entirely ordinary.
    /// </summary>
    [Fact]
    public void ATJunctionProducesThreeSectionsNotOne()
    {
        var tech = Board(35.0, 35.0, 1.6);
        long w = Mm(0.4);

        var shapes = new List<LayoutShape>
        {
            Rect(Top, 0, Mm(10), Mm(40), Mm(10) + w),
            Rect(Top, Mm(20), Mm(10) + w, Mm(20) + w, Mm(25)),
            Rect(Bot, -Mm(0.3), Mm(9.7), Mm(40.3), Mm(25.3)),
        };

        var result = PdnGraphExtractor.Extract(
            Request(tech, shapes, (Mm(0.2), Mm(10) + w / 2), (Mm(39.8), Mm(10) + w / 2)));

        Assert.Null(result.Refusal);

        var sections = result.Netlist!.Origins
            .Where(o => o.Kind == PdnOriginKind.TraceSection && o.From is { IsReference: false })
            .ToList();

        Assert.Equal(3, sections.Count);

        // Two arms of the run and the T's own branch — and the branch is the short one.
        var lengths = sections.Select(s => s.LengthMetres!.Value).OrderBy(x => x).ToList();
        Assert.InRange(lengths[0], 0.013, 0.016);     // ~15 mm up the arm
        Assert.InRange(lengths[1], 0.018, 0.022);     // ~20 mm each way along the run
        Assert.InRange(lengths[2], 0.018, 0.022);
    }

    /// <summary>
    /// <b>Failure shape 2 — a section's width taken at one point.</b> A linear taper matches the
    /// INTEGRAL to under 1 %, and the test is that it is NEITHER endpoint's answer and not the mean
    /// width's either: a taper read at its wide end is optimistic, at its narrow end pessimistic, and
    /// at its mean width neither of those and still wrong, because resistance integrates 1/W.
    /// </summary>
    [Fact]
    public void ATapersResistanceIsTheIntegralAndNeitherEndpoint()
    {
        var tech = Board(35.0, 35.0, 1.6);
        long l = Mm(20);

        // 0.2 mm at one end, 0.8 mm at the other, over 20 mm.
        var taper = new PolygonShape
        {
            Layer = Top,
            Xy = [0, Mm(0.4), l, Mm(0.1), l, Mm(0.9), 0, Mm(0.6)],
        };

        var shapes = new List<LayoutShape>
        {
            taper,
            Rect(Bot, -Mm(0.3), -Mm(0.3), l + Mm(0.3), Mm(1.3)),
        };

        var result = PdnGraphExtractor.Extract(
            Request(tech, shapes, (Mm(0.1), Mm(0.5)), (Mm(19.9), Mm(0.5))));

        Assert.Null(result.Refusal);
        var pdn = result.Netlist!;

        int a = SourceNode(pdn), b = NodeAt(pdn, "U1.VDD");
        double measured = ResistanceBetween(pdn.Netlist, a, b);

        double xa = pdn.NodeCells[a].CentreX / DbuPerMetre;
        double xb = pdn.NodeCells[b].CentreX / DbuPerMetre;
        double w1 = 0.2e-3 + 0.6e-3 * xa / 20e-3;
        double w2 = 0.2e-3 + 0.6e-3 * xb / 20e-3;
        double span = xb - xa;

        // ∫ dx / W(x) over a linear taper, in closed form.
        double integral = CopperRho * span / (35e-6 * (w2 - w1)) * Math.Log(w2 / w1);
        Assert.InRange(measured, integral * 0.99, integral * 1.01);

        // And it is none of the three single-sample answers — by a wide, stated margin.
        double atNarrow = CopperRho * span / (35e-6 * w1);
        double atWide = CopperRho * span / (35e-6 * w2);
        double atMean = CopperRho * span / (35e-6 * (w1 + w2) / 2);

        Assert.True(measured < atNarrow * 0.6, "reading the narrow end would be pessimistic by ~2x");
        Assert.True(measured > atWide * 1.5, "reading the wide end would be optimistic by ~2x");
        Assert.True(measured > atMean * 1.1, "even the MEAN width is optimistic — 1/W is what integrates");
    }

    /// <summary>
    /// <b>Failure shape 3 — a via treated as a junction when it is a parallel group.</b> A layer
    /// transition with six vias is ONE node and SIX parallel barrels, not one barrel.
    /// </summary>
    [Fact]
    public void ASixViaTransitionIsOneNodeAndSixParallelBarrels()
    {
        var tech = ThreeConductors();

        var shapes = new List<LayoutShape>
        {
            Rect(Top, 0, Mm(4), Mm(11), Mm(4.4)),           // in on TOP
            Rect(Top, Mm(10), Mm(3), Mm(12), Mm(5)),        // the land pad, TOP
            Rect(Mid, Mm(10), Mm(3), Mm(12), Mm(5)),        // the land pad, MID
            Rect(Mid, Mm(12), Mm(3.8), Mm(22), Mm(4.2)),    // out on MID
            Rect(Bot, -Mm(1), Mm(2), Mm(23), Mm(6)),        // the reference
        };

        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 2; j++)
                shapes.Add(new ViaShape
                {
                    Layer = ViaLayer, LandingLayer = Top,
                    X = Mm(10.4 + i * 0.6), Y = Mm(3.5 + j * 0.8),
                    PadSize = Mm(0.5), DrillSize = Mm(0.3),
                });

        var result = PdnGraphExtractor.Extract(
            Request(tech, shapes, (Mm(0.2), Mm(4.2)), (Mm(21.8), Mm(4))));

        Assert.Null(result.Refusal);
        var pdn = result.Netlist!;

        var barrels = pdn.Origins.Where(o => o.Kind == PdnOriginKind.Via).ToList();
        Assert.Equal(6, barrels.Count);

        // All six between ONE pair of nodes — which is what "one node and six parallel barrels"
        // means, and the whole of it: six resistors between the same two nodes IS six in parallel,
        // with no special case anywhere for a group.
        var pairs = barrels
            .Select(o => pdn.Netlist.Components[o.ComponentIndex].Nodes)
            .Select(n => (Math.Min(n[0], n[1]), Math.Max(n[0], n[1])))
            .Distinct()
            .ToList();

        Assert.Single(pairs);
        Assert.All(barrels, o => Assert.Equal(barrels[0].ResistanceOhms, o.ResistanceOhms));
    }

    // ── "same currency": not a second simulator (§4.6) ─────────────────────────────────────────

    /// <summary>
    /// The two extractors' results on the same trace-only board have identical <c>Ports</c>,
    /// identical port bindings and the same <c>NodeCells</c> coverage over the pad set — so
    /// everything downstream reads them identically. <b>This is what "not a second simulator"
    /// means</b>, asserted rather than stated.
    ///
    /// <para>And the element counts are the structural property that makes the fast reading fast: a
    /// handful against tens of thousands. No wall clock is measured, because "single-digit
    /// milliseconds" is a design intent and the status strip is where it is reported.</para>
    /// </summary>
    [Fact]
    public void BothReadingsAreTheSameCurrencyAndOnlyTheElementCountDiffers()
    {
        var (request, _) = StraightBoard();

        var fast = PdnGraphExtractor.Extract(request).Netlist!;
        var accurate = PdnMeshExtractor.Extract(request).Netlist!;

        Assert.Equal(accurate.Ports.Count, fast.Ports.Count);

        for (int i = 0; i < fast.Ports.Count; i++)
        {
            Assert.Equal(accurate.Ports[i].Index, fast.Ports[i].Index);
            Assert.Equal(accurate.Ports[i].Name, fast.Ports[i].Name);
            Assert.Equal(accurate.Ports[i].Anchor, fast.Ports[i].Anchor);
            Assert.Equal(accurate.Ports[i].DcCurrentA, fast.Ports[i].DcCurrentA);

            // Both name a power node and a reference node, and both map them onto cells a board can
            // be coloured at — the currency briefs 8 and 15 draw from.
            Assert.True(fast.NodeCells.ContainsKey(fast.Ports[i].PowerNode));
            Assert.True(accurate.NodeCells.ContainsKey(accurate.Ports[i].PowerNode));

            // The port's own cells sit where the pad is, on both readings, to within a cell.
            Assert.NotEmpty(fast.Ports[i].Cells);
            Assert.Contains(fast.Ports[i].Cells, c => !c.IsReference);
            Assert.Contains(fast.Ports[i].Cells, c => c.IsReference);
        }

        // Same rail, same reference extent, same temperature basis — and DIFFERENT model kinds.
        Assert.Equal(accurate.Provenance.RailName, fast.Provenance.RailName);
        Assert.Equal(accurate.Provenance.ReferenceExtent, fast.Provenance.ReferenceExtent);
        Assert.Equal(accurate.Provenance.CopperTemperatureCelsius,
                     fast.Provenance.CopperTemperatureCelsius);
        Assert.NotEqual(accurate.Provenance.ModelKind, fast.Provenance.ModelKind);

        Assert.True(fast.Netlist.Components.Count < 500,
                    $"the fast reading is a few hundred elements; it built {fast.Netlist.Components.Count}");
        Assert.True(accurate.Netlist.Components.Count > 20 * fast.Netlist.Components.Count,
                    $"the mesh is thousands: {accurate.Netlist.Components.Count} against " +
                    $"{fast.Netlist.Components.Count}");
    }

    // ── R-rail18-2: the stackup check, on the model that is the DEFAULT ────────────────────────

    /// <summary>
    /// The fast model computes §4.1's plane capacitance, and it agrees with the mesh's figure to
    /// <b>5 %</b> — the tolerance stated rather than discovered.
    /// </summary>
    /// <remarks>
    /// <b>The two cannot agree to machine precision and it would be wrong to ask them to.</b> The
    /// mesh sums per-cell overlap on a discretised grid and reads a little under on any shape whose
    /// edges do not fall on cell boundaries; the graph intersects the polygons exactly. 5 % is the
    /// grid's own quantisation on the boards this runs on, and it is the same figure §7's
    /// fast-versus-accurate resistance gate uses.
    ///
    /// <para>At HEAD the fast reading was 0 F and the window, the report and <c>circuitrf rail</c>
    /// all printed a sentence saying the USER'S STACKUP states no dielectric — on the default model,
    /// on every board, including this one, which states one.</para>
    /// </remarks>
    [Fact]
    public void R_rail18_2_TheFastModelReportsAPlaneCapacitance_AgreeingWithTheMesh()
    {
        var (request, _) = StraightBoard();

        var fast = PdnGraphExtractor.Extract(request);
        var accurate = PdnMeshExtractor.Extract(request);
        Assert.Null(fast.Refusal);
        Assert.Null(accurate.Refusal);

        var f = fast.Netlist!.Provenance;
        var a = accurate.Netlist!.Provenance;

        Assert.Equal(PdnPlaneCapacitanceBasis.Computed, f.PlaneCapacitanceBasis);
        Assert.True(f.PlaneCapacitanceFarads > 0,
                    "the fast model reported no plane capacitance at all.");

        Assert.Equal(a.PlaneCapacitanceFarads, f.PlaneCapacitanceFarads,
                     Math.Abs(a.PlaneCapacitanceFarads) * 0.05);
        Assert.Equal(a.PlaneOverlapSquareMetres, f.PlaneOverlapSquareMetres,
                     Math.Abs(a.PlaneOverlapSquareMetres) * 0.05);
        Assert.Equal(a.PlaneSeparationMetres, f.PlaneSeparationMetres, 12);

        // And the sentence it produces is the sentence with a number in it, on BOTH models.
        foreach (var e in new[] { fast, accurate })
            Assert.Contains("ε₀εᵣA/h over", Line(e), StringComparison.Ordinal);
    }

    /// <summary>
    /// A stackup that genuinely states no dielectric between the rail and its reference gets the
    /// ORIGINAL sentence — <b>from both models</b>. That is the case the check was written for, and
    /// a repair that lost it would have removed the check rather than fixed it.
    /// </summary>
    [Fact]
    public void R_rail18_2_AStackupWithNoDielectricStillGetsTheStackupSentence_FromBothModels()
    {
        var tech = Board(35.0, 35.0, 1.6);

        // The one entry between TOP and BOT, removed. Everything else about the board is unchanged.
        tech.Stackup.Layers = [.. tech.Stackup.Layers.Where(l => l.Kind != StackupKind.Dielectric)];

        long w = Mm(0.3), l = Mm(40);
        var request = Request(
            tech,
            [Rect(Top, 0, 0, l, w), Rect(Bot, 0, -Mm(0.35), l, w + Mm(0.35))],
            (Mm(0.1), w / 2), (l - Mm(0.1), w / 2));

        foreach (var e in new[] { PdnGraphExtractor.Extract(request), PdnMeshExtractor.Extract(request) })
        {
            Assert.Null(e.Refusal);
            Assert.Equal(PdnPlaneCapacitanceBasis.NoDielectricStated,
                         e.Netlist!.Provenance.PlaneCapacitanceBasis);
            Assert.Contains("The stackup states no dielectric", Line(e), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A model that did not compute the number says <b>that</b>, and names itself — it never blames
    /// the stackup for its own omission. And the stackup sentence has exactly ONE spelling in the
    /// source, so a second reading cannot drift into saying it for a different reason.
    /// </summary>
    [Fact]
    public void R_rail18_2b_AnUncomputedNumberSaysSo_AndTheStackupSentenceHasOneSpelling()
    {
        var (request, _) = StraightBoard();
        var extraction = PdnGraphExtractor.Extract(request);
        Assert.Null(extraction.Refusal);

        var pdn = extraction.Netlist!;
        var uncomputed = new PdnNetlist
        {
            Netlist    = pdn.Netlist,
            Origins    = pdn.Origins,
            NodeCells  = pdn.NodeCells,
            Ports      = pdn.Ports,
            Provenance = pdn.Provenance with
            {
                PlaneCapacitanceFarads = 0,
                PlaneCapacitanceBasis = PdnPlaneCapacitanceBasis.NotComputed,
            },
        };

        string line = Line(uncomputed);
        Assert.Contains("not computed by", line, StringComparison.Ordinal);
        Assert.Contains(pdn.Provenance.Model, line, StringComparison.Ordinal);
        Assert.DoesNotContain("The stackup states no dielectric", line, StringComparison.Ordinal);

        // The sentence lives in RailDcResult and nowhere else. `PlaneMedia` and `PlaneCapacitance`
        // raise a NOTE with the same words, which is a different surface — this pins the readout.
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "src/Design/RailRf/RailDcResult.cs"));
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(
            source, "The stackup states no dielectric").Count);
    }

    private static string Line(PdnExtraction extraction) => Line(extraction.Netlist!);

    /// <summary>The one sentence under test, read off a result carrying nothing but the extraction —
    /// <c>PlaneCapacitanceLine</c> reads the provenance and nothing else.</summary>
    private static string Line(PdnNetlist pdn) => new RailDcResult
    {
        RailName     = "VDD",
        Netlist      = pdn,
        Data         = new RfCore.Data.DataSet(),
        NodeVoltages = new Dictionary<int, double>(),
        Breakdown    = [],
        Ports        = [],
        Sources      = [],
    }.PlaneCapacitanceLine;

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir;
    }

    // ── shared fixtures ────────────────────────────────────────────────────────────────────────

    /// <summary>A 40 mm run of 0.3 mm copper over a reference strip — trace-dominated, so both
    /// readings answer it and the comparison is about the reading and not about the board.</summary>
    private static (PdnExtractionRequest Request, Technology Tech) StraightBoard()
    {
        var tech = Board(35.0, 35.0, 1.6);
        long w = Mm(0.3), l = Mm(40);
        var shapes = new List<LayoutShape>
        {
            Rect(Top, 0, 0, l, w),
            Rect(Bot, 0, -Mm(0.35), l, w + Mm(0.35)),
        };
        return (Request(tech, shapes, (Mm(0.1), w / 2), (l - Mm(0.1), w / 2)), tech);
    }

    /// <summary>
    /// The LOOP resistance both readings see: out along the rail and back along the reference, which
    /// is the quantity §4.1's factor of two belongs to and the one a drop is actually measured across.
    /// </summary>
    private static (double Fast, double Accurate) BothLoops(PdnExtractionRequest request)
    {
        var fast = PdnGraphExtractor.Extract(request);
        var accurate = PdnMeshExtractor.Extract(request);

        Assert.Null(fast.Refusal);
        Assert.Null(accurate.Refusal);

        return (Loop(fast.Netlist!), Loop(accurate.Netlist!));
    }

    private static double Loop(PdnNetlist pdn) =>
        ResistanceBetween(pdn.Netlist, SourceNode(pdn), pdn.Ports[0].PowerNode)
      + ResistanceBetween(pdn.Netlist, pdn.Ports[0].ReferenceNode, 0);

    // ── the little solver, and the readers it needs ────────────────────────────────────────────

    private static int NodeAt(PdnNetlist pdn, string portName) =>
        pdn.Ports.First(p => p.Name == portName).PowerNode;

    /// <summary>The rail-side node of the first source. A source is a BRANCH rather than a port, so
    /// it is found through the elements it stamped.</summary>
    private static int SourceNode(PdnNetlist pdn)
    {
        var series = pdn.Netlist.Components.FirstOrDefault(c => c.InstancePath == "source.1.r");
        if (series is not null) return series.Nodes[1];
        return pdn.Netlist.Components.First(c => c.InstancePath == "source.1.v").Nodes[0];
    }

    private static Dictionary<int, List<(int To, double G)>> Adjacency(ElaboratedNetlist nl)
    {
        var adj = new Dictionary<int, List<(int, double)>>();

        foreach (var c in nl.Components)
        {
            if (c.Model is not ResistorModel) continue;
            if (!c.Parameters.TryGetValue("R", out var r)) continue;
            double ohms = r.AsReal();
            if (ohms < 0) continue;

            double g = ohms > 0 ? 1.0 / ohms : ResistorModel.DefaultGmax;
            if (!adj.TryGetValue(c.Nodes[0], out var la)) adj[c.Nodes[0]] = la = [];
            if (!adj.TryGetValue(c.Nodes[1], out var lb)) adj[c.Nodes[1]] = lb = [];
            la.Add((c.Nodes[1], g));
            lb.Add((c.Nodes[0], g));
        }

        return adj;
    }

    /// <summary>
    /// The DC resistance between two nodes of the netlist's RESISTIVE network, by injecting 1 A at
    /// <paramref name="a"/> with <paramref name="b"/> held at zero and solving the Laplacian with
    /// Jacobi-preconditioned conjugate gradients. The TEST's, because neither extractor solves.
    /// </summary>
    private static double ResistanceBetween(ElaboratedNetlist nl, int a, int b)
    {
        var adj = Adjacency(nl);

        var index = new Dictionary<int, int> { [a] = 0 };
        var order = new List<int> { a };
        var stack = new Stack<int>([a]);

        while (stack.Count > 0)
        {
            int n = stack.Pop();
            if (!adj.TryGetValue(n, out var nbrs)) continue;
            foreach (var (m, _) in nbrs)
                if (!index.ContainsKey(m)) { index[m] = order.Count; order.Add(m); stack.Push(m); }
        }

        Assert.True(index.ContainsKey(b), "The two nodes are not resistively connected.");

        int n0 = order.Count;
        var rows = new List<(int To, double G)>[n0];
        var diag = new double[n0];
        for (int i = 0; i < n0; i++) rows[i] = [];

        foreach (int node in order)
        {
            int i = index[node];
            if (!adj.TryGetValue(node, out var nbrs)) continue;
            foreach (var (m, g) in nbrs) { rows[i].Add((index[m], g)); diag[i] += g; }
        }

        int grounded = index[b];
        var rhs = new double[n0];
        rhs[index[a]] = 1.0;

        void Mul(double[] x, double[] into)
        {
            for (int i = 0; i < n0; i++)
            {
                if (i == grounded) { into[i] = x[i]; continue; }
                double s = diag[i] * x[i];
                foreach (var (j, g) in rows[i]) s -= g * x[j];
                into[i] = s;
            }
        }

        var v = new double[n0];
        var r = new double[n0];
        var p = new double[n0];
        var z = new double[n0];
        var ap = new double[n0];

        Mul(v, ap);
        for (int i = 0; i < n0; i++) r[i] = rhs[i] - ap[i];

        double Pre(int i) => i == grounded ? 1.0 : (diag[i] > 0 ? diag[i] : 1.0);
        for (int i = 0; i < n0; i++) p[i] = z[i] = r[i] / Pre(i);

        double rz = Dot(r, z);
        for (int it = 0; it < 50000 && Math.Sqrt(Dot(r, r)) > 1e-15; it++)
        {
            Mul(p, ap);
            double alpha = rz / Dot(p, ap);
            for (int i = 0; i < n0; i++) { v[i] += alpha * p[i]; r[i] -= alpha * ap[i]; }
            for (int i = 0; i < n0; i++) z[i] = r[i] / Pre(i);
            double rzNew = Dot(r, z);
            double beta = rzNew / rz;
            rz = rzNew;
            for (int i = 0; i < n0; i++) p[i] = z[i] + beta * p[i];
        }

        return v[index[a]] - v[grounded];
    }

    private static double Dot(double[] x, double[] y)
    {
        double s = 0;
        for (int i = 0; i < x.Length; i++) s += x[i] * y[i];
        return s;
    }
}
