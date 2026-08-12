// ================================================================
//  HarmonicaSchematicExport.cs  —  §7 of brief-harmonicarf-r1c-chrome-readouts-dut-and-export
//
//  R-h9c-15  Export Testbench writes a runnable .csch: symbols placed, wires on the connection
//            grid, the same bias, the same terminations and an analysis matching harmonicaRF's own.
//
//  THE TUNER-VS-P1TONE QUESTION, RESOLVED IN WRITING: this schematic carries an HB analysis (the
//  owner's own "configured the same way as harmonicaRF"), and under a plain type=hb run a Tuner is
//  INERT — HarmonicaInterchange.ExportTestbench's own doc comment already found this for the .cnl
//  export (nothing in HbEngine calls SetRole/SetTone/SetSourceDrive; those are the loadpull engine's).
//  The same reasoning applies unchanged here: this export uses P1Tone (source) and PnTone (load),
//  the two components HbEngine DOES give a band ruler to — never a Tuner pair. "Copy termination
//  set" is a different menu item and stays a Tuner (§7.8: it is driven by the loadpull engine).
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Harmonica;

/// <summary>
/// Builds a <see cref="SchematicEditModel"/> reproducing one harmonicaRF operating point, "best
/// effort" per the owner — a left-to-right/best-effort placement rather than a laid-out drawing, on
/// the connection grid (R7's own on-grid invariant), never off it.
///
/// <para><b>What this can express, and what it refuses rather than silently omits.</b> An SDD (2- or
/// 3-port), one of the five native FET laws, or a Diode — mapped onto the matching built-in
/// <c>SymbolKind</c>. An External DUT and a Touchstone-embedded package (S2p/S4p) are NOT yet
/// expressible as schematic components and are REFUSED BY NAME (<see cref="NotSupportedException"/>)
/// rather than dropped from the exported circuit, which would make it a different, silently wrong
/// one. <see cref="HarmonicaInterchange.ExportTestbench"/>'s <c>.cnl</c> path has none of these
/// limits and stays the fallback for those cases.</para>
/// </summary>
public static class HarmonicaSchematicExport
{
    /// <summary>Left-to-right/outward spacing between placed components, world units. A multiple of
    /// <see cref="SchematicEditModel.GridSize"/> (100) by construction, so every component center
    /// this file ever computes is on-grid before <see cref="AddComponent"/> even snaps it.
    ///
    /// <para><b>Deliberately NOT 400.</b> A 2-pin component's own lead half-length is 200, so a
    /// placement pitch of exactly 400 makes a chained component's NEAR pin land exactly
    /// <c>Pitch - 200 = 200</c> units past its anchor — which is precisely the SDD symbol's own
    /// differential pin spacing (<c>SymbolPortDefs.GenerateSddPorts</c>: the "+" and "−" pins of one
    /// port sit 200 apart). At Pitch=400 the gate bias inductor's own near pin therefore lands
    /// exactly on the DUT's own gate-minus pin — an unintended short between the DUT's two
    /// differential terminals, found by tracing a real export's coordinates against
    /// <c>NetExtractor</c>'s output rather than assumed. 600 clears that coincidence
    /// (<c>600-200=400≠200</c>) while staying an on-grid multiple of 100.</para></summary>
    private const double Pitch = 600.0;

    private enum Direction { Left, Right, Up, Down }

    /// <summary>Per-export mutable state — a small object rather than static fields, so nothing here
    /// is shared between two exports running at once.</summary>
    private sealed class Ctx
    {
        public readonly SchematicEditModel Model = new();
        public int GroundCount;

        /// <summary>Every physical DUT pin coordinate, populated once by <see cref="PlaceDut"/>.
        /// <see cref="ConnectOrthogonal"/> consults this to keep a straight wire from ever running
        /// COLLINEARLY THROUGH a DUT pin it was never meant to touch — NetExtractor's own §5.1 rule
        /// (confirmed directly: a wire endpoint or interior point coinciding with another connection
        /// point unions them, whether or not that was the writer's intent) means a wire that merely
        /// PASSES a pin on its way to somewhere else silently shorts to it. For a multi-port SDD this
        /// is not rare: a 3-port SDD places two of its three ports on the very same X column (gate
        /// above, drain below), so almost any vertical continuation from either risks crossing the
        /// other's pins — found by tracing a real n=3 export's coordinates against NetExtractor's
        /// output, the same way the single gate/gateNeg case was.</summary>
        public readonly List<(double X, double Y)> AvoidPoints = new();
    }

    /// <summary>
    /// Builds the schematic. Throws <see cref="NotSupportedException"/>, naming what could not be
    /// expressed, rather than shipping a schematic that runs and is wrong.
    /// </summary>
    public static SchematicEditModel Export(CircuitModel model, TerminationSet terminations, double pavlDbm)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(terminations);

        if (model.Embedding.HasTouchstone)
            throw new NotSupportedException(
                "Export Testbench (.csch) cannot yet express this document's Touchstone embedding " +
                "(an S2p/S4p block) as a schematic component. Use Export Testbench (.cnl) instead, " +
                "which does.");

        if (model.Dut.Kind == DutKind.External)
            throw new NotSupportedException(
                $"Export Testbench (.csch) cannot yet express an External DUT ('{model.Dut.TypeName}') " +
                "as a schematic component. Use Export Testbench (.cnl) instead, which does.");

        var ctx = new Ctx();
        var (gate, drain, source) = PlaceDut(ctx, model.Dut);

        var pkg = model.Embedding.Package;

        // Outward from the DUT's own terminals: series lead, then the shunt cap AT that same plane
        // (a shunt never advances the node — HarmonicaNetlist.Shunt's own rule).
        var gateOuter = PlaceSeriesRl(ctx, gate, Direction.Left, "RG", "LG", pkg.Rg, pkg.Lg);
        gateOuter     = PlaceShunt(ctx, gateOuter, Direction.Down, "CPG", pkg.Cpg);

        var drainOuter = PlaceSeriesRl(ctx, drain, Direction.Up, "RD", "LD", pkg.Rd, pkg.Ld);
        drainOuter     = PlaceShunt(ctx, drainOuter, Direction.Right, "CPD", pkg.Cpd);

        if (pkg.CgdExt != 0)
            PlaceBridge(ctx, gate, drain, "CGDX", pkg.CgdExt);

        // The source lead — only when the DUT actually has a source terminal (a Diode has none; its
        // "source" parameter would be a dangling branch nothing references, so it is skipped rather
        // than drawn pointing at nothing).
        if (source is { } src)
        {
            if (pkg.Rs != 0 || pkg.Ls != 0)
                TieToGround(ctx, PlaceSeriesRl(ctx, src, Direction.Down, "RS", "LS", pkg.Rs, pkg.Ls));
            else
                TieToGround(ctx, src);
        }

        // The two termination planes — each a THREE-way junction (bias choke, DC-blocked
        // termination, and the embedding chain already wired above), exactly as
        // HarmonicaNetlist/HarmonicaInterchange's own SourcePlane/LoadPlane are.
        //
        // The gate bias tap is deliberately Direction.Up, not Down. SymbolPortDefs.GenerateSddPorts'
        // own convention puts a port's "−" pin 200 below its "+" pin — so gateNeg always sits exactly
        // between gate and anything placed Down from it on the same column (found by tracing a real
        // export's coordinates against NetExtractor's output: the straight wire from gate to a
        // Down-placed LCHG passed collinearly through gateNeg's own coordinate, forming an unintended
        // T-junction that shorted gate+ to the grounded gate− node, for EVERY Pitch — increasing Pitch
        // alone cannot fix this, since gateNeg's Y never moves). Direction.Up moves away from gateNeg
        // instead, which the drain side already does implicitly (its own main chain also runs Up,
        // away from drainNeg) — mirroring that safe convention rather than reproducing gate's own hazard.
        PlaceBias(ctx, gateOuter, Direction.Up, "LCHG", "VGG", model.Bias.Vgs ?? 0.0, model.Settings.BiasChokeHenries);
        PlaceSourceTermination(ctx, gateOuter, Direction.Left, model, terminations, pavlDbm);

        PlaceBias(ctx, drainOuter, Direction.Right, "LCHD", "VDD", model.Bias.Vds, model.Settings.BiasChokeHenries);
        PlaceLoadTermination(ctx, drainOuter, Direction.Up, model, terminations);

        ctx.Model.Analyses.Add(BuildAnalysis(model));
        return ctx.Model;
    }

    // ── the DUT ──────────────────────────────────────────────────────────────

    /// <summary>Places the DUT at the origin and returns its GATE/DRAIN connection points and its
    /// SOURCE point (null for a Diode, which has none).</summary>
    private static ((double X, double Y) Gate, (double X, double Y) Drain, (double X, double Y)? Source)
        PlaceDut(Ctx ctx, DutSpec dut)
    {
        switch (dut.Kind)
        {
            case DutKind.NativeFet:
            {
                var comp = AddComponent(ctx, FetKindFor(dut.TypeName), 0, 0, SymbolRotation.R0, "DUT");
                CopyParameters(comp, dut);
                // g=port0 (LEFT), d=port1 (TOP), s=port2 (BOTTOM) — SymbolPortDefs.For's own layout.
                for (int i = 0; i < comp.PortCount; i++)
                    ctx.AvoidPoints.Add(comp.GetPortWorldCoord(i));
                return (comp.GetPortWorldCoord(0), comp.GetPortWorldCoord(1), comp.GetPortWorldCoord(2));
            }

            case DutKind.Diode:
            {
                // R270 puts the anode (port0) on the LEFT and the cathode (port1) on the RIGHT —
                // gate-side/drain-side, matching HarmonicaNetlist.DutLine's "Diode:{Dut} {gate} {drain}".
                var comp = AddComponent(ctx, SymbolKind.Diode, 0, 0, SymbolRotation.R270, "DUT");
                CopyParameters(comp, dut);
                for (int i = 0; i < comp.PortCount; i++)
                    ctx.AvoidPoints.Add(comp.GetPortWorldCoord(i));
                return (comp.GetPortWorldCoord(0), comp.GetPortWorldCoord(1), null);
            }

            case DutKind.Sdd:
            {
                // R-h9c-11 — SDD2 or SDD3. Port pairs, index 2p="+"/2p+1="−": port0=(gate,source),
                // port1=(drain,source), port2=(source,ground) when 3-port — the SAME convention
                // HarmonicaNetlist.DutLine writes as text.
                int n = dut.SddPortCount == 3 ? 3 : 2;
                var comp = AddComponent(ctx, SymbolKind.Sdd, 0, 0, SymbolRotation.R0, "DUT");
                comp.Parameters.Add(new EditableParameter
                    { Name = "NumPorts", Expression = n.ToString(CultureInfo.InvariantCulture) });
                CopyParameters(comp, dut);

                // PortCount reports the LOGICAL port count (n) for Sdd; the PHYSICAL pin count is 2n
                // (one "+"/"−" pair per port) — every physical pin, not just the first n, must be
                // registered so a straight wire elsewhere in this file can never run through one.
                for (int i = 0; i < 2 * n; i++)
                    ctx.AvoidPoints.Add(comp.GetPortWorldCoord(i));

                var gate     = comp.GetPortWorldCoord(0);
                var gateNeg  = comp.GetPortWorldCoord(1);
                var drain    = comp.GetPortWorldCoord(2);
                var drainNeg = comp.GetPortWorldCoord(3);

                // Every port's "−" (and, for SDD3, port2's "+") shares ONE source node — the same
                // merge HarmonicaNetlist expresses by repeating the "source" net across port pairs.
                var sourceJunction = Offset((0, 0), Direction.Down, Pitch);
                ConnectOrthogonal(ctx, gateNeg, sourceJunction);
                ConnectOrthogonal(ctx, drainNeg, sourceJunction);

                if (n == 3)
                {
                    var sourcePlus = comp.GetPortWorldCoord(4);
                    var groundPin  = comp.GetPortWorldCoord(5);
                    ConnectOrthogonal(ctx, sourcePlus, sourceJunction);
                    // Port2's "−" is literal ground in the .cnl ("{source} 0"), never through Rs/Ls —
                    // a separate ground reference from the one the package's source lead reaches.
                    TieToGround(ctx, groundPin);
                }

                return (gate, drain, sourceJunction);
            }

            default:
                throw new NotSupportedException(
                    $"Export Testbench (.csch) cannot express DUT kind '{dut.Kind}' as a schematic component.");
        }
    }

    private static void CopyParameters(EditableComponent comp, DutSpec dut)
    {
        foreach (var (k, v) in dut.Parameters)
            comp.Parameters.Add(new EditableParameter { Name = k, Expression = v });

        // The multiplier goes on every DUT kind that accepts one — matches HarmonicaNetlist.DutLine's
        // own "m= before the equations" rule (an SDD line's multiplier must come before its equations,
        // per src/Harmonica/CLAUDE.md — irrelevant to parameter ORDER here since these are named
        // key/value pairs, not positional text, but the VALUE is the same one either export writes).
        if (dut.Multiplicity != 1.0)
            comp.Parameters.Add(new EditableParameter { Name = "m", Expression = Num(dut.Multiplicity) });
    }

    private static SymbolKind FetKindFor(string typeName) => typeName switch
    {
        "FET_Angelov"      => SymbolKind.FetAngelov,
        "FET_Curtice"      => SymbolKind.FetCurtice,
        "FET_CurticeCubic" => SymbolKind.FetCurticeCubic,
        "FET_Materka"      => SymbolKind.FetMaterka,
        "FET_Statz"        => SymbolKind.FetStatz,
        _ => throw new NotSupportedException(
            $"Export Testbench (.csch) does not recognise native FET law '{typeName}'."),
    };

    // ── the embedding (lumped package only — Touchstone is refused above) ──────

    /// <summary>Places R then L in series, outward from <paramref name="from"/>. Mirrors
    /// <c>HarmonicaNetlist.Series</c>'s own "nothing emitted, node unchanged" rule when both are
    /// zero.</summary>
    private static (double X, double Y) PlaceSeriesRl(Ctx ctx, (double X, double Y) from, Direction dir,
                                                       string rName, string lName, double r, double l)
    {
        var point = from;
        if (r != 0) point = PlaceTwoPin(ctx, point, dir, rName, SymbolKind.Resistor, "R", r);
        if (l != 0) point = PlaceTwoPin(ctx, point, dir, lName, SymbolKind.Inductor, "L", l);
        return point;
    }

    /// <summary>A shunt capacitance to ground, tapped at <paramref name="at"/>. The node identity is
    /// UNCHANGED — a shunt does not advance the chain, matching <c>HarmonicaNetlist.Shunt</c>.</summary>
    private static (double X, double Y) PlaceShunt(Ctx ctx, (double X, double Y) at, Direction dir,
                                                    string name, double c)
    {
        if (c == 0) return at;
        var pins = PlaceTwoPinComponent(ctx, at, dir, name, SymbolKind.Capacitor, "C", c);
        TieToGround(ctx, pins.Far);
        return at;
    }

    /// <summary>The gate-drain feedback capacitance, bridging the DUT's own two terminals directly —
    /// never through the series leads, matching HarmonicaNetlist's own <c>CGDX</c>.</summary>
    private static void PlaceBridge(Ctx ctx, (double X, double Y) a, (double X, double Y) b, string name, double c)
    {
        if (c == 0) return;
        var mid = (SnapToGrid((a.X + b.X) / 2), SnapToGrid((a.Y + b.Y) / 2));
        var comp = AddComponent(ctx, SymbolKind.Capacitor, mid.Item1, mid.Item2, SymbolRotation.R0, name);
        comp.Parameters.Add(new EditableParameter { Name = "C", Expression = Num(c) });
        ConnectOrthogonal(ctx, a, comp.GetPortWorldCoord(0));
        ConnectOrthogonal(ctx, b, comp.GetPortWorldCoord(1));
    }

    // ── bias and terminations (mirrors HarmonicaNetlist / HarmonicaInterchange exactly) ────────

    /// <summary>The ideal choke and its DC supply, off the termination plane. <c>Vdc</c>'s OTHER
    /// terminal is ground, matching <c>Vdc:{name} {biasNode} 0</c>.</summary>
    private static void PlaceBias(Ctx ctx, (double X, double Y) outer, Direction dir,
                                  string chokeName, string vdcName, double vdcVolts, double chokeH)
    {
        var biasNode = PlaceTwoPin(ctx, outer, dir, chokeName, SymbolKind.Inductor, "L", chokeH);
        PlaceTerminationTail(ctx, biasNode, dir, vdcName, SymbolKind.Vdc, vdc =>
            vdc.Parameters.Add(new EditableParameter { Name = "Vdc", Expression = Num(vdcVolts) }));
    }

    /// <summary>R-h9c-15's source termination: the DC block, then a <c>P1Tone</c> at the frame's own
    /// available power — never a Tuner (see this file's own top-of-file note).</summary>
    private static void PlaceSourceTermination(Ctx ctx, (double X, double Y) outer, Direction dir,
                                               CircuitModel model, TerminationSet t, double pavlDbm)
    {
        var afterBlock = PlaceTwoPin(ctx, outer, dir, "CBLKS", SymbolKind.Capacitor, "C",
                                     model.Settings.DcBlockFarads);

        PlaceTerminationTail(ctx, afterBlock, dir, "PIN", SymbolKind.P1Tone, pin =>
        {
            pin.Parameters.Add(new EditableParameter { Name = "Num",  Expression = "1" });
            pin.Parameters.Add(new EditableParameter { Name = "Pavl", Expression = Num(pavlDbm) });
            pin.Parameters.Add(new EditableParameter { Name = "Z",    Expression = Num(TerminationSet.UnmarkedBandOhms) });
            pin.Parameters.Add(new EditableParameter { Name = "Freq", Expression = Num(model.Settings.FrequencyHz) });
            AppendBandParams(pin, t, TerminationSide.Source);
        });
    }

    /// <summary>R-h9c-15's load termination: the DC block, then a <c>PnTone</c> declaring NO tones —
    /// a per-harmonic passive termination, never a Tuner.</summary>
    private static void PlaceLoadTermination(Ctx ctx, (double X, double Y) outer, Direction dir,
                                             CircuitModel model, TerminationSet t)
    {
        var afterBlock = PlaceTwoPin(ctx, outer, dir, "CBLKL", SymbolKind.Capacitor, "C",
                                     model.Settings.DcBlockFarads);

        PlaceTerminationTail(ctx, afterBlock, dir, "PLOAD", SymbolKind.PnTone, load =>
        {
            // Deliberately ONLY Z/Z[k] — no Freq[i]/Pavl[i]/Phase[i]. PnTone's own placement default
            // seeds two tones; this export builds its Parameters from scratch and never calls that
            // path, so "no tones declared" is a fact about what is written, not a default relied on.
            load.Parameters.Add(new EditableParameter { Name = "Z", Expression = Num(TerminationSet.UnmarkedBandOhms) });
            AppendBandParams(load, t, TerminationSide.Load);
        });
    }

    /// <summary>The second half of every termination plane — a Vdc/P1Tone/PnTone placed
    /// <see cref="Pitch"/> further past the choke/cap already placed at <paramref name="from"/>,
    /// wired to it, and its own far pin tied to ground. Grows its own placement pitch exactly like
    /// <see cref="PlaceTwoPinComponent"/> does for the choke/cap half — a FIXED one-step offset here
    /// has no collision avoidance at all, and two termination planes anchored close together (as
    /// gate's and drain's both are for a 3-port SDD, since two of its three ports share one column)
    /// can easily place one plane's own Vdc/PnTone pin exactly on the OTHER plane's own pin (found by
    /// tracing a real n=3 export: PLOAD's own near pin landed precisely on LCHG's own far pin).</summary>
    private static void PlaceTerminationTail(Ctx ctx, (double X, double Y) from, Direction dir,
                                             string instanceName, SymbolKind kind,
                                             Action<EditableComponent> configure)
    {
        double pitch = Pitch;
        (double X, double Y) pos, near, far;
        while (true)
        {
            pos = Offset(from, dir, pitch);
            var pin0 = (pos.X, pos.Y - 200);
            var pin1 = (pos.X, pos.Y + 200);
            (near, far) = NearFarPins(pin0, pin1, dir);
            if (!IsObstructed(ctx, near) && !IsObstructed(ctx, far)) break;
            pitch += 200;
        }

        var comp = AddComponent(ctx, kind, pos.X, pos.Y, SymbolRotation.R0, instanceName);
        configure(comp);

        ConnectOrthogonal(ctx, from, near);
        TieToGround(ctx, far);
    }

    private static void AppendBandParams(EditableComponent comp, TerminationSet t, TerminationSide side)
    {
        foreach (int band in t.MarkedBands(side).OrderBy(b => b))
        {
            var z = t.Z(side, band);
            comp.Parameters.Add(new EditableParameter
            {
                Name = $"Z[{band}]",
                Expression = z.Imaginary == 0 ? Num(z.Real) : $"complex({Num(z.Real)},{Num(z.Imaginary)})",
            });
        }
    }

    // ── the analysis (R-h9c-15 — "configured the same way as harmonicaRF") ─────

    private static HarmonicBalanceAnalysis BuildAnalysis(CircuitModel model) => new("HB1")
    {
        ToneExpr          = Num(model.Settings.FrequencyHz),
        ToneUnit          = "Hz",
        NumFreqsExpr       = "1",
        MaxHarmonicExpr    = model.Settings.HarmonicCount.ToString(CultureInfo.InvariantCulture),
        TolExpr            = Num(model.Settings.Tol),
        MaxIterExpr        = model.Settings.MaxIter.ToString(CultureInfo.InvariantCulture),
        GuardHarmonicExpr  = model.Settings.GuardHarmonic.ToString(CultureInfo.InvariantCulture),
        LambdaExpr         = Num(model.Settings.Lambda),
        FFTOverSampleExpr  = model.Settings.FftOverSample.ToString(CultureInfo.InvariantCulture),
    };

    // ── placement/wiring primitives — every coordinate on-grid by construction (R7) ────────────

    private static (double X, double Y) Offset((double X, double Y) p, Direction dir, double amount) => dir switch
    {
        Direction.Left  => (p.X - amount, p.Y),
        Direction.Right => (p.X + amount, p.Y),
        Direction.Up    => (p.X, p.Y - amount),
        _               => (p.X, p.Y + amount),   // Down
    };

    private static double SnapToGrid(double v) => Math.Round(v / 100.0) * 100.0;

    private static EditableComponent AddComponent(Ctx ctx, SymbolKind kind, double x, double y,
                                                   SymbolRotation rotation, string instanceName)
    {
        var c = new EditableComponent
        {
            Symbol       = kind,
            X            = SnapToGrid(x),
            Y            = SnapToGrid(y),
            Rotation     = rotation,
            InstanceName = instanceName,
        };
        ctx.Model.Components.Add(c);

        // Every placed component's own physical pins are permanent obstacles from this point on —
        // not just the DUT's. Without this, a component placed LATER (e.g. LCHD) has no way to keep
        // an EARLIER route's own staircase waypoints from coincidentally landing exactly on one of
        // ITS pins once it exists — found by tracing a real n=3 export: the drain-to-LCHD route's own
        // safe-looking waypoint landed exactly on LCHD's own far pin, shorting the inductor to the
        // node it was supposed to be in series with. Registering here, at construction, is what makes
        // that impossible for every future route regardless of which function placed which component.
        // (For a variadic SDD this only sees `PortCount`'s value AT THIS MOMENT — before its NumPorts
        // parameter is set by the caller — so PlaceDut's own explicit 2*n loop, run once NumPorts and
        // the true physical pin count are known, is still what registers this kind of DUT correctly;
        // this generic pass is redundant-but-harmless for that one case, not a replacement for it.)
        for (int i = 0; i < c.PortCount; i++)
            ctx.AvoidPoints.Add(c.GetPortWorldCoord(i));

        return c;
    }

    /// <summary>Places a 2-pin component <see cref="Pitch"/> further in <paramref name="dir"/>, wires
    /// <paramref name="from"/> to its near pin, and returns its far pin — the general series-element
    /// step every R/L/C placement in this file reduces to.</summary>
    private static (double X, double Y) PlaceTwoPin(Ctx ctx, (double X, double Y) from, Direction dir,
                                                     string instanceName, SymbolKind kind,
                                                     string paramName, double value)
        => PlaceTwoPinComponent(ctx, from, dir, instanceName, kind, paramName, value).Far;

    /// <summary>Every 2-pin component here is placed at <see cref="SymbolRotation.R0"/> — pin0 is
    /// always its physically TOP (smaller-Y) terminal, pin1 always its BOTTOM (larger-Y) terminal,
    /// regardless of which <paramref name="dir"/> was used to compute its center. Which one is
    /// actually nearer <paramref name="from"/> therefore depends on direction: for every direction
    /// except Up, the component's center sits at or below <paramref name="from"/>'s own Y, so pin0
    /// (the smaller-Y/top one) is nearer. For Up, the center sits ABOVE <paramref name="from"/>
    /// (smaller Y than "from"), so pin1 (the larger-Y/bottom one) is the near pin instead — treating
    /// pin0 as "near" there wires the FAR pin to the anchor and hands the caller the NEAR pin as the
    /// supposed continuation point, which is what let a downstream ground land back on an
    /// already-placed pin (found by tracing a real export's coordinates against
    /// <c>NetExtractor</c>'s output).</summary>
    private static ((double X, double Y) Near, (double X, double Y) Far) NearFarPins(
        (double X, double Y) pin0, (double X, double Y) pin1, Direction dir)
        => dir == Direction.Up ? (pin1, pin0) : (pin0, pin1);

    private static ((double X, double Y) Near, (double X, double Y) Far) PlaceTwoPinComponent(
        Ctx ctx, (double X, double Y) from, Direction dir, string instanceName, SymbolKind kind,
        string paramName, double value)
    {
        // A 2-pin component's own leads always sit exactly 200 units above/below its center at R0
        // (see NearFarPins' own doc comment), so the near/far pins a candidate placement WOULD
        // produce are computable before any component is actually created. That is what lets a
        // placement whose pin would land EXACTLY on a DUT pin be discarded and retried further out,
        // rather than only discovered afterward — a fixed Pitch is exactly a multiple of the DUT's
        // own internal pin spacing (200 within one differential pair, 400 between consecutive ports
        // on a 3-port SDD) purely by arithmetic coincidence, and clearing one modulus does not clear
        // the other (found by tracing a real n=3 export's coordinates against NetExtractor's output).
        double pitch = Pitch;
        (double X, double Y) pos, near, far;
        while (true)
        {
            pos = Offset(from, dir, pitch);
            var pin0 = (pos.X, pos.Y - 200);
            var pin1 = (pos.X, pos.Y + 200);
            (near, far) = NearFarPins(pin0, pin1, dir);
            if (!IsObstructed(ctx, near) && !IsObstructed(ctx, far)) break;
            pitch += 200;
        }

        var comp = AddComponent(ctx, kind, pos.X, pos.Y, SymbolRotation.R0, instanceName);
        comp.Parameters.Add(new EditableParameter { Name = paramName, Expression = Num(value) });

        ConnectOrthogonal(ctx, from, near);
        return (near, far);
    }

    /// <summary>True when <paramref name="p"/> exactly coincides with one of <see
    /// cref="Ctx.AvoidPoints"/> — the "new component's own pin lands on an existing DUT pin" half of
    /// the coincidence hazard; <see cref="SegmentCrossesAvoidPoint"/> is the "a wire merely passes
    /// one on its way elsewhere" half.</summary>
    private static bool CoincidesWithAvoidPoint(Ctx ctx, (double X, double Y) p)
    {
        const double eps = 1e-6;
        foreach (var a in ctx.AvoidPoints)
            if (Math.Abs(a.X - p.X) < eps && Math.Abs(a.Y - p.Y) < eps) return true;
        return false;
    }

    /// <summary>True when <paramref name="p"/> lands on the open INTERIOR of an already-drawn wire —
    /// the half of the coincidence hazard <see cref="CoincidesWithAvoidPoint"/> cannot see, because
    /// <see cref="Ctx.AvoidPoints"/> only ever records discrete PIN positions, never the full run of
    /// a wire between two of them. A retry loop that only clears every registered pin can still walk
    /// a brand-new component's own pin straight onto the middle of an unrelated wire (found by
    /// tracing a real n=3 export: PLOAD's own near pin, placed by <see cref="PlaceTerminationTail"/>
    /// specifically to clear every pin in <c>AvoidPoints</c>, still landed squarely on the interior
    /// of the DUT-to-LCHG wire — a point that coincides with no PIN at all, so the existing check
    /// found nothing wrong, while NetExtractor's own §5.1 rule unions it with that wire regardless).
    /// Uses <see cref="SchematicEditModel.ConnectTolerance"/>, the same tolerance NetExtractor's own
    /// T-junction union already uses, so "would this coincide" here means exactly what it means
    /// there.</summary>
    private static bool CoincidesWithWireInterior(Ctx ctx, (double X, double Y) p)
    {
        foreach (var wire in ctx.Model.Wires)
        {
            var pts = wire.Points;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                if (SchematicGeometry.PointOnSegmentInterior(p.X, p.Y,
                        pts[i].X, pts[i].Y, pts[i + 1].X, pts[i + 1].Y,
                        SchematicEditModel.ConnectTolerance))
                    return true;
            }
        }
        return false;
    }

    /// <summary>The full obstruction test a placement retry loop must clear: coinciding with a
    /// registered pin, OR landing on the interior of an already-drawn wire. Always both together —
    /// see <see cref="CoincidesWithWireInterior"/>'s own doc comment for why checking only the first
    /// is not enough.</summary>
    private static bool IsObstructed(Ctx ctx, (double X, double Y) p)
        => CoincidesWithAvoidPoint(ctx, p) || CoincidesWithWireInterior(ctx, p);

    /// <summary>A new <c>Ground</c> component near <paramref name="at"/>, wired to it. Multiple
    /// separate <c>Ground</c> symbols are ordinary — every one names the SAME net ("0") by
    /// definition, with no wire needed between two of them.
    ///
    /// <para>The ground's own placement point grows (200 units at a time, straight down) until it
    /// clears every registered pin — a fixed one-step offset can land it EXACTLY on some unrelated
    /// component's own pin, coincidentally, and a Ground's single pin coinciding with another
    /// connection point unions them just as surely as a wire vertex does (found by tracing a real
    /// n=3 export: one termination plane's own ground tie landed precisely on the OTHER termination
    /// plane's signal pin, silently grounding a node that was never meant to be grounded).</para>
    /// </summary>
    private static void TieToGround(Ctx ctx, (double X, double Y) at)
    {
        double offset = 200;
        (double X, double Y) pos;
        while (true)
        {
            pos = Offset(at, Direction.Down, offset);
            if (!IsObstructed(ctx, pos)) break;
            offset += 200;
        }

        var g = AddComponent(ctx, SymbolKind.Ground, pos.X, pos.Y, SymbolRotation.R0,
                             $"GND{++ctx.GroundCount}");
        ConnectOrthogonal(ctx, at, g.GetPortWorldCoord(0));
    }

    /// <summary>Wires two points with an orthogonal (Manhattan) route — a straight segment when they
    /// already share an axis, an L-bend otherwise. Every input here is already on-grid, so the bend
    /// point (sharing one coordinate from each end) is too.
    ///
    /// <para>A straight segment is refused, and rerouted via a sideways jog, whenever it would run
    /// COLLINEARLY THROUGH one of <see cref="Ctx.AvoidPoints"/> — see that field's own doc comment for
    /// why a wire merely passing a DUT pin silently shorts to it (NetExtractor's own §5.1 rule).</para>
    /// </summary>
    private static void ConnectOrthogonal(Ctx ctx, (double X, double Y) from, (double X, double Y) to)
    {
        if (Math.Abs(from.X - to.X) < 1e-6 && Math.Abs(from.Y - to.Y) < 1e-6) return;

        if (Math.Abs(from.X - to.X) < 1e-6 || Math.Abs(from.Y - to.Y) < 1e-6)
        {
            ConnectStraightSafely(ctx, from, to);
            return;
        }

        // The L-bend's own two legs are each axis-aligned runs in their own right — and each is just
        // as capable of running collinearly through a DUT pin as the single-segment case above (found
        // by tracing a real n=3 export: the drain-side bias tap's FIRST leg ran straight across the
        // SDD's own source-minus pin on its way to the bend point). Route both through the same
        // avoid-and-jog logic, never `AddWire` directly.
        var bend = (to.X, from.Y);
        ConnectStraightSafely(ctx, from, bend);
        ConnectStraightSafely(ctx, bend, to);
    }

    /// <summary>Draws one axis-aligned run from <paramref name="a"/> to <paramref name="b"/> (they
    /// must already share an axis), rerouting via a sideways jog whenever the direct run would
    /// coincide with a DUT pin it was never meant to touch — see <see cref="Ctx.AvoidPoints"/>'s own
    /// doc comment for why a wire merely passing a pin silently shorts to it.</summary>
    private static void ConnectStraightSafely(Ctx ctx, (double X, double Y) a, (double X, double Y) b)
    {
        if (Math.Abs(a.X - b.X) < 1e-6 && Math.Abs(a.Y - b.Y) < 1e-6) return;

        if (!SegmentCrossesAvoidPoint(ctx, a, b))
        {
            AddWire(ctx, a, b);
            return;
        }

        // The direct run would coincide with a DUT pin it was never meant to touch. A dip anchored
        // directly at `a`'s own axis value is NOT enough to fix this — `a` is routinely a DUT pin
        // itself, sitting on a column densely packed with its OWN siblings 200 units apart (an SDD's
        // "+"/"−" pair, or — for a 3-port device whose ports don't all land on opposite sides — a
        // second port's whole pair too), so a dip that stays on that column can cross a sibling
        // regardless of how far it goes (found the same way as everything else here: a real n=3
        // export's own dip landed exactly on drain-minus while trying to route AROUND source-minus).
        // The fix is a four-leg "staircase": step a SHORT, fixed distance off `a`'s own column FIRST
        // — 100 units, an odd multiple of the 200-unit spacing every pin coordinate in this file is
        // built from, so it can never coincide with one — dip there instead, then rejoin at `b`.
        bool vertical = Math.Abs(a.X - b.X) < 1e-6;
        double toward = vertical ? Math.Sign(b.Y - a.Y) : Math.Sign(b.X - a.X);
        if (toward == 0) toward = 1;

        foreach (double escape in EscapeCandidatesDbu)
        foreach (double jog in JogCandidatesDbu)
        {
            (double X, double Y) escapePt, dipPt, alignPt;
            if (vertical)
            {
                // Escape ALONG the segment's own axis (Y) first — off `a`'s row, not off its
                // column, since the column (a.X == b.X) is exactly what must stay untouched until
                // the dip has already moved away from it.
                escapePt = (a.X, a.Y + escape * toward);
                dipPt    = (a.X + jog, a.Y + escape * toward);
                alignPt  = (a.X + jog, b.Y);
            }
            else
            {
                escapePt = (a.X + escape * toward, a.Y);
                dipPt    = (a.X + escape * toward, a.Y + jog);
                alignPt  = (b.X, a.Y + jog);
            }

            // a -> escapePt (off the shared column/row) -> dipPt (perpendicular offset, now safe)
            // -> alignPt (back onto b's own row/column) -> b (perpendicular return).
            if (!TryStaircase(ctx, a, escapePt, dipPt, alignPt, b)) continue;
            return;
        }

        // No candidate cleared every avoid point (should not happen with this file's own fixed
        // layout, but a silently-dropped connection would be worse than a direct run that
        // NetExtractor can at least report a conflict on) — fall back to the direct wire.
        AddWire(ctx, a, b);
    }

    /// <summary>Attempts one 5-point staircase route (<paramref name="a"/> → … → <paramref
    /// name="b"/>); commits and returns true only if every leg and every introduced midpoint is
    /// clear of <see cref="Ctx.AvoidPoints"/> — a midpoint landing EXACTLY on one is rejected just
    /// like a collinear crossing, since (unlike <paramref name="a"/>/<paramref name="b"/>, which are
    /// always some real pin the caller meant to reach) it is purely an artifact of which candidate
    /// happened to be tried.</summary>
    private static bool TryStaircase(
        Ctx ctx, (double X, double Y) a, (double X, double Y) p1, (double X, double Y) p2,
        (double X, double Y) p3, (double X, double Y) b)
    {
        foreach (var mid in new[] { p1, p2, p3 })
            if (CoincidesWithAvoidPoint(ctx, mid)) return false;
        if (SegmentCrossesAvoidPoint(ctx, a, p1)) return false;
        if (SegmentCrossesAvoidPoint(ctx, p1, p2)) return false;
        if (SegmentCrossesAvoidPoint(ctx, p2, p3)) return false;
        if (SegmentCrossesAvoidPoint(ctx, p3, b)) return false;

        AddWire(ctx, a, p1);
        AddWire(ctx, p1, p2);
        AddWire(ctx, p2, p3);
        AddWire(ctx, p3, b);
        return true;
    }

    /// <summary>How far the staircase steps off its starting point's own column/row before dipping —
    /// small and, critically, an ODD multiple of 100: every pin coordinate this file ever computes is
    /// built from 200-unit steps (a lead half-length) from some origin, so an odd-hundred offset can
    /// never land exactly on one, regardless of which origin it was measured from.</summary>
    private static readonly double[] EscapeCandidatesDbu = { 100, 300, 500, 700 };

    /// <summary>Sideways offsets tried, in order, for the dip itself — both signs, growing outward by
    /// a half lead-length (100) at a time, not a whole one (200). A whole-lead-length step only ever
    /// lands the dip on one of two lattices (the pin lattice itself, or the lattice exactly between
    /// consecutive pins); once <see cref="Ctx.AvoidPoints"/> holds every placed component's own pins
    /// — not just the DUT's — the dip most likely to actually clear everything is routinely the ONE
    /// that lands squarely between two of them, which a 200-only step can skip over entirely (found by
    /// tracing a real n=3 export's own drain-to-choke route: every 200-step candidate crossed one
    /// sibling pin or another, while the intervening 100-step candidate cleared all of them).</summary>
    private static readonly double[] JogCandidatesDbu =
        { 0, 100, -100, 200, -200, 300, -300, 400, -400, 500, -500, 600, -600, 700, -700, 800, -800 };

    /// <summary>True when any of <see cref="Ctx.AvoidPoints"/> lies collinear with, and strictly
    /// between, <paramref name="a"/> and <paramref name="b"/> — <paramref name="a"/>/<paramref
    /// name="b"/> must already share an axis (this file never calls it on a diagonal pair).</summary>
    private static bool SegmentCrossesAvoidPoint(Ctx ctx, (double X, double Y) a, (double X, double Y) b)
    {
        foreach (var p in ctx.AvoidPoints)
            if (IsStrictlyBetweenOnSharedAxis(a, b, p)) return true;
        return false;
    }

    private static bool IsStrictlyBetweenOnSharedAxis(
        (double X, double Y) a, (double X, double Y) b, (double X, double Y) p)
    {
        const double eps = 1e-6;
        if (Math.Abs(a.X - b.X) < eps && Math.Abs(p.X - a.X) < eps)
        {
            double lo = Math.Min(a.Y, b.Y), hi = Math.Max(a.Y, b.Y);
            return p.Y > lo + eps && p.Y < hi - eps;
        }
        if (Math.Abs(a.Y - b.Y) < eps && Math.Abs(p.Y - a.Y) < eps)
        {
            double lo = Math.Min(a.X, b.X), hi = Math.Max(a.X, b.X);
            return p.X > lo + eps && p.X < hi - eps;
        }
        return false;
    }

    private static void AddWire(Ctx ctx, (double X, double Y) a, (double X, double Y) b)
    {
        var w = new EditableWire();
        w.Points.Add((SnapToGrid(a.X), SnapToGrid(a.Y)));
        w.Points.Add((SnapToGrid(b.X), SnapToGrid(b.Y)));
        ctx.Model.Wires.Add(w);
    }

    private static string Num(double v) => v.ToString("G17", CultureInfo.InvariantCulture);
}
