// TraceImpedanceProbe — the layout's "what Z0 is this trace?" review tool (round-7 field report).
// One test per claim: it agrees with an independent closed form for microstrip and for grounded
// coplanar waveguide, it refuses a pad and a tee rather than printing a width they do not have, and
// it flags a return path that breaks under the trace.

using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Messages;

namespace CircuitRF.Ui.Tests.Em;

public class TraceImpedanceProbeTests
{
    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey Gnd = new(2, 0);

    private static long Um(double um) => (long)Math.Round(um * LayoutUnits.DefaultDbuPerMicron);

    /// <summary>Metal on Top, a dielectric, and a copper layer under it — the plane is drawn COPPER,
    /// not a stackup ground, so the probe has to find it.</summary>
    private static Technology Tech(double tMetalUm, double hUm, double epsr) => new()
    {
        Name = "probe",
        Layers =
        [
            new LayerDef { Key = Top, Name = "Top", Purpose = "conductor" },
            new LayerDef { Key = Gnd, Name = "Plane", Purpose = "conductor" },
        ],
        Stackup = new Stackup
        {
            Top = BoundaryCondition.Open,
            Bottom = BoundaryCondition.Open,
            Layers =
            [
                new StackupLayer { Kind = StackupKind.Conductor, Name = "Top", ThicknessDbu = Um(tMetalUm),
                                   SigmaSm = 5.8e7, DrawingLayers = [Top] },
                new StackupLayer { Kind = StackupKind.Dielectric, Name = "Core", ThicknessDbu = Um(hUm), Epsr = epsr },
                new StackupLayer { Kind = StackupKind.Conductor, Name = "Plane", ThicknessDbu = Um(35),
                                   SigmaSm = 5.8e7, DrawingLayers = [Gnd] },
            ],
        },
    };

    private static RectShape Rect(LayerKey layer, double x1, double y1, double x2, double y2) =>
        new() { Layer = layer, X1 = Um(x1), Y1 = Um(y1), X2 = Um(x2), Y2 = Um(y2) };

    private static TraceImpedanceResult Probe(IReadOnlyList<LayoutShape> shapes, Technology tech, double xUm, double yUm) =>
        TraceImpedanceProbe.Probe(shapes, tech, LayoutUnits.DefaultDbuPerMicron, Um(xUm), Um(yUm), Top);

    [Fact]
    public void Microstrip_AgreesWithHammerstadJensen()
    {
        // t/W = 0.5 %: inside H-J's own thickness regime, as the kernel's oracle (MicrostripOracleTests)
        // keeps it — outside it the disagreement is H-J's thickness model, not the solve.
        const double w = 1000, h = 500, t = 5, er = 4.4;
        var tech = Tech(t, h, er);
        // A trace drawn at an angle, so the direction really is measured rather than read off an axis.
        var trace = new PolygonShape
        {
            Layer = Top,
            Xy = Line(-8000, -3000, 8000, 3000, w),
        };
        LayoutShape[] shapes = [trace, Rect(Gnd, -12000, -12000, 12000, 12000)];

        var r = Probe(shapes, tech, 0, 0);

        Assert.True(r.Ok, r.Refusal);
        var (z0, eeff) = HammerstadJensen.Compute(w * 1e-6, h * 1e-6, t * 1e-6, er, new MicrostripValidityReporter("test"));
        Assert.Equal(w, r.WidthM * 1e6, 1.0);
        Assert.Equal(z0, r.Z0Ohms, z0 * 0.02);
        Assert.Equal(eeff, r.Eeff, eeff * 0.02);
        Assert.Equal("microstrip", r.Configuration);
        Assert.Equal("Plane", r.ReferenceBelow);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void GroundedCoplanar_AgreesWithConformalMapping()
    {
        // Conductor-backed CPW, zero-thickness conformal-mapping result (Simons, "Coplanar Waveguide
        // Circuits, Components, and Systems", 2001, §2.4; also Wadell, "Transmission Line Design
        // Handbook", 1991, §3.4.3). The metal is 1 µm so the zero-thickness formula applies: measured
        // −0.6 % at 1 µm and −2.4 % at 5 µm, the gap's own sidewall capacitance, falling as t → 0.
        const double w = 300, g = 100, h = 300, er = 4.4;
        var tech = Tech(1, h, er);
        LayoutShape[] shapes =
        [
            Rect(Top, -6000, -w / 2, 6000, w / 2),
            Rect(Top, -6000, w / 2 + g, 6000, 6000),
            Rect(Top, -6000, -6000, 6000, -w / 2 - g),
            Rect(Gnd, -8000, -8000, 8000, 8000),
        ];

        var r = Probe(shapes, tech, 0, 0);

        Assert.True(r.Ok, r.Refusal);
        double a = w / 2, b = w / 2 + g;
        double k = a / b, k3 = Math.Tanh(Math.PI * a / (2 * h)) / Math.Tanh(Math.PI * b / (2 * h));
        double ratio = K(k) / K(Prime(k)), ratio3 = K(k3) / K(Prime(k3));
        double q = ratio3 / ratio;
        double eeff = (1 + er * q) / (1 + q);
        double z0 = 60 * Math.PI / Math.Sqrt(eeff) / (ratio + ratio3);

        Assert.Equal(z0, r.Z0Ohms, z0 * 0.02);
        Assert.Equal(g, r.GapLeftM!.Value * 1e6, 1.0);
        Assert.Equal(g, r.GapRightM!.Value * 1e6, 1.0);
        Assert.Equal("grounded coplanar waveguide", r.Configuration);
        Assert.Equal(TraceImpedanceResult.GapToConductor, r.GapLeftTo);

        static double Prime(double m) => Math.Sqrt(1 - m * m);
        static double K(double m)   // complete elliptic integral of the first kind, by the AGM
        {
            double x = 1, y = Prime(m);
            for (int i = 0; i < 30; i++) (x, y) = ((x + y) / 2, Math.Sqrt(x * y));
            return Math.PI / (2 * x);
        }
    }

    [Fact]
    public void SquarePad_IsRefusedAsNotALine()
    {
        var tech = Tech(35, 500, 4.4);
        LayoutShape[] shapes = [Rect(Top, -300, -300, 300, 300), Rect(Gnd, -5000, -5000, 5000, 5000)];

        var r = Probe(shapes, tech, 50, 20);

        Assert.False(r.Ok);
        Assert.Contains("not a line", r.Refusal);
    }

    [Fact]
    public void Tee_BesideTheJunction_IsRefused()
    {
        // A 400 µm through line with a 200 µm branch down from it; the click is in the branch, one
        // branch-width below the junction, where the narrowest chord is the branch's true width but
        // the copper two widths along is the through line.
        var tech = Tech(35, 500, 4.4);
        LayoutShape[] shapes =
        [
            Rect(Top, -6000, -200, 6000, 200),
            Rect(Top, -100, -6000, 100, -200),
            Rect(Gnd, -8000, -8000, 8000, 8000),
        ];

        var r = Probe(shapes, tech, 0, -300);

        Assert.False(r.Ok);
        Assert.Contains("junction", r.Refusal);
    }

    [Fact]
    public void ReferenceThatBreaksUnderTheTrace_IsFlagged()
    {
        // The plane has a window cut across the trace 3 mm from the click.
        var tech = Tech(35, 500, 4.4);
        var plane = new PolygonShape
        {
            Layer = Gnd,
            Xy = [Um(-8000), Um(-8000), Um(8000), Um(-8000), Um(8000), Um(8000), Um(-8000), Um(8000)],
            // Wound against the outline, as a hole is (NonZero).
            Holes = [[Um(2500), Um(-1500), Um(2500), Um(1500), Um(3500), Um(1500), Um(3500), Um(-1500)]],
        };
        LayoutShape[] shapes = [Rect(Top, -6000, -500, 6000, 500), plane];

        var r = Probe(shapes, tech, -1000, 0);

        Assert.True(r.Ok, r.Refusal);
        Assert.Contains(r.Warnings, w => w.StartsWith("Discontinuity: 'Plane'", StringComparison.Ordinal));
    }

    [Fact]
    public void CanvasCommand_ProbesTheStackupTopLayer_AndPostsTheAnswer()
    {
        // An imported technology can draw the plane OVER the top copper; the click still means the
        // top of the stack, never the draw order.
        var tech = Tech(5, 500, 4.4);
        tech.Layers[0].ZOrder = 0;
        tech.Layers[1].ZOrder = 30;
        var view = new LayoutView();
        view.Shapes.Add(Rect(Top, -6000, -500, 6000, 500));
        view.Shapes.Add(Rect(Gnd, -8000, -8000, 8000, 8000));
        var sink = new Sink();
        var vm = new LayoutEditorViewModel(view, messageSink: sink) { Technology = tech };

        Assert.Equal(Top, vm.TraceImpedanceLayerAt(Um(0), Um(0)));
        var r = vm.ProbeTraceImpedance(Um(0), Um(0));

        Assert.True(r is { Ok: true }, r?.Refusal);
        // One line, not a line per warning and note (owner, 2026-09-24).
        Assert.StartsWith("Trace Z0: Z0 ", Assert.Single(sink.Lines), StringComparison.Ordinal);
    }

    private sealed class Sink : IMessageSink
    {
        public readonly List<string> Lines = [];
        public void Post(MessageLevel level, string text, string? filePath = null) => Lines.Add(text);
        public void Clear() => Lines.Clear();
    }

    /// <summary>The line says what a gap ends on: a via fence with no pour around it reads as a coplanar
    /// edge in the cut, and the reviewer should know that is what was measured to (owner, 2026-09-24).</summary>
    [Fact]
    public void AGapToAViaFence_SaysViaPads()
    {
        var tech = Tech(5, 300, 4.4);
        var shapes = new List<LayoutShape>
        {
            Rect(Top, -6000, -150, 6000, 150),
            Rect(Gnd, -8000, -8000, 8000, 8000),
        };
        for (double x = -5600; x <= 5600; x += 800)
            foreach (double y in new[] { -650.0, 650.0 })
                shapes.Add(new ViaShape { Layer = Gnd, X = Um(x), Y = Um(y), PadSize = Um(500), DrillSize = Um(300),
                                          LandingLayer = Top });

        var r = Probe(shapes, tech, 0, 0);

        Assert.True(r.Ok, r.Refusal);
        Assert.Equal(250, r.GapLeftM!.Value * 1e6, 1.0);
        Assert.Equal(TraceImpedanceResult.GapToViaPad, r.GapLeftTo);
        Assert.Equal(TraceImpedanceResult.GapToViaPad, r.GapRightTo);
        Assert.Contains("G to via pads", r.Summary(), StringComparison.Ordinal);
    }

    /// <summary>Round 8: imported artwork carries jogs of a few microns (a 5 µm offset in a 381 µm
    /// trace on the reported board). The minimum chord through a jog leans across it, and the edge
    /// check used to read that as a bend and refuse the trace.</summary>
    [Fact]
    public void AFewMicronJog_IsStillOneStraightTrace()
    {
        var tech = Tech(35, 300, 4.4);
        // Left edge x = −190.5 below y = 0 and −195.5 above y = 90; right edge +190.5 / +185.5; the
        // 90 µm between is the jog, drawn as one slanted facet each side.
        var trace = new PolygonShape
        {
            Layer = Top,
            Xy = [Um(-190.5), Um(-5000), Um(190.5), Um(-5000), Um(190.5), Um(0), Um(185.5), Um(90),
                  Um(185.5), Um(5000), Um(-195.5), Um(5000), Um(-195.5), Um(90), Um(-190.5), Um(0)],
        };
        LayoutShape[] shapes = [trace, Rect(Gnd, -8000, -8000, 8000, 8000)];

        var r = Probe(shapes, tech, 0, 45);

        Assert.True(r.Ok, r.Refusal);
        Assert.Equal(381, r.WidthM * 1e6, 381 * 0.02);
    }

    /// <summary>Round 8: the current drawing layer was the inner plane under the trace, so the probe
    /// measured the PLANE and refused. A refusal on the preferred layer falls through to the next
    /// copper layer at the point, and the answer says so.</summary>
    [Fact]
    public void APlaneAsTheLayerMeant_FallsThroughToTheTraceAbove()
    {
        var tech = Tech(35, 500, 4.4);
        LayoutShape[] shapes = [Rect(Top, -6000, -500, 6000, 500), Rect(Gnd, -8000, -8000, 8000, 8000)];

        var r = TraceImpedanceProbe.ProbeFirst(shapes, tech, LayoutUnits.DefaultDbuPerMicron, 0, 0, [Gnd, Top]);

        Assert.True(r.Ok, r.Refusal);
        Assert.Equal("Top", r.SignalLayer);
        Assert.Contains(r.Notes, n => n.StartsWith("'Plane' has no straight trace", StringComparison.Ordinal));
    }

    /// <summary>A straight strip of width <paramref name="w"/> from (x1, y1) to (x2, y2), µm.</summary>
    private static long[] Line(double x1, double y1, double x2, double y2, double w)
    {
        double dx = x2 - x1, dy = y2 - y1, len = Math.Sqrt(dx * dx + dy * dy);
        double nx = -dy / len * w / 2, ny = dx / len * w / 2;
        return [Um(x1 + nx), Um(y1 + ny), Um(x1 - nx), Um(y1 - ny), Um(x2 - nx), Um(y2 - ny), Um(x2 + nx), Um(y2 + ny)];
    }
}
