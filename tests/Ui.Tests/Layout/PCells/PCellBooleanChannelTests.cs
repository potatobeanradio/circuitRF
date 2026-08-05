using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Layout.PCells.Wire;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout.PCells;

/// <summary>
/// The boolean host-call channel (wire schema §8), driven end to end over a real pipe.
///
/// <para><b>What these actually prove, and why a pure-C# test could not.</b> The point of the
/// channel is that a generator's booleans are performed by circuitRF's own clipper, so a script's
/// answer and circuitRF's own answer cannot differ. Testing the service in isolation would check
/// that Clipper2 works — which is already covered. What needs proving is the round trip: a script
/// asks mid-generate, the host answers WITHOUT losing its place in the conversation, and the
/// coordinates that come back are exact.</para>
/// </summary>
public sealed class PCellBooleanChannelTests
{
    private static readonly Technology Tech = new()
    {
        Name = "T",
        Layers = { new LayerDef { Key = new LayerKey(1, 0), Name = "M1" } },
    };

    private static readonly PCellLayerSelection NoLayers = new(null, null);

    /// <summary>Writes a generator script that asks circuitRF to clip, and returns the result.</summary>
    private static string WriteScript(string body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-bool-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "gen.py");
        File.WriteAllText(path, "import circuitrf_pcell as crf\n\n" + body + "\n\ncrf.run()\n");
        return path;
    }

    private static PCellResult Run(string scriptPath, PythonRunner runner)
        => runner.Generate("OP", new Dictionary<string, PCellValue>(),
                           Array.Empty<PCellWireParameterDecl>(), Tech, NoLayers);

    // ── the round trip ────────────────────────────────────────────────────────

    /// <summary>
    /// Two overlapping squares union into ONE region whose outline is the exact silhouette — the
    /// headline: a script asked mid-generate and got circuitRF's own clipper's answer back.
    /// </summary>
    [PythonFact]
    public void AUnionAskedMidGenerate_ComesBackAsOneExactRegion()
    {
        string script = WriteScript("""
            @crf.generator("OP", [])
            def op(params, tech):
                a = [(0, 0), (200, 0), (200, 200), (0, 200)]
                b = [(100, 0), (300, 0), (300, 200), (100, 200)]
                polys = crf.clip("or", [a], [b])
                shapes = [crf.Polygon(crf.Layer(1, 0), [c for pt in p.outer for c in pt])
                          for p in polys]
                return crf.Result(shapes=shapes, pins=[])
            """);

        using var runner = PythonRunner.Start(script);
        runner.Describe();
        var result = Run(script, runner);

        Assert.Equal(1, runner.ServiceCallCount);
        var poly = Assert.IsType<PolygonShape>(Assert.Single(result.Shapes));

        // The union of [0,200]x[0,200] and [100,300]x[0,200] is exactly [0,300]x[0,200].
        var xs = poly.Xy.Where((_, i) => i % 2 == 0).ToArray();
        var ys = poly.Xy.Where((_, i) => i % 2 == 1).ToArray();
        Assert.Equal(0, xs.Min());
        Assert.Equal(300, xs.Max());
        Assert.Equal(0, ys.Min());
        Assert.Equal(200, ys.Max());

        // Area, not vertex count: Clipper2 keeps the collinear vertices where the two operands' edges
        // met, so the outline is 6 points describing the same rectangle. Area is what says the union
        // is genuinely one merged region rather than one operand or an overlapping pair.
        Assert.Equal(300L * 200L, SignedArea(poly.Xy));
    }

    /// <summary>Twice the shoelace area, halved — exact in integers for an axis-aligned region.</summary>
    private static long SignedArea(long[] xy)
    {
        long twice = 0;
        int n = xy.Length / 2;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            twice += xy[i * 2] * xy[j * 2 + 1] - xy[j * 2] * xy[i * 2 + 1];
        }
        return Math.Abs(twice) / 2;
    }

    /// <summary>
    /// Difference is ORDER-DEPENDENT, and the host must apply it in the direction the script asked.
    /// A subtraction performed the wrong way round still produces a plausible-looking region, which
    /// is exactly why this is asserted on the resulting extent rather than on "something came back".
    /// </summary>
    [PythonFact]
    public void ADifference_SubtractsInTheDirectionAsked()
    {
        string script = WriteScript("""
            @crf.generator("OP", [])
            def op(params, tech):
                big = [(0, 0), (300, 0), (300, 100), (0, 100)]
                cut = [(200, 0), (400, 0), (400, 100), (200, 100)]
                polys = crf.clip("not", [big], [cut])
                shapes = [crf.Polygon(crf.Layer(1, 0), [c for pt in p.outer for c in pt])
                          for p in polys]
                return crf.Result(shapes=shapes, pins=[])
            """);

        using var runner = PythonRunner.Start(script);
        runner.Describe();
        var poly = Assert.IsType<PolygonShape>(Assert.Single(Run(script, runner).Shapes));

        var xs = poly.Xy.Where((_, i) => i % 2 == 0).ToArray();
        Assert.Equal(0, xs.Min());
        Assert.Equal(200, xs.Max());   // 300 would mean nothing was removed; 400 the wrong direction
    }

    /// <summary>
    /// A hole survives the round trip as a hole. circuitRF's own geometry model carries holes
    /// natively (§3.1a), and the reply describes them separately from the outer ring — so a
    /// generator subtracting a via pad from a pour gets back the region it asked for, not a
    /// keyholed approximation of it.
    /// </summary>
    [PythonFact]
    public void AHole_SurvivesAsAHole_NotAsAFilledRegion()
    {
        string script = WriteScript("""
            @crf.generator("OP", [])
            def op(params, tech):
                pour = [(0, 0), (400, 0), (400, 400), (0, 400)]
                pad  = [(100, 100), (300, 100), (300, 300), (100, 300)]
                polys = crf.clip("not", [pour], [pad])
                p = polys[0]
                shapes = [crf.Polygon(
                    crf.Layer(1, 0),
                    [c for pt in p.outer for c in pt],
                    holes=[[c for pt in h for c in pt] for h in p.holes])]
                return crf.Result(shapes=shapes, pins=[])
            """);

        using var runner = PythonRunner.Start(script);
        runner.Describe();
        var poly = Assert.IsType<PolygonShape>(Assert.Single(Run(script, runner).Shapes));

        var hole = Assert.Single(poly.Holes!);
        var hx = hole.Where((_, i) => i % 2 == 0).ToArray();
        var hy = hole.Where((_, i) => i % 2 == 1).ToArray();
        Assert.Equal(100, hx.Min());
        Assert.Equal(300, hx.Max());
        Assert.Equal(100, hy.Min());
        Assert.Equal(300, hy.Max());
    }

    /// <summary>
    /// Several booleans in one generate, each answered in turn. This is the property that makes the
    /// channel usable at all: servicing a request must not consume the reply the host is waiting
    /// for, or the conversation desynchronises after the first call.
    /// </summary>
    [PythonFact]
    public void ManyBooleansInOneGenerate_AreEachAnswered_WithoutLosingTheReply()
    {
        string script = WriteScript("""
            @crf.generator("OP", [])
            def op(params, tech):
                shapes = []
                for i in range(12):
                    a = [(i * 100, 0), (i * 100 + 150, 0), (i * 100 + 150, 50), (i * 100, 50)]
                    b = [(i * 100 + 100, 0), (i * 100 + 200, 0), (i * 100 + 200, 50), (i * 100 + 100, 50)]
                    for p in crf.clip("or", [a], [b]):
                        shapes.append(crf.Polygon(crf.Layer(1, 0),
                                                  [c for pt in p.outer for c in pt]))
                return crf.Result(shapes=shapes, pins=[])
            """);

        using var runner = PythonRunner.Start(script);
        runner.Describe();
        var result = Run(script, runner);

        Assert.Equal(12, runner.ServiceCallCount);
        Assert.Equal(12, result.Shapes.Count);
    }

    // ── refusals ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A refusal comes back as a refusal the script can see, and the generate fails by name rather
    /// than the connection going quiet — the failure mode a service loop makes possible if the host
    /// were to stop answering.
    /// </summary>
    [PythonFact]
    public void AnUnknownRule_IsRefusedByName_NotSilence()
    {
        string script = WriteScript("""
            from circuitrf_pcell.services import channel

            @crf.generator("OP", [])
            def op(params, tech):
                # Deliberately bypasses crf.clip's own rule check to exercise the HOST's refusal.
                channel().call({"op": "clip", "rule": "nonsense", "subject": [], "clip": []}, [])
                return crf.Result(shapes=[], pins=[])
            """);

        using var runner = PythonRunner.Start(script);
        runner.Describe();

        var ex = Assert.Throws<PCellWireException>(() => Run(script, runner));
        Assert.Contains("nonsense", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Asking for a boolean with no host to ask says so, rather than quietly computing a different
    /// answer. There is deliberately no Python fallback clipper — a second implementation is the
    /// thing this channel exists to avoid.
    /// </summary>
    [PythonFact]
    public void WithNoHostConnected_TheRefusalNamesWhy_AndThereIsNoFallback()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-bool-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "offline.py");
        File.WriteAllText(path, """
            import circuitrf_pcell as crf
            try:
                crf.clip("or", [[(0, 0), (1, 0), (1, 1)]], [])
                print("NO-REFUSAL")
            except crf.HostUnavailable as ex:
                print("REFUSED:", ex)
            """);

        var psi = new System.Diagnostics.ProcessStartInfo(PythonRunner.Interpreter!)
        {
            RedirectStandardOutput = true,
            UseShellExecute        = false,
            WorkingDirectory       = PythonRunner.PackageRoot,
        };
        psi.ArgumentList.Add(path);
        psi.Environment["PYTHONPATH"] = PythonRunner.PackageRoot;

        using var p = System.Diagnostics.Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10_000);

        Assert.StartsWith("REFUSED:", output.Trim(), StringComparison.Ordinal);
        Assert.Contains("circuitRF", output, StringComparison.Ordinal);
    }

    // ── offset ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A grow comes back at exactly the asked-for distance. This is the operation a kit uses to derive
    /// one layer from another — a well grown out of the diffusion it must enclose — so being off by a
    /// database unit is a design-rule violation that draws perfectly.
    /// </summary>
    [PythonFact]
    public void AGrow_ExpandsByExactlyTheAskedDistance()
    {
        string script = WriteScript("""
            @crf.generator("OP", [])
            def op(params, tech):
                square = [(0, 0), (100, 0), (100, 100), (0, 100)]
                polys = crf.offset(25, [square])
                shapes = [crf.Polygon(crf.Layer(1, 0), [c for pt in p.outer for c in pt])
                          for p in polys]
                return crf.Result(shapes=shapes, pins=[])
            """);

        using var runner = PythonRunner.Start(script);
        runner.Describe();
        var poly = Assert.IsType<PolygonShape>(Assert.Single(Run(script, runner).Shapes));

        var xs = poly.Xy.Where((_, i) => i % 2 == 0).ToArray();
        var ys = poly.Xy.Where((_, i) => i % 2 == 1).ToArray();
        Assert.Equal(-25, xs.Min());
        Assert.Equal(125, xs.Max());
        Assert.Equal(-25, ys.Min());
        Assert.Equal(125, ys.Max());
    }

    /// <summary>
    /// A shrink that consumes the region yields NOTHING, and that is an answer rather than a failure —
    /// the same outcome the editor's own Offset command produces.
    /// </summary>
    [PythonFact]
    public void AShrinkThatConsumesTheRegion_YieldsNothing_NotAnError()
    {
        string script = WriteScript("""
            @crf.generator("OP", [])
            def op(params, tech):
                square = [(0, 0), (100, 0), (100, 100), (0, 100)]
                polys = crf.offset(-200, [square])
                shapes = [crf.Polygon(crf.Layer(1, 0), [c for pt in p.outer for c in pt])
                          for p in polys]
                return crf.Result(shapes=shapes, pins=[])
            """);

        using var runner = PythonRunner.Start(script);
        runner.Describe();

        Assert.Empty(Run(script, runner).Shapes);
        Assert.Equal(1, runner.ServiceCallCount);   // it was asked, and answered
    }

    /// <summary>
    /// A script's grow and circuitRF's OWN Offset command agree exactly. That agreement is the entire
    /// reason this is a host call — two implementations would differ in a way nothing on screen shows.
    /// </summary>
    [PythonFact]
    public void AScriptsGrow_AgreesWithCircuitRfsOwnOffsetCommand()
    {
        string script = WriteScript("""
            @crf.generator("OP", [])
            def op(params, tech):
                square = [(0, 0), (300, 0), (300, 200), (0, 200)]
                polys = crf.offset(40, [square])
                shapes = [crf.Polygon(crf.Layer(1, 0), [c for pt in p.outer for c in pt])
                          for p in polys]
                return crf.Result(shapes=shapes, pins=[])
            """);

        using var runner = PythonRunner.Start(script);
        runner.Describe();
        var viaScript = Assert.IsType<PolygonShape>(Assert.Single(Run(script, runner).Shapes));

        var direct = LayoutBooleans.Offset(
            new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 300, Y2 = 200 },
            40, null);
        var viaEditor = Assert.IsType<PolygonShape>(Assert.Single(direct.Shapes));

        Assert.Equal(SignedArea(viaEditor.Xy), SignedArea(viaScript.Xy));
        Assert.Equal(Bounds(viaEditor.Xy), Bounds(viaScript.Xy));
    }

    private static (long MinX, long MinY, long MaxX, long MaxY) Bounds(long[] xy)
    {
        var xs = xy.Where((_, i) => i % 2 == 0).ToArray();
        var ys = xy.Where((_, i) => i % 2 == 1).ToArray();
        return (xs.Min(), ys.Min(), xs.Max(), ys.Max());
    }
}
