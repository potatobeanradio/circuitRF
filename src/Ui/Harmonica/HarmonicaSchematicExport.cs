// ================================================================
//  HarmonicaSchematicExport.cs  —  §7 of brief-harmonicarf-r1c-chrome-readouts-dut-and-export
//
//  R-h9c-15  Export Testbench writes a runnable .csch: symbols placed, wires on the connection
//            grid, the same bias, the same terminations and an analysis matching harmonicaRF's own.
//
//  ROUND 10 (owner, 2026-08-15) reworked most of what this file draws. The durable rules:
//
//   • THE SOURCE IS A P1Tone, THE LOAD IS A LoadTuner NAMED "Load" (owner's own instruction).
//     The load used to be a PnTone declaring no tones, for a measured reason that has NOT gone away
//     and is recorded here rather than deleted: under a plain `type=hb` run nothing calls
//     TunerModel.SetTone, so TunerModel.GetZ takes its "S-param mode" branch and returns the
//     DECLARED Z[1] AT EVERY HARMONIC — the per-band Z[2]/Z[3] written below are inert unless the
//     tone is set. See `src/Ui/RESOLVED.md`'s Round 10 entry; this is the one place the exported
//     schematic can still disagree with harmonicaRF.
//
//   • EVERY GROUND SITS EXACTLY ON THE PIN IT GROUNDS — no wire, no offset (owner: "This cleans up
//     the schematic rendering"). A Ground's single pin is at its own origin, so placing one at a pin
//     coordinate unions the two through NetExtractor's ordinary coincidence rule.
//
//   • A Vdc's "+" IS ITS TOP PIN (pin 0) AND ITS "−" IS THE BOTTOM ONE. This file used to ground the
//     TOP pin and feed the choke from the bottom, which put −Vgs on the gate and −Vds on the drain —
//     a real, silent sign inversion in every schematic this exporter has ever written. The bias
//     supplies are now placed sideways (owner §12) with the "+" pin routed to the choke and the "−"
//     pin grounded in place.
//
//   • A SERIES ELEMENT IS ORIENTED ALONG ITS OWN RUN. A left/right run places its R/L/C at R90
//     (pins east/west), an up/down run at R0 (pins north/south), so a chain never needs an L-bend
//     into a component that is lying across it.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
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

    /// <summary>Half the lead length of every 2-pin primitive at R0 (see <c>SymbolPortDefs.For</c>'s
    /// own default case: pins at local <c>(0, ±200)</c>).</summary>
    private const double Lead = 200.0;

    /// <summary>The global a <c>VAR</c> declares and the parametric sweep steps — the available
    /// power the source presents (owner §4: "Pin needs to be a VAR").</summary>
    public const string PinVariable = "Pin";

    /// <summary>The instance name the load termination carries (owner §8).</summary>
    public const string LoadTunerInstanceName = "Load";

    /// <summary>
    /// The four current probes and the four net labels the PA measurement block reads. Public and
    /// named because they are a CONTRACT, not decoration: every equation in <see cref="Measurements"/>
    /// spells one of these strings, and a rename that touched only one side would leave a schematic
    /// whose measurements silently fail to resolve.
    /// </summary>
    public const string InputProbe    = "Iin";
    public const string OutputProbe   = "Iout";
    public const string DrainDcProbe  = "IDC";
    public const string GateDcProbe   = "Igate";
    public const string InputNet      = "Vin";
    public const string OutputNet     = "Vout";
    public const string DrainBiasNet  = "VDD";
    public const string GateBiasNet   = "VGG";

    private enum Direction { Left, Right, Up, Down }

    private static bool IsHorizontal(Direction d) => d is Direction.Left or Direction.Right;

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

        /// <summary>Points a Ground was deliberately dropped onto (owner §5/§6). A later placement
        /// must still avoid them — but the ground itself is not an obstruction to the pin it was put
        /// there to ground, which is why they are recorded separately from being merely "another
        /// pin".</summary>
        public readonly HashSet<(double, double)> GroundedPoints = new();

        /// <summary>Every wire VERTEX drawn so far. <see cref="CoincidesWithWireInterior"/> cannot see
        /// these — <c>PointOnSegmentInterior</c> excludes endpoints by definition — and a new
        /// component pin landing exactly on an existing wire's corner shorts to it just as surely as
        /// one landing mid-run. Found on the 3-port SDD export, whose gate and drain sit on the SAME
        /// column so both bias chains route up it: the drain choke's own near pin landed precisely on
        /// the corner of the GATE supply's route, tying VGG and VDD together through their chokes —
        /// a singular MNA, reported by the engine rather than by anything in the drawing.</summary>
        public readonly HashSet<(double, double)> WireVertices = new();
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
        var pkg = model.Embedding.Package;

        // Whether the DUT's own source terminal IS ground, or is lifted off it by a source lead. When
        // it is ground, every "−" terminal gets its own Ground symbol sitting on the pin (owner §5:
        // "do not have long wires... don't share a ground using a wire") instead of a shared junction.
        bool sourceIsGround = pkg.Rs == 0 && pkg.Ls == 0;

        var (gate, drain, source) = PlaceDut(ctx, model.Dut, sourceIsGround);

        // Outward from the DUT's own terminals: series lead, then the shunt cap AT that same plane
        // (a shunt never advances the node — HarmonicaNetlist.Shunt's own rule).
        var gateOuter = PlaceSeriesRl(ctx, gate, Direction.Left, "RG", "LG", pkg.Rg, pkg.Lg);
        gateOuter     = PlaceShunt(ctx, gateOuter, Direction.Down, "CPG", pkg.Cpg);

        // The drain chain runs RIGHT (owner §10: the load sits to the right of the DUT, on the
        // drain's own row, so its connecting wire is a single horizontal run with no bends).
        var drainOuter = PlaceSeriesRl(ctx, drain, Direction.Right, "RD", "LD", pkg.Rd, pkg.Ld);
        drainOuter     = PlaceShunt(ctx, drainOuter, Direction.Down, "CPD", pkg.Cpd);

        if (pkg.CgdExt != 0)
            PlaceBridge(ctx, gate, drain, "CGDX", pkg.CgdExt);

        // The source lead — only when the DUT actually has a source terminal (a Diode has none; its
        // "source" parameter would be a dangling branch nothing references, so it is skipped rather
        // than drawn pointing at nothing).
        if (source is { } src)
            GroundAt(ctx, PlaceSeriesRl(ctx, src, Direction.Down, "RS", "LS", pkg.Rs, pkg.Ls));

        // The two termination planes — each a THREE-way junction (bias choke, DC-blocked
        // termination, and the embedding chain already wired above), exactly as
        // HarmonicaNetlist/HarmonicaInterchange's own SourcePlane/LoadPlane are.
        //
        // Both bias taps run UP (owner §11: the drain bias mirrors the gate's), and the supply itself
        // is placed to the OUTSIDE (gate supply left, drain supply right) so its wire leaves the pin
        // sideways before turning down — never straight through the Vdc symbol (owner §12).
        PlaceBias(ctx, gateOuter,  "LCHG", "VGG", model.Bias.Vgs ?? 0.0, model.Settings.BiasChokeHenries,
                  toward: -1, probeName: GateDcProbe,  supplyNet: GateBiasNet);
        PlaceBias(ctx, drainOuter, "LCHD", "VDD", model.Bias.Vds,        model.Settings.BiasChokeHenries,
                  toward: +1, probeName: DrainDcProbe, supplyNet: DrainBiasNet);

        PlaceSourceTermination(ctx, gateOuter, model, terminations);
        PlaceLoadTermination(ctx, drainOuter, model, terminations);

        PlacePinVariable(ctx, pavlDbm);
        PlaceMeasurements(ctx);

        var hb = BuildAnalysis(model);
        ctx.Model.Analyses.Add(hb);
        if (BuildPinSweep(model, hb.Name) is { } sweep)
            ctx.Model.Analyses.Add(sweep);

        return ctx.Model;
    }

    // ── the DUT ──────────────────────────────────────────────────────────────

    /// <summary>Places the DUT at the origin and returns its GATE/DRAIN connection points and its
    /// SOURCE point (null for a Diode, which has none, and null for an SDD whose source terminal is
    /// literally ground — that case is grounded pin-by-pin here rather than through a junction the
    /// caller would have to tie).</summary>
    private static ((double X, double Y) Gate, (double X, double Y) Drain, (double X, double Y)? Source)
        PlaceDut(Ctx ctx, DutSpec dut, bool sourceIsGround)
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

                var src = comp.GetPortWorldCoord(2);
                if (sourceIsGround) { GroundAt(ctx, src); return (comp.GetPortWorldCoord(0), comp.GetPortWorldCoord(1), null); }
                return (comp.GetPortWorldCoord(0), comp.GetPortWorldCoord(1), src);
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
                if (sourceIsGround)
                {
                    // Owner §5 — that shared node IS ground here, so each terminal gets its own
                    // Ground sitting exactly on its pin. Several Ground symbols name one net by
                    // definition; a wire between them would only obscure the device.
                    GroundAt(ctx, gateNeg);
                    GroundAt(ctx, drainNeg);
                    if (n == 3)
                    {
                        GroundAt(ctx, comp.GetPortWorldCoord(4));
                        GroundAt(ctx, comp.GetPortWorldCoord(5));
                    }
                    return (gate, drain, null);
                }

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
                    GroundAt(ctx, groundPin);
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
        if (r != 0) point = PlaceTwoPin(ctx, point, dir, rName, SymbolKind.Resistor, "R", Num(r), "");
        if (l != 0) point = PlaceTwoPin(ctx, point, dir, lName, SymbolKind.Inductor, "L", Num(l), "");
        return point;
    }

    /// <summary>A shunt capacitance to ground, tapped at <paramref name="at"/>. The node identity is
    /// UNCHANGED — a shunt does not advance the chain, matching <c>HarmonicaNetlist.Shunt</c>.</summary>
    private static (double X, double Y) PlaceShunt(Ctx ctx, (double X, double Y) at, Direction dir,
                                                    string name, double c)
    {
        if (c == 0) return at;
        var pins = PlaceTwoPinComponent(ctx, at, dir, name, SymbolKind.Capacitor, "C", Num(c), "");
        GroundAt(ctx, pins.Far);
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

    /// <summary>
    /// The ideal choke (straight UP off the termination plane) and its DC supply.
    ///
    /// <para><b>Polarity, and the bug this fixed.</b> <c>Vdc</c>'s pin 0 is its "+" terminal and pin 1
    /// its "−" (<c>BuiltInSymbols.BuildVdcSource</c> draws the markers there; <c>VdcModel.Stamp</c>
    /// constrains <c>V(Nodes[0]) − V(Nodes[1]) = Vdc</c>). This used to ground pin 0 and feed the
    /// choke from pin 1, i.e. it exported <c>−Vgs</c> and <c>−Vds</c>.</para>
    ///
    /// <para><b>Placement, owner §12.</b> The supply sits <paramref name="toward"/> (−1 left, +1
    /// right) of the choke and ABOVE it, so the wire leaves the "+" pin sideways and only then turns
    /// down — a straight vertical drop from a supply sitting directly above the choke runs through
    /// the Vdc's own symbol body, which is exactly what was reported.</para>
    /// </summary>
    private static void PlaceBias(Ctx ctx, (double X, double Y) outer,
                                  string chokeName, string vdcName, double vdcVolts, double chokeH,
                                  int toward, string probeName, string supplyNet)
    {
        var (chokeValue, chokeUnit) = Engineering(chokeH, "H");
        var biasNode = PlaceTwoPin(ctx, outer, Direction.Up, chokeName, SymbolKind.Inductor,
                                   "L", chokeValue, chokeUnit);

        // The supply's own centre: Pitch to the side, and one Pitch further up so its "+" (top) pin
        // clears the choke's own top pin by a full grid step before the wire turns.
        double side = Pitch;
        (double X, double Y) plus, minus;
        while (true)
        {
            (double X, double Y) pos = (biasNode.X + toward * side, biasNode.Y - Pitch + Lead);
            plus  = (pos.X, pos.Y - Lead);
            minus = (pos.X, pos.Y + Lead);
            if (!IsObstructed(ctx, plus) && !IsObstructed(ctx, minus))
            {
                var vdc = AddComponent(ctx, SymbolKind.Vdc, pos.X, pos.Y, SymbolRotation.R0, vdcName);
                vdc.Parameters.Add(new EditableParameter
                    { Name = "Vdc", Expression = Num(vdcVolts), Unit = "V", Dimension = UnitDimension.Voltage });
                break;
            }
            side += Pitch;
        }

        GroundAt(ctx, minus);

        // The DC probe goes on the SIDEWAYS leg, between the supply's "+" pin and the turn down to
        // the choke, oriented so its np→nm current is the current LEAVING the supply — see
        // PlaceProbe's own doc comment for why that is the only orientation that makes
        // `V(supply)·I(probe)` the power the supply DELIVERS.
        var towardChoke = toward < 0 ? Direction.Right : Direction.Left;
        var afterProbe = PlaceProbe(ctx, plus, towardChoke, probeName, currentAlongTravel: true,
                                    netLabel: supplyNet);
        ConnectOrthogonal(ctx, afterProbe, biasNode);
    }

    /// <summary>The source termination: the DC block (lying along its own horizontal run), then a
    /// <c>P1Tone</c> whose available power is the swept <see cref="PinVariable"/> global.</summary>
    private static void PlaceSourceTermination(Ctx ctx, (double X, double Y) outer,
                                               CircuitModel model, TerminationSet t)
    {
        // The input probe sits at the DUT's own gate plane, BEFORE the DC block, so what it measures
        // is the current actually delivered into the device. Its np→nm direction is AGAINST the
        // placement travel (which runs outward, away from the DUT) because power flows the other way
        // — into the DUT — and Pin_deliv must come out positive.
        var afterProbe = PlaceProbe(ctx, outer, Direction.Left, InputProbe,
                                    currentAlongTravel: false, netLabel: InputNet);

        var (blockValue, blockUnit) = Engineering(model.Settings.DcBlockFarads, "F");
        var afterBlock = PlaceTwoPin(ctx, afterProbe, Direction.Left, "CBLKS", SymbolKind.Capacitor,
                                     "C", blockValue, blockUnit);

        // P1Tone stays upright (a source drawn over its own ground is the conventional reading), so
        // its centre sits one lead BELOW the run: pin 0 (top) meets the block on a straight
        // horizontal wire, pin 1 (bottom) is grounded where it stands.
        double reach = Pitch;
        (double X, double Y) top, bottom;
        EditableComponent pin;
        while (true)
        {
            (double X, double Y) pos = (afterBlock.X - reach, afterBlock.Y + Lead);
            top    = (pos.X, pos.Y - Lead);
            bottom = (pos.X, pos.Y + Lead);
            if (!IsObstructed(ctx, top) && !IsObstructed(ctx, bottom))
            {
                pin = AddComponent(ctx, SymbolKind.P1Tone, pos.X, pos.Y, SymbolRotation.R0, "PIN");
                break;
            }
            reach += Pitch;
        }

        pin.Parameters.Add(new EditableParameter { Name = "Num",  Expression = "1" });
        pin.Parameters.Add(new EditableParameter
            { Name = "Pavl", Expression = PinVariable, Unit = "dBm", Dimension = UnitDimension.Power });
        // "Z" is P1Tone's own catch-all (Zdefault) AND the Γ→Z reference, and the factory requires it
        // to be REAL — so it stays the unmarked-band value; every band in range is written explicitly
        // below and never falls through to it.
        pin.Parameters.Add(new EditableParameter { Name = "Z", Expression = Num(TerminationSet.UnmarkedBandOhms) });
        pin.Parameters.Add(new EditableParameter
        {
            Name = "Freq", Expression = Num(model.Settings.FrequencyHz / 1e9),
            Unit = "GHz", Dimension = UnitDimension.Frequency,
        });
        AppendBandParams(pin, t, TerminationSide.Source, model.Settings.HarmonicCount);

        ConnectOrthogonal(ctx, afterBlock, top);
        GroundAt(ctx, bottom);
    }

    /// <summary>
    /// The load termination (owner §8): the DC block lying along the drain's own row, then a
    /// <c>LoadTuner</c> named <see cref="LoadTunerInstanceName"/> whose single pin meets the block's
    /// far end on one straight horizontal wire (owner §10 — "no bends in it").
    ///
    /// <para><c>BiasTee=off</c> and <c>ShowBias=false</c>: the drain supply is already drawn as an
    /// explicit LCHD/VDD pair, and a second, hidden bias tee inside the tuner would be a second DC
    /// path nothing on the schematic shows.</para>
    /// </summary>
    private static void PlaceLoadTermination(Ctx ctx, (double X, double Y) outer,
                                             CircuitModel model, TerminationSet t)
    {
        // The output probe sits at the DUT's own drain plane, before the DC block — and here the
        // travel direction IS the power-flow direction (outward, toward the load), so np leads.
        var afterProbe = PlaceProbe(ctx, outer, Direction.Right, OutputProbe,
                                    currentAlongTravel: true, netLabel: OutputNet);

        var (blockValue, blockUnit) = Engineering(model.Settings.DcBlockFarads, "F");
        var afterBlock = PlaceTwoPin(ctx, afterProbe, Direction.Right, "CBLKL", SymbolKind.Capacitor,
                                     "C", blockValue, blockUnit);

        // LoadTuner's single pin is at local (−300, 0) — a left-facing lead — so the centre sits
        // 300 further right than the pin, and the pin lands on the block's own row.
        const double PinLead = 300.0;
        double reach = Pitch;
        (double X, double Y) tunerPin;
        EditableComponent load;
        while (true)
        {
            (double X, double Y) pos = (afterBlock.X + reach, afterBlock.Y);
            tunerPin = (pos.X - PinLead, pos.Y);
            if (!IsObstructed(ctx, tunerPin))
            {
                load = AddComponent(ctx, SymbolKind.LoadTuner, pos.X, pos.Y, SymbolRotation.R0,
                                    LoadTunerInstanceName);
                break;
            }
            reach += Pitch;
        }

        AppendBandParams(load, t, TerminationSide.Load, model.Settings.HarmonicCount);
        load.Parameters.Add(new EditableParameter
            { Name = "Zdefault", Expression = Num(TerminationSet.UnmarkedBandOhms), ShowOnSchematic = false });
        load.Parameters.Add(new EditableParameter
            { Name = "Z0", Expression = Num(model.Settings.Z0), ShowOnSchematic = false });
        // QUOTED, not bare: a schematic parameter is an EXPRESSION, so a bare `off` resolves as a
        // variable name and elaboration fails with "Unresolved name 'off'" (the `.cnl` spelling
        // `BiasTee=off` is a different reader with a different grammar). CreateTunerModel wants a
        // ValueKind.String and only ever compares it against "on".
        load.Parameters.Add(new EditableParameter { Name = "BiasTee",  Expression = "\"off\"", ShowOnSchematic = false });
        load.Parameters.Add(new EditableParameter { Name = "ShowBias", Expression = "false", ShowOnSchematic = false });

        ConnectOrthogonal(ctx, afterBlock, tunerPin);
    }

    /// <summary>Owner §3/§8 — <b>every</b> band in range, not only the marked ones. A band with no
    /// marker is at <see cref="TerminationSet.UnmarkedBandOhms"/> in harmonicaRF and must say so in
    /// the exported component: leaving it out hands the band to the component's own catch-all, which
    /// is a different number the moment anyone edits the file.</summary>
    private static void AppendBandParams(EditableComponent comp, TerminationSet t,
                                         TerminationSide side, int harmonicCount)
    {
        for (int band = 1; band <= harmonicCount; band++)
            comp.Parameters.Add(new EditableParameter
            {
                Name       = $"Z[{band}]",
                Expression = ComplexJ(t.Z(side, band)),
                Unit       = "",
            });
    }

    // ── current probes, net labels and the PA measurement block ───────────────

    /// <summary>
    /// Inserts an <c>IProbe</c> in series on a HORIZONTAL run, <see cref="Lead"/> past
    /// <paramref name="from"/>, wires <paramref name="from"/> to its near pin, optionally names that
    /// wire's net, and returns its far pin — the same "step along the chain" shape
    /// <see cref="PlaceTwoPinComponent"/> has, for a component whose geometry is nothing like a
    /// two-terminal primitive's.
    ///
    /// <para><b>ORIENTATION IS THE WHOLE POINT, and it is not symmetric.</b> An IProbe's pins are
    /// <c>np</c> at local <c>(0, +100)</c> and <c>nm</c> at <c>(+100, +100)</c>
    /// (<c>SymbolPortDefs.For</c>), and the current it reports flows <c>np → nm</c>. At <c>R0</c> that
    /// is left-to-right; <c>MirrorX</c> negates the local x, so <c>nm</c> moves to <c>−100</c> and the
    /// reported current runs right-to-left. <c>np</c> stays at the component's own X either way, which
    /// is what makes the placement arithmetic below a single case rather than two.</para>
    ///
    /// <para><paramref name="currentAlongTravel"/> is what a caller actually knows: does the current
    /// I want to measure flow in the same direction the chain is being BUILT, or against it? Both
    /// occur here. The load chain is built outward from the drain and power flows outward too, so it
    /// is <c>true</c>. The source chain is also built outward — away from the gate, toward the
    /// generator — while power flows inward, so it is <c>false</c>. Get this backwards and every
    /// derived number keeps its magnitude and flips its sign, which is exactly the failure the owner
    /// warned about ("otherwise the current direction will be backwards").</para>
    ///
    /// <para>The probe's own centre sits 100 units ABOVE the run, because both its pins are 100 below
    /// its origin. That is the only place in this file where a component's centre is not on the wire
    /// it sits in.</para>
    /// </summary>
    private static (double X, double Y) PlaceProbe(Ctx ctx, (double X, double Y) from, Direction dir,
                                                   string instanceName, bool currentAlongTravel,
                                                   string? netLabel)
    {
        int sign = dir == Direction.Right ? +1 : -1;
        double reach = Lead;
        (double X, double Y) near, far;
        while (true)
        {
            near = (from.X + sign * reach,         from.Y);
            far  = (from.X + sign * (reach + 100), from.Y);
            if (!IsObstructed(ctx, near) && !IsObstructed(ctx, far)) break;
            reach += 200;
        }

        var np = currentAlongTravel ? near : far;
        var nm = currentAlongTravel ? far  : near;

        // np is at the component's own X at BOTH mirror states; the mirror is what decides which side
        // nm lands on. See this method's own doc comment.
        AddComponent(ctx, SymbolKind.IProbe, np.X, from.Y - 100, SymbolRotation.R0, instanceName,
                     mirrorX: nm.X < np.X);

        var lead = ConnectOrthogonalTracked(ctx, from, near);
        if (netLabel is not null && lead is not null) LabelNet(ctx, lead, netLabel);
        return far;
    }

    /// <summary>Draws a run and hands back its FIRST wire, so a caller can hang a net label on it —
    /// the anchor a <see cref="EditableNetLabel"/> needs is a wire, not a coordinate.</summary>
    private static EditableWire? ConnectOrthogonalTracked(Ctx ctx, (double X, double Y) a, (double X, double Y) b)
    {
        int before = ctx.Model.Wires.Count;
        ConnectOrthogonal(ctx, a, b);
        return ctx.Model.Wires.Count > before ? ctx.Model.Wires[before] : null;
    }

    /// <summary>
    /// Names the net <paramref name="wire"/> belongs to, so a measurement can say <c>V("Vout", 1)</c>
    /// instead of chasing an auto-assigned <c>n7</c> that moves whenever the drawing does.
    ///
    /// <para>Anchored at the segment's midpoint with a 20-unit lift — the label draws just above the
    /// wire, and <c>NetExtractor.FindLabelNetKey</c> resolves it by scanning for a segment within
    /// <c>GridSize/2</c> = 50 units, so the lift is well inside what binds. (Both numbers are copied
    /// from a real hand-drawn testbench rather than picked.)</para>
    /// </summary>
    private static void LabelNet(Ctx ctx, EditableWire wire, string name)
    {
        if (wire.Points.Count < 2) return;
        var a = wire.Points[0];
        var b = wire.Points[^1];
        var label = new EditableNetLabel { Name = name };
        label.AnchorToWire(wire, (a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0 - 20.0);
        ctx.Model.NetLabels.Add(label);
    }

    /// <summary>
    /// The PA metrics, as <c>MEAS</c> blocks under the circuit — three of them, grouped the way a
    /// reader thinks about them (input, output/gain, DC/efficiency) rather than one long column.
    ///
    /// <para>Every equation resolves through the probes and net labels placed above, and every one is
    /// named so the NEXT equation can use it: <c>MeasurementEvaluator</c> binds each result into scope
    /// as it goes, so <c>Gp_dB</c> may simply say <c>Pout_dBm - Pin_deliv_dBm</c>. Order therefore
    /// matters, and is the order written here.</para>
    ///
    /// <para><b>The DC term needs BOTH supplies, and the gate's is not negligible on the shipped
    /// device.</b> Its gate is a plain 50 Ω to source (<c>I[1,0] = _v1/50</c>), so at Vgs = −3.05 V it
    /// draws 61 mA — 0.19 W the gate supply really delivers. Dropping that term would overstate
    /// efficiency. Both probes measure current OUT of their own supply, so both products are
    /// "power delivered" with no sign correction anywhere.</para>
    /// </summary>
    private static void PlaceMeasurements(Ctx ctx)
    {
        double top = ctx.Model.Components.Count == 0 ? 0 : ctx.Model.Components.Max(c => c.Y);
        double y   = SnapToGrid(top) + Pitch;
        double x   = SnapToGrid(ctx.Model.Components.Count == 0 ? 0 : ctx.Model.Components.Min(c => c.X));

        Block("MEAS1", x, y,
            ("Pin_avail_dBm", PinVariable),
            ("Pin_deliv_W",   $"real(0.5*HB1.V(\"{InputNet}\",1)*conj(HB1.I(\"{InputProbe}\",1)))"),
            ("Pin_deliv_dBm", "10*log10(Pin_deliv_W*1000)"),
            ("IRL_dB",        "Pin_deliv_dBm - Pin_avail_dBm"),
            ("Zin",           $"HB1.V(\"{InputNet}\",1)/HB1.I(\"{InputProbe}\",1)"));

        Block("MEAS2", x + 4 * Pitch, y,
            ("Pout_W",   $"real(0.5*HB1.V(\"{OutputNet}\",1)*conj(HB1.I(\"{OutputProbe}\",1)))"),
            ("Pout_dBm", "10*log10(Pout_W*1000)"),
            ("Gp_dB",    "Pout_dBm - Pin_deliv_dBm"),
            ("Gt_dB",    "Pout_dBm - Pin_avail_dBm"));

        Block("MEAS3", x + 8 * Pitch, y,
            ("Idc_A",   $"real(HB1.I(\"{DrainDcProbe}\",0))"),
            ("Pdc_W",   $"real(HB1.V(\"{DrainBiasNet}\",0)*HB1.I(\"{DrainDcProbe}\",0))" +
                        $" + real(HB1.V(\"{GateBiasNet}\",0)*HB1.I(\"{GateDcProbe}\",0))"),
            ("DE_pct",  "Pout_W/Pdc_W*100"),
            ("PAE_pct", "(Pout_W - Pin_deliv_W)/Pdc_W*100"));

        void Block(string name, double bx, double by, params (string Name, string Expr)[] rows)
        {
            var m = AddComponent(ctx, SymbolKind.Meas, bx, by, SymbolRotation.R0, name);
            foreach (var (n, e) in rows)
                m.Parameters.Add(new EditableParameter { Name = n, Expression = e });
        }
    }

    /// <summary>Owner §4 — the drive level is a <c>VAR</c> the analysis sweeps, not a literal on the
    /// source. Placed clear of the circuit; a VAR has no pins, so its position is cosmetic.</summary>
    private static void PlacePinVariable(Ctx ctx, double pavlDbm)
    {
        double minY = ctx.Model.Components.Count == 0 ? 0 : ctx.Model.Components.Min(c => c.Y);
        var v = AddComponent(ctx, SymbolKind.Var, 0, SnapToGrid(minY) - Pitch, SymbolRotation.R0, "VAR1");
        v.Parameters.Add(new EditableParameter
            { Name = PinVariable, Expression = Num(pavlDbm), Unit = "", ShowOnSchematic = true });
    }

    // ── the analysis (R-h9c-15 — "configured the same way as harmonicaRF") ─────

    private static HarmonicBalanceAnalysis BuildAnalysis(CircuitModel model) => new("HB1")
    {
        ToneExpr          = Num(model.Settings.FrequencyHz / 1e9),
        ToneUnit          = "GHz",
        NumFreqsExpr       = "1",
        MaxHarmonicExpr    = model.Settings.HarmonicCount.ToString(CultureInfo.InvariantCulture),
        TolExpr            = Num(model.Settings.Tol),
        MaxIterExpr        = model.Settings.MaxIter.ToString(CultureInfo.InvariantCulture),
        GuardHarmonicExpr  = model.Settings.GuardHarmonic.ToString(CultureInfo.InvariantCulture),
        LambdaExpr         = Num(model.Settings.Lambda),
        FFTOverSampleExpr  = model.Settings.FftOverSample.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>Owner §4 — the Pin sweep, Start/Stop/Step exactly as harmonicaRF's own Power Sweep
    /// dialog states them. Null when the range cannot describe a sweep (a non-positive step, or a
    /// stop below the start), which is left as a plain single-point HB rather than an analysis that
    /// fails to expand.</summary>
    private static ParametricSweepAnalysis? BuildPinSweep(CircuitModel model, string innerName)
    {
        double start = model.Settings.PinStartDbm;
        double stop  = model.Settings.PinMaxDbm;
        double step  = model.Settings.PinStepDbm;
        if (!(step > 0) || !double.IsFinite(start) || !double.IsFinite(stop) || stop < start) return null;

        return new ParametricSweepAnalysis(
            "PinSweep", PinVariable,
            new SweepSpec(start, stop, step, SweepAxisMode.StepSize, SweepKind.Linear),
            innerName);
    }

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
                                                   SymbolRotation rotation, string instanceName,
                                                   bool mirrorX = false)
    {
        var c = new EditableComponent
        {
            Symbol       = kind,
            X            = SnapToGrid(x),
            Y            = SnapToGrid(y),
            Rotation     = rotation,
            // The mirror is a CONSTRUCTOR argument, not something a caller sets afterwards: the pin
            // registration below reads GetPortWorldCoord, so a mirror applied later leaves
            // AvoidPoints holding the UNMIRRORED pin — a phantom obstacle at a coordinate no pin
            // occupies, and no obstacle at the one that does. Both halves of that were observed on
            // the 3-port SDD export the moment IProbe (the only mirrored component here) appeared.
            MirrorX      = mirrorX,
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
        //
        // A Ground is the deliberate exception: it is PUT on an existing pin (owner §5/§6), so
        // registering its own pin would make that coordinate look doubly obstructed to nothing.
        if (kind != SymbolKind.Ground)
            for (int i = 0; i < c.PortCount; i++)
                ctx.AvoidPoints.Add(c.GetPortWorldCoord(i));

        return c;
    }

    /// <summary>Places a 2-pin component <see cref="Pitch"/> further in <paramref name="dir"/>, wires
    /// <paramref name="from"/> to its near pin, and returns its far pin — the general series-element
    /// step every R/L/C placement in this file reduces to.</summary>
    private static (double X, double Y) PlaceTwoPin(Ctx ctx, (double X, double Y) from, Direction dir,
                                                     string instanceName, SymbolKind kind,
                                                     string paramName, string value, string unit)
        => PlaceTwoPinComponent(ctx, from, dir, instanceName, kind, paramName, value, unit).Far;

    /// <summary>
    /// The two pin coordinates a 2-pin primitive centred at <paramref name="pos"/> would have, and
    /// which of them is nearer <paramref name="dir"/>'s origin.
    ///
    /// <para>Orientation follows the run (owner §7): a LEFT/RIGHT run places the component at R90, so
    /// its pins lie east/west (<c>LocalToWorld</c>'s R90 maps local <c>(0,−200)</c> to world
    /// <c>(+200, 0)</c> and <c>(0,+200)</c> to <c>(−200, 0)</c>); an UP/DOWN run leaves it at R0 with
    /// pins north/south. Either way pin 0 sits on the +X/−Y side, so the pin nearer the anchor is
    /// pin 0 for a Left or Down run and pin 1 for a Right or Up one.</para>
    /// </summary>
    private static (SymbolRotation Rot, (double X, double Y) Pin0, (double X, double Y) Pin1,
                    (double X, double Y) Near, (double X, double Y) Far)
        PinGeometry((double X, double Y) pos, Direction dir)
    {
        bool horizontal = IsHorizontal(dir);
        var pin0 = horizontal ? (pos.X + Lead, pos.Y) : (pos.X, pos.Y - Lead);
        var pin1 = horizontal ? (pos.X - Lead, pos.Y) : (pos.X, pos.Y + Lead);
        bool nearIsPin0 = dir is Direction.Left or Direction.Down;
        return (horizontal ? SymbolRotation.R90 : SymbolRotation.R0,
                pin0, pin1,
                nearIsPin0 ? pin0 : pin1,
                nearIsPin0 ? pin1 : pin0);
    }

    private static ((double X, double Y) Near, (double X, double Y) Far) PlaceTwoPinComponent(
        Ctx ctx, (double X, double Y) from, Direction dir, string instanceName, SymbolKind kind,
        string paramName, string value, string unit)
    {
        // A 2-pin component's own leads always sit exactly 200 units from its centre (see
        // PinGeometry), so the near/far pins a candidate placement WOULD produce are computable
        // before any component is actually created. That is what lets a placement whose pin would
        // land EXACTLY on a DUT pin be discarded and retried further out, rather than only discovered
        // afterward — a fixed Pitch is exactly a multiple of the DUT's own internal pin spacing (200
        // within one differential pair, 400 between consecutive ports on a 3-port SDD) purely by
        // arithmetic coincidence, and clearing one modulus does not clear the other (found by tracing
        // a real n=3 export's coordinates against NetExtractor's output).
        double pitch = Pitch;
        (double X, double Y) pos, near, far;
        SymbolRotation rot;
        while (true)
        {
            pos = Offset(from, dir, pitch);
            (rot, _, _, near, far) = PinGeometry(pos, dir);
            if (!IsObstructed(ctx, near) && !IsObstructed(ctx, far)) break;
            pitch += 200;
        }

        var comp = AddComponent(ctx, kind, pos.X, pos.Y, rot, instanceName);
        comp.Parameters.Add(new EditableParameter { Name = paramName, Expression = value, Unit = unit });

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
    /// tracing a real n=3 export: PLOAD's own near pin, placed specifically to clear every pin in
    /// <c>AvoidPoints</c>, still landed squarely on the interior of the DUT-to-LCHG wire — a point
    /// that coincides with no PIN at all, so the existing check found nothing wrong, while
    /// NetExtractor's own §5.1 rule unions it with that wire regardless).
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
    /// registered pin, with a point some Ground already occupies, OR landing on the interior of an
    /// already-drawn wire. Always all three together — see <see cref="CoincidesWithWireInterior"/>'s
    /// own doc comment for why checking only the first is not enough.</summary>
    private static bool IsObstructed(Ctx ctx, (double X, double Y) p)
        => CoincidesWithAvoidPoint(ctx, p)
           || ctx.GroundedPoints.Contains((p.X, p.Y))
           || ctx.WireVertices.Contains((p.X, p.Y))
           || CoincidesWithWireInterior(ctx, p);

    /// <summary>
    /// A <c>Ground</c> placed EXACTLY on <paramref name="at"/> — owner §5/§6, "place a GND exactly on
    /// the pin that is to be grounded (no wires)". A Ground's one pin is at its own origin, so the
    /// two coincide and NetExtractor unions them by the same rule any other coincident pair follows.
    ///
    /// <para>Several separate <c>Ground</c> symbols are ordinary and are what this file now uses
    /// everywhere: every one names the SAME net ("0") by definition, so a wire between two of them
    /// carries no information and only crowds the drawing.</para>
    /// </summary>
    private static void GroundAt(Ctx ctx, (double X, double Y) at)
    {
        (double X, double Y) pos = (SnapToGrid(at.X), SnapToGrid(at.Y));
        if (!ctx.GroundedPoints.Add((pos.X, pos.Y))) return;   // already grounded — one symbol is enough
        AddComponent(ctx, SymbolKind.Ground, pos.X, pos.Y, SymbolRotation.R0,
                     $"GND{++ctx.GroundCount}");
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
            if (!TryRoute(ctx, a, escapePt, dipPt, alignPt, b)) continue;
            return;
        }

        // No candidate cleared every avoid point (should not happen with this file's own fixed
        // layout, but a silently-dropped connection would be worse than a direct run that
        // NetExtractor can at least report a conflict on) — fall back to the direct wire.
        AddWire(ctx, a, b);
    }

    /// <summary>Attempts one candidate route through <paramref name="points"/>; commits every leg and
    /// returns true only if the whole thing is clear of <see cref="Ctx.AvoidPoints"/> — a MIDPOINT
    /// landing exactly on one is rejected just like a collinear crossing, since (unlike the two ends,
    /// which are always some real pin the caller meant to reach) it is purely an artifact of which
    /// candidate happened to be tried.
    ///
    /// <para>Consecutive duplicates are collapsed FIRST, which is what lets a candidate degenerate
    /// gracefully: the perpendicular-first route (<c>escape = 0</c>) has its own first waypoint equal
    /// to <c>a</c>, and both a zero-length wire and an "is this midpoint a pin?" check against the
    /// caller's own starting pin would be wrong.</para></summary>
    private static bool TryRoute(Ctx ctx, params (double X, double Y)[] points)
    {
        var route = new List<(double X, double Y)>(points.Length);
        foreach (var p in points)
            if (route.Count == 0 ||
                Math.Abs(route[^1].X - p.X) > 1e-6 || Math.Abs(route[^1].Y - p.Y) > 1e-6)
                route.Add(p);
        if (route.Count < 2) return true;

        for (int i = 1; i < route.Count - 1; i++)
            if (CoincidesWithAvoidPoint(ctx, route[i])) return false;
        for (int i = 0; i < route.Count - 1; i++)
            if (SegmentCrossesAvoidPoint(ctx, route[i], route[i + 1])) return false;

        for (int i = 0; i < route.Count - 1; i++) AddWire(ctx, route[i], route[i + 1]);
        return true;
    }

    /// <summary>How far the staircase steps off its starting point's own column/row before dipping —
    /// small and, critically, an ODD multiple of 100: every pin coordinate this file ever computes is
    /// built from 200-unit steps (a lead half-length) from some origin, so an odd-hundred offset can
    /// never land exactly on one, regardless of which origin it was measured from.
    ///
    /// <para><b>ZERO IS FIRST, and it is the one that matters most.</b> The escape leg travels along
    /// the very axis the obstacle sits on, so stepping toward <c>b</c> only works while the obstacle
    /// is further away than the step. Once <c>a</c> is CLOSE to it — a component pin placed a single
    /// grid step short of a grounded DUT terminal, which is routine on a 3-port SDD where two ports
    /// share one column and the third sits 200 units off the chain — every non-zero forward candidate
    /// either lands ON the obstacle or crosses it on the way, all of them are rejected, and
    /// <see cref="ConnectStraightSafely"/> falls back to the very direct wire it was trying to avoid.
    /// That fallback is a SHORT whose only symptom is a singular MNA from the engine, much later and
    /// with nothing in the drawing to point at. A zero escape turns perpendicular IMMEDIATELY (an
    /// ordinary Z-bend), which needs no room along the blocked axis at all; the non-zero candidates
    /// remain for the case the original note describes, where <c>a</c> sits on a column densely packed
    /// with its own siblings and the perpendicular step has to start somewhere else.</para>
    ///
    /// <para>The NEGATIVE half is the last resort: step AWAY from <c>b</c> first. The obstacle is by
    /// definition between the two ends, so backward always has room — unless what is behind <c>a</c>
    /// is its own probe or DUT, which is exactly why it is tried last rather than relied on.</para></summary>
    private static readonly double[] EscapeCandidatesDbu = { 0, 100, 300, 500, 700, -100, -300, -500, -700 };

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
        var pa = (SnapToGrid(a.X), SnapToGrid(a.Y));
        var pb = (SnapToGrid(b.X), SnapToGrid(b.Y));
        var w = new EditableWire();
        w.Points.Add(pa);
        w.Points.Add(pb);
        ctx.Model.Wires.Add(w);
        ctx.WireVertices.Add(pa);
        ctx.WireVertices.Add(pb);
    }

    // ── number formatting (owner §1) ──────────────────────────────────────────

    /// <summary>
    /// The shortest string that round-trips back to <paramref name="v"/> exactly, with the exponent
    /// tidied to the form a person writes: <c>1e-6</c>, not <c>9.9999999999999995E-07</c>.
    ///
    /// <para>The old spelling was <c>"G17"</c>, which is round-trip-safe by brute force — 17 digits
    /// always are — and prints every one of them whether or not they carry information. .NET's
    /// <c>"R"</c> has produced the SHORTEST round-trippable form since .NET Core 3.0, so nothing
    /// about the value changes here, only how much of it is written down.</para>
    /// </summary>
    private static string Num(double v)
    {
        if (!double.IsFinite(v)) return "0";
        return Tidy(v.ToString("R", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// One SI prefix on <paramref name="baseSymbol"/> chosen so the mantissa lands in [1, 1000) —
    /// <c>1e-6 H</c> → <c>("1", "uH")</c>, <c>1 H</c> → <c>("1", "H")</c>, <c>1e-9 F</c> →
    /// <c>("1", "nF")</c>.
    ///
    /// <para>Owner §2 asked the bias network's L and C to carry units instead of being unitless
    /// ("a C is 1 uF in the schematic, not 1.000000 uF or 0.999999 uF"). A FIXED uH/uF pair reads
    /// well at microscale and badly anywhere else — the ideal bias values are 1 H and 1 F, which a
    /// fixed micro prefix would write as "1000000 uH" and "1000000 uF". Choosing the prefix from the
    /// magnitude gives the clean single digit the owner asked for at every value the setting can
    /// take, which is the request rather than the letter of it.</para>
    ///
    /// <para>The prefixes offered are exactly the ones <c>Units</c> knows for these two dimensions;
    /// anything outside that range keeps the base symbol and an exponent, which still parses.</para>
    /// </summary>
    private static (string Value, string Unit) Engineering(double baseValue, string baseSymbol)
    {
        if (baseValue == 0 || !double.IsFinite(baseValue)) return (Num(baseValue), baseSymbol);

        (double Scale, string Prefix)[] steps =
            [(1.0, ""), (1e-3, "m"), (1e-6, "u"), (1e-9, "n"), (1e-12, "p"), (1e-15, "f")];

        double mag = Math.Abs(baseValue);
        foreach (var (scale, prefix) in steps)
            if (mag >= scale)
                return (Num(baseValue / scale), prefix + baseSymbol);

        // Smaller than a femto-anything — keep the base symbol rather than invent a prefix Units
        // does not know; the exponent form parses perfectly well.
        return (Num(baseValue), baseSymbol);
    }

    /// <summary>An impedance, at a precision a person would read (owner §8: "round the Z number to a
    /// reasonable precision"). Six significant figures is far past any termination anyone sets by
    /// hand and still keeps the near-short (1e-6) exactly as it is.</summary>
    private static string NumZ(double v)
    {
        if (!double.IsFinite(v)) return "0";
        return Tidy(v.ToString("G6", CultureInfo.InvariantCulture));
    }

    /// <summary><c>1E-06</c> → <c>1e-6</c>; a plain decimal is returned unchanged.</summary>
    private static string Tidy(string s)
    {
        int e = s.IndexOf('E');
        if (e < 0) return s;

        string mantissa = s[..e];
        string exponent = s[(e + 1)..];
        bool negative = exponent.StartsWith('-');
        if (negative || exponent.StartsWith('+')) exponent = exponent[1..];
        exponent = exponent.TrimStart('0');
        return exponent.Length == 0 ? mantissa : $"{mantissa}e{(negative ? "-" : "")}{exponent}";
    }

    /// <summary>Owner §3/§8 — a complex impedance in <c>j</c> form (<c>80+j*10</c>), never
    /// <c>complex(80,10)</c>. Both parse; the <c>j</c> form is the one every hero <c>.cnl</c> and the
    /// Tuner's own parameter documentation is written in, and the one a user can edit in place.</summary>
    private static string ComplexJ(Complex z)
    {
        string re = NumZ(z.Real);
        if (z.Imaginary == 0) return re;
        double im = z.Imaginary;
        return $"{re}{(im < 0 ? "-" : "+")}j*{NumZ(Math.Abs(im))}";
    }
}
