using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Palette wiring for the families added alongside the MESFETs and the bipolar pair: the two lateral
/// MOS levels, the vertical power MOSFET, the JFET, the IGBT, the three p-channel MESFET laws and
/// the ferrite bead.
///
/// <para><b>The load-bearing test is W4</b>, and it is <see cref="DevicePaletteWiringTests"/>'s P4
/// applied to the new families: every parameter name the registry offers is proven to REACH the
/// model, by perturbing it and requiring the device's behaviour to move. A registry name the factory
/// does not read compiles, saves, loads, appears in the parameter dialog, accepts an edit, and then
/// does nothing at all. These families brought roughly two hundred new parameter rows in one go,
/// which is exactly the situation that failure mode is waiting for.</para>
///
/// <para>Written as its own file rather than folded into P4 because P4's other tests are built
/// around a shared FET parameter block and a three-pin geometry, neither of which these share.</para>
///
///   W1 — every new kind is a placeable palette item in the category a user would look in.
///   W2 — geometry: pin names and counts, including the MOS family's fourth pin.
///   W3 — each tile places a DISTINCT engine component.
///   W4 — every registry parameter name is read by the factory, and the registry's name set matches
///        this test's activation table exactly.
///   W5 — a freshly placed device is a WORKING device: the defaults build and evaluate finitely.
/// </summary>
public class GateControlledPaletteWiringTests
{
    private static readonly SymbolKind[] Nonlinear =
    [
        SymbolKind.Mos1N, SymbolKind.Mos1P, SymbolKind.Mos3N, SymbolKind.Mos3P,
        SymbolKind.VdmosN, SymbolKind.VdmosP,
        SymbolKind.JfetN, SymbolKind.JfetP,
        SymbolKind.IgbtN, SymbolKind.IgbtP,
        SymbolKind.PFetCurtice, SymbolKind.PFetStatz, SymbolKind.PFetMaterka,
    ];

    // ── W1 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void W1_EveryNewKindIsPlaceable_InTheCategoryAUserWouldLookIn()
    {
        var items = LibraryCatalog.AllItems;
        foreach (var kind in Nonlinear)
        {
            var item = items.FirstOrDefault(i => i.Kind == kind);
            Assert.True(item is not null, $"{kind} is missing from the palette catalog");
            Assert.Equal(ComponentCategory.Devices, item!.Category);
            Assert.Contains(ComponentCategory.Nonlinear, item.ExtraCategories);
        }

        // The bead is a LINEAR impedance and belongs with the lumped elements, not the devices.
        var bead = items.FirstOrDefault(i => i.Kind == SymbolKind.Bead);
        Assert.True(bead is not null, "the ferrite bead is missing from the palette catalog");
        Assert.Equal(ComponentCategory.Lumped, bead!.Category);

        // Findable by the words a user actually types. The card's own type name matters most: a
        // user who has just been shown a refusal naming NMOS or VDMOS types that word next.
        void Reaches(string query, params SymbolKind[] expected)
        {
            var hits = LibraryCatalog.Search(query).Select(i => i.Kind).ToHashSet();
            foreach (var k in expected)
                Assert.True(hits.Contains(k), $"search '{query}' must reach {k}");
        }

        Reaches("NMOS",   SymbolKind.Mos1N, SymbolKind.Mos3N);
        Reaches("PMOS",   SymbolKind.Mos1P, SymbolKind.Mos3P);
        Reaches("MOSFET", SymbolKind.Mos1N, SymbolKind.Mos3N, SymbolKind.VdmosN);
        Reaches("VDMOS",  SymbolKind.VdmosN, SymbolKind.VdmosP);
        Reaches("JFET",   SymbolKind.JfetN, SymbolKind.JfetP);
        Reaches("IGBT",   SymbolKind.IgbtN, SymbolKind.IgbtP);
        Reaches("PMF",    SymbolKind.PFetCurtice, SymbolKind.PFetStatz, SymbolKind.PFetMaterka);
        Reaches("ferrite", SymbolKind.Bead);
        // …and by what the part is FOR, which is how a switching part is looked for.
        Reaches("body diode", SymbolKind.VdmosN);
        Reaches("inverter",   SymbolKind.IgbtN);
    }

    // ── W2 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void W2_Geometry_TheMosFamilyHasAFourthPinAndTheOthersDoNot()
    {
        // FOUR pins, and the bulk is a real one. Tying it internally would have been one line and
        // would silently delete the body effect.
        foreach (var kind in new[] { SymbolKind.Mos1N, SymbolKind.Mos1P, SymbolKind.Mos3N, SymbolKind.Mos3P })
        {
            var ports = SymbolPortDefs.For(kind);
            Assert.Equal(["d", "g", "s", "b"], ports.Select(p => p.Name));
            // The bulk sits opposite the gate, which keeps the three familiar pins where a reader
            // already expects them.
            Assert.Equal((200f, 0f), (ports[3].LocalX, ports[3].LocalY));
        }

        // Three pins each, and the power MOSFET's is deliberately NOT four: its source-to-body short
        // is inside the silicon, which is what makes the body diode a source-to-drain element.
        foreach (var kind in new[] { SymbolKind.VdmosN, SymbolKind.VdmosP, SymbolKind.JfetN, SymbolKind.JfetP })
            Assert.Equal(["d", "g", "s"], SymbolPortDefs.For(kind).Select(p => p.Name));

        foreach (var kind in new[] { SymbolKind.IgbtN, SymbolKind.IgbtP })
            Assert.Equal(["c", "g", "e"], SymbolPortDefs.For(kind).Select(p => p.Name));

        // The p-channel MESFET laws keep their n-channel counterpart's geometry exactly — they are
        // the same component with every sign reversed.
        foreach (var kind in new[] { SymbolKind.PFetCurtice, SymbolKind.PFetStatz, SymbolKind.PFetMaterka })
            Assert.Equal(SymbolPortDefs.For(SymbolKind.FetCurtice), SymbolPortDefs.For(kind));

        // The bead falls through to the lumped default — R, L and C's own two pins — so there is no
        // second copy of those coordinates to drift.
        Assert.Equal(SymbolPortDefs.For(SymbolKind.Resistor), SymbolPortDefs.For(SymbolKind.Bead));
    }

    // ── W3 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void W3_EachTilePlacesADistinctEngineComponent()
    {
        var refs = Nonlinear.Concat([SymbolKind.Bead])
            .ToDictionary(k => k, k => ComponentTypeRegistry.EngineReference(k));

        Assert.Equal(refs.Count, refs.Values.Distinct(StringComparer.Ordinal).Count());

        Assert.Equal("MOS1_N",  refs[SymbolKind.Mos1N]);
        Assert.Equal("MOS3_P",  refs[SymbolKind.Mos3P]);
        Assert.Equal("VDMOS_N", refs[SymbolKind.VdmosN]);
        Assert.Equal("JFET_P",  refs[SymbolKind.JfetP]);
        Assert.Equal("IGBT_N",  refs[SymbolKind.IgbtN]);
        Assert.Equal("Bead",    refs[SymbolKind.Bead]);
        Assert.Equal("PFET_Statz", refs[SymbolKind.PFetStatz]);

        // …and every one of them builds.
        foreach (var (kind, reference) in refs)
            Assert.True(ComponentModelFactory.TryCreate(reference, new Dictionary<string, Value>()) is not null,
                $"the factory does not build {kind}'s engine reference '{reference}'");
    }

    // ── W4 ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Non-zero, non-default working values for EVERY parameter the registry offers. Non-zero
    /// matters: a parameter left at zero can be structurally inert, and perturbing an inert
    /// parameter proves nothing.
    ///
    /// <para><b>Temp sits far from Tnom in every table below</b>, for the reason the bipolar's does:
    /// at <c>Temp == Tnom</c> every temperature relation collapses to the identity, so Xti, Eg and
    /// Tnom all read as unwired parameters while doing exactly what they say.</para>
    /// </summary>
    private static Dictionary<string, double> Activation(SymbolKind kind)
    {
        // Shared by both lateral MOS levels. Every process alternative is live as well as its device
        // counterpart — Nsub, Uo, Rsh/Nrd/Nrs, Cj/Cjsw with the areas — because the model derives one
        // from the other only where the device quantity is absent, and a perturbation of the
        // process one has to move something even so.
        Dictionary<string, double> Mos() => new()
        {
            ["Vto"] = 0.72, ["Kp"] = 6e-5, ["Gamma"] = 0.55, ["Phi"] = 0.7,
            ["W"] = 12e-6, ["L"] = 0.9e-6, ["Ld"] = 0.06e-6, ["Tox"] = 16e-9,
            ["Uo"] = 540.0, ["Nsub"] = 8e15,
            ["Cgso"] = 2.6e-10, ["Cgdo"] = 2.4e-10, ["Cgbo"] = 1.8e-10,
            ["Is"] = 2e-14, ["Js"] = 1e-4, ["N"] = 1.08,
            ["Cbd"] = 18e-15, ["Cbs"] = 19e-15, ["Cj"] = 3e-4, ["Cjsw"] = 1.6e-10,
            ["Ad"] = 30e-12, ["As"] = 32e-12, ["Pd"] = 9e-6, ["Ps"] = 9.5e-6,
            ["Pb"] = 0.84, ["Mj"] = 0.46, ["Mjsw"] = 0.31, ["Fc"] = 0.55,
            ["Rd"] = 7.0, ["Rs"] = 5.0, ["Rsh"] = 22.0, ["Nrd"] = 1.4, ["Nrs"] = 1.6,
            ["Xti"] = 3.2, ["Eg"] = 1.14, ["Temp"] = 85.0, ["Tnom"] = 25.0,
        };

        switch (kind)
        {
            case SymbolKind.Mos1N:
            case SymbolKind.Mos1P:
            {
                var m = Mos();
                m["Lambda"] = 0.03;
                if (kind == SymbolKind.Mos1P) m["Vto"] = -0.72;
                return m;
            }

            case SymbolKind.Mos3N:
            case SymbolKind.Mos3P:
            {
                var m = Mos();
                // Each of the six turns on exactly one mechanism, and each must be non-zero here or
                // the perturbation has nothing to move.
                m["Eta"] = 0.06; m["Theta"] = 0.08; m["Kappa"] = 0.5;
                m["Vmax"] = 2.2e5; m["Delta"] = 0.7; m["Xj"] = 0.2e-6;
                if (kind == SymbolKind.Mos3P) m["Vto"] = -0.72;
                return m;
            }

            case SymbolKind.VdmosN:
            case SymbolKind.VdmosP:
            {
                var m = new Dictionary<string, double>
                {
                    ["Vto"] = 3.2, ["Kp"] = 12.0, ["Lambda"] = 0.01, ["Rds"] = 5e6,
                    ["Is"] = 5e-13, ["N"] = 1.05,
                    // Nbv is only live below −Bv, which is why the probe's bias grid goes to +90 V.
                    ["Bv"] = 60.0, ["Ibv"] = 1.2e-3, ["Nbv"] = 1.3,
                    ["Tt"] = 8e-8, ["Cjo"] = 9e-10, ["Vj"] = 0.86, ["Mj"] = 0.46, ["Fc"] = 0.55,
                    ["Cgs"] = 1.8e-9,
                    // Cgdmin BELOW Cgdmax, or the model reads the pair as a constant capacitance and
                    // Vgdt goes inert with them.
                    ["Cgdmax"] = 1.5e-9, ["Cgdmin"] = 2.5e-11, ["Vgdt"] = 1.4,
                    ["Rg"] = 2.5, ["Rd"] = 0.03, ["Rs"] = 0.012,
                    ["Vtotc"] = -6e-3, ["Kptc"] = -0.4,
                    ["Xti"] = 3.2, ["Eg"] = 1.14, ["Temp"] = 85.0, ["Tnom"] = 25.0,
                };
                if (kind == SymbolKind.VdmosP) m["Vto"] = -3.2;
                return m;
            }

            case SymbolKind.JfetN:
            case SymbolKind.JfetP:
            {
                var m = new Dictionary<string, double>
                {
                    ["Vto"] = -2.0, ["Beta"] = 1.2e-3, ["Lambda"] = 0.03,
                    ["Is"] = 2e-14, ["N"] = 1.05,
                    // Recombination is off at Isr = 0, so a zero here would make Nr inert with it.
                    ["Isr"] = 5e-13, ["Nr"] = 1.9,
                    ["Cgs"] = 4e-12, ["Cgd"] = 1.5e-12, ["Pb"] = 0.9, ["M"] = 0.45, ["Fc"] = 0.55,
                    ["Rd"] = 8.0, ["Rs"] = 6.0, ["Area"] = 1.5,
                    ["Xti"] = 3.2, ["Eg"] = 1.14, ["Vtotc"] = 2e-3, ["Betatce"] = -0.5,
                    ["Temp"] = 85.0, ["Tnom"] = 25.0,
                };
                if (kind == SymbolKind.JfetP) m["Vto"] = 2.0;
                return m;
            }

            case SymbolKind.IgbtN:
            case SymbolKind.IgbtP:
            {
                var m = new Dictionary<string, double>
                {
                    ["Vto"] = 5.4, ["Kp"] = 9.0, ["Lambda"] = 0.005, ["Bf"] = 0.6,
                    ["Is"] = 2e-12, ["N"] = 1.05, ["Tau"] = 1.2e-6,
                    ["Rbe"] = 2e5, ["Rce"] = 5e6,
                    ["Bv"] = 700.0, ["Ibv"] = 1.2e-3, ["Nbv"] = 1.3,
                    ["Cjc"] = 3e-10, ["Vj"] = 0.86, ["Mj"] = 0.46, ["Fc"] = 0.55,
                    ["Cge"] = 2.5e-9, ["Cgcmax"] = 1.2e-9, ["Cgcmin"] = 3e-11, ["Vgct"] = 1.4,
                    ["Rg"] = 3.0, ["Rc"] = 0.02, ["Re"] = 0.01,
                    ["Vtotc"] = -8e-3, ["Kptc"] = -0.4,
                    ["Xti"] = 3.2, ["Eg"] = 1.14, ["Temp"] = 85.0, ["Tnom"] = 25.0,
                };
                if (kind == SymbolKind.IgbtP) m["Vto"] = -5.4;
                return m;
            }

            // The three p-channel MESFET laws take their n-channel counterpart's table with every
            // threshold-like value negated, which is what a p-channel card states.
            case SymbolKind.PFetCurtice:
            case SymbolKind.PFetStatz:
            case SymbolKind.PFetMaterka:
            {
                var n = kind switch
                {
                    SymbolKind.PFetCurtice => SymbolKind.FetCurtice,
                    SymbolKind.PFetStatz   => SymbolKind.FetStatz,
                    _                      => SymbolKind.FetMaterka,
                };
                var m = DevicePaletteWiringTests.ActivationFor(n);
                if (m.ContainsKey("Vto")) m["Vto"] = -m["Vto"];
                if (m.ContainsKey("Vp0")) m["Vp0"] = -m["Vp0"];
                return m;
            }

            case SymbolKind.Bead:
                return new() { ["Rdc"] = 0.05, ["L"] = 2.5e-7, ["Rp"] = 600.0, ["Cp"] = 8e-13 };

            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    /// <summary>
    /// A behaviour fingerprint: currents, charges and both Jacobians, over a bias grid chosen per
    /// family so every branch of every model is actually visited. Wide on purpose — a parameter that
    /// moves only the charge, or only the reverse branch, must still register.
    /// </summary>
    private static double[] Probe(SymbolKind kind, ComponentModel m)
    {
        double[][] biases = kind switch
        {
            // Eight ports: Vds, Vbs, Vbd, Vgs, Vgd, Vgb, then the two ohmic drops. Cutoff, linear,
            // saturation, a reversed drain (which exercises the drain/source swap) and a bulk driven
            // hard enough to reach the depletion charge's forward branch.
            SymbolKind.Mos1N or SymbolKind.Mos1P or SymbolKind.Mos3N or SymbolKind.Mos3P =>
                MosBiases(m is Core.Devices.Mos.MosfetModelBase mb && mb.IsNChannel),

            // Seven ports: Vds, Vsd, Vgs, Vgd, then Rg, Rd, Rs. Off and blocking, on and linear,
            // third-quadrant with the gate off (the body diode) and past the avalanche rating.
            SymbolKind.VdmosN or SymbolKind.VdmosP =>
                VdmosBiases(m is Core.Devices.Mos.VdmosModel vd && vd.IsNChannel),

            // Five ports: Vds, Vgs, Vgd, then Rd, Rs.
            SymbolKind.JfetN or SymbolKind.JfetP =>
                JfetBiases(m is Core.Devices.Jfet.JfetModel j && j.IsNChannel),

            // Eight ports: Vbe, Veb, Vge, Vgb, Vce, then Rg, Rc, Re.
            SymbolKind.IgbtN or SymbolKind.IgbtP =>
                IgbtBiases(m is Core.Devices.Igbt.IgbtModel g && g.IsNChannel),

            // The p-channel MESFETs are two-port, in their own polarity's forward sense.
            _ => [[1.5, -1.0], [0.5, -3.0], [-0.3, -5.0], [-0.1, -0.4], [-0.8, -0.2]],
        };

        var probe = new List<double>();
        foreach (var v in biases)
        {
            var r = m.Evaluate(new PortVoltages(v));
            probe.AddRange(r.I);
            probe.AddRange(r.Q);
            for (int i = 0; i < r.Dg.GetLength(0); i++)
                for (int j = 0; j < r.Dg.GetLength(1); j++)
                { probe.Add(r.Dg[i, j]); probe.Add(r.Dc[i, j]); }
        }
        return [.. probe];
    }

    private static double[][] MosBiases(bool n)
    {
        double s = n ? 1.0 : -1.0;
        // (vd, vg, vs, vb) in the device's own polarity, then the two ohmic drops.
        (double D, double G, double S, double B)[] t =
        [
            (3.0, 2.5, 0.0, 0.0),      // saturation
            (0.15, 2.5, 0.0, -2.0),    // linear, with a back bias
            (3.0, 0.2, 0.0, 0.0),      // cutoff
            (-1.0, 2.5, 0.0, -2.0),    // reversed drain: the drain/source swap
            (0.5, 3.0, 0.0, 0.6),      // bulk forward, past Fc·Pb on the source junction
        ];
        double[] ohm = [0.02, 0.01];
        return [.. t.Select(x =>
        {
            double d = s * x.D, g = s * x.G, so = s * x.S, b = s * x.B;
            return new[] { d - so, b - so, b - d, g - so, g - d, g - b, s * ohm[0], s * ohm[1] };
        })];
    }

    private static double[][] VdmosBiases(bool n)
    {
        double s = n ? 1.0 : -1.0;
        (double D, double G)[] t =
        [
            (12.0, 10.0),   // on, linear
            (40.0, 0.0),    // off, blocking
            (-0.9, 0.0),    // third quadrant, gate off: the body diode
            (-0.15, 10.0),  // third quadrant, gate on: the channel
            (90.0, 0.0),    // past the avalanche rating
        ];
        return [.. t.Select(x =>
        {
            double d = s * x.D, g = s * x.G;
            return new[] { d, -d, g, g - d, s * 0.3, s * 0.05, s * 0.01 };
        })];
    }

    private static double[][] JfetBiases(bool n)
    {
        double s = n ? 1.0 : -1.0;
        (double D, double G)[] t =
        [
            (4.0, -0.5),   // saturation
            (0.3, -0.5),   // linear
            (4.0, -3.0),   // pinched off
            (-1.2, -0.5),  // reversed drain
            (2.0, 0.7),    // gate junction forward, past Fc·Pb
        ];
        return [.. t.Select(x =>
        {
            double d = s * x.D, g = s * x.G;
            return new[] { d, g, g - d, s * 0.05, s * 0.02 };
        })];
    }

    private static double[][] IgbtBiases(bool n)
    {
        double s = n ? 1.0 : -1.0;
        // (vc, vg, vb) — the internal base node placed a junction drop below the collector, where a
        // real solve would put it, and also away from there.
        (double C, double G, double B)[] t =
        [
            (15.0, 15.0, 14.25),
            (300.0, 15.0, 299.3),
            (15.0, 0.0, 0.2),        // gate off
            (-2.0, 15.0, -1.0),      // reverse: the bipolar blocks
            (900.0, 0.0, 800.0),     // blocking past the break-over rating: Vbe > Bv
        ];
        return [.. t.Select(x =>
        {
            double c = s * x.C, g = s * x.G, b = s * x.B;
            return new[] { b, c - b, g, g - b, c, s * 0.3, s * 0.02, s * 0.01 };
        })];
    }

    private static ComponentModel Build(SymbolKind kind, IReadOnlyDictionary<string, double> pars)
    {
        var vals = pars.ToDictionary(kv => kv.Key, kv => new Value(kv.Value));
        var m = ComponentModelFactory.TryCreate(ComponentTypeRegistry.EngineReference(kind), vals);
        Assert.True(m is not null, $"factory did not build {kind}");
        return m!;
    }

    /// <summary>
    /// Parameters that are read ONLY when another is absent, and which one to clear before probing
    /// them. Several quantities can be stated as a device value or as a process value, and the model
    /// derives one from the other only where the first is missing — so with both present, the
    /// fallback is genuinely inert and perturbing it proves nothing.
    ///
    /// <para><b>This is a real property of the models, not a testing convenience</b>, and the two
    /// directions are not the same: an absolute <c>Cbd</c>/<c>Cbs</c> WINS over <c>Cj</c> times an
    /// area, while a current DENSITY <c>Js</c> times an area wins over an absolute <c>Is</c>. Those
    /// are the published rules and they genuinely differ. Writing this table is what found the
    /// capacitance pair reading backwards, which had made an explicitly stated <c>Cbd</c> silently
    /// inert on any card that also carried <c>Cj</c>.</para>
    /// </summary>
    private static readonly Dictionary<string, string[]> ReadOnlyWhenAbsent = new()
    {
        ["Uo"]   = ["Kp"],                    // Kp = Uo x Cox, derived only when Kp is not stated
        ["Nsub"] = ["Gamma", "Phi"],          // …and Gamma/Phi are derived from the doping likewise
        ["Rsh"]  = ["Rd", "Rs"],              // sheet resistance x squares, only without Rd/Rs
        ["Nrd"]  = ["Rd", "Rs"],
        ["Nrs"]  = ["Rd", "Rs"],
        ["Cj"]   = ["Cbd", "Cbs"],            // the absolute capacitance wins over the process one…
        ["Is"]   = ["Js"],                    // …and the current DENSITY wins over the absolute one
    };

    [Theory]
    [InlineData(SymbolKind.Mos1N)]
    [InlineData(SymbolKind.Mos1P)]
    [InlineData(SymbolKind.Mos3N)]
    [InlineData(SymbolKind.Mos3P)]
    [InlineData(SymbolKind.VdmosN)]
    [InlineData(SymbolKind.VdmosP)]
    [InlineData(SymbolKind.JfetN)]
    [InlineData(SymbolKind.JfetP)]
    [InlineData(SymbolKind.IgbtN)]
    [InlineData(SymbolKind.IgbtP)]
    [InlineData(SymbolKind.PFetCurtice)]
    [InlineData(SymbolKind.PFetStatz)]
    [InlineData(SymbolKind.PFetMaterka)]
    public void W4_EveryRegistryParameterNameActuallyReachesTheModel(SymbolKind kind)
    {
        var registryNames = ComponentTypeRegistry.DefaultParameters(kind, 0).Select(p => p.Name).ToList();
        var activation = Activation(kind);

        // The two name sets must agree. A parameter added to the registry and not here (or the
        // reverse) means one was updated alone — which is the drift this test exists to prevent.
        Assert.Equal(registryNames.OrderBy(n => n, StringComparer.Ordinal),
                     activation.Keys.OrderBy(n => n, StringComparer.Ordinal));

        foreach (var name in registryNames)
        {
            // A parameter that is only read when another is absent has to be probed with that other
            // one cleared, or it is structurally inert and the perturbation proves nothing.
            var start = new Dictionary<string, double>(activation);
            if (ReadOnlyWhenAbsent.TryGetValue(name, out var dominant))
                foreach (var dom in dominant)
                    if (start.ContainsKey(dom)) start[dom] = 0.0;

            var baseline = Probe(kind, Build(kind, start));
            var perturbed = new Dictionary<string, double>(start) { [name] = start[name] * 1.6 };

            var moved = Probe(kind, Build(kind, perturbed));

            // EXACT inequality, with no tolerance at all. If a parameter is genuinely not read, the
            // two probes take the same code path on the same inputs and are bit-identical, so any
            // difference whatever is real and there is nothing to filter out.
            //
            // A tolerance here is actively harmful: gate overlap charges are of order 1e-14 C, so an
            // absolute floor anywhere near that reads a fully-wired Cgso as unwired. That is what a
            // first draft of this test did.
            bool changed = baseline.Zip(moved).Any(p => p.First != p.Second);

            Assert.True(changed,
                $"{kind}: parameter '{name}' is offered in the palette but changes nothing — " +
                "the factory does not read that name.");
        }
    }

    /// <summary>
    /// The bead is LINEAR, so it has no <c>Evaluate</c> to fingerprint. Its behaviour is what it
    /// stamps, so that is what is probed — through a recorder standing in for the engine's matrix.
    /// </summary>
    [Fact]
    public void W4b_EveryBeadParameterNameActuallyReachesTheModel()
    {
        var registryNames = ComponentTypeRegistry.DefaultParameters(SymbolKind.Bead, 0)
                                                 .Select(p => p.Name).ToList();
        var activation = Activation(SymbolKind.Bead);
        Assert.Equal(registryNames.OrderBy(n => n, StringComparer.Ordinal),
                     activation.Keys.OrderBy(n => n, StringComparer.Ordinal));

        Complex[] Stamped(IReadOnlyDictionary<string, double> pars)
        {
            var m = ComponentModelFactory.TryCreate("Bead", new Dictionary<string, Value>());
            Assert.NotNull(m);
            var ec = new ElaboratedComponent("Bead", "FB1", [1, 2],
                pars.ToDictionary(kv => kv.Key, kv => new Value(kv.Value)), m!);

            var seen = new List<Complex>();
            // Across the whole shape of the response: DC, well below resonance, at it, and above.
            foreach (double f in new[] { 0.0, 1e6, 100e6, 356e6, 3e9 })
            {
                var rec = new Recorder();
                ec.Stamp(rec, 2 * Math.PI * f);
                seen.AddRange(rec.Diagonal);
            }
            return [.. seen];
        }

        var baseline = Stamped(activation);
        foreach (var name in registryNames)
        {
            var perturbed = new Dictionary<string, double>(activation) { [name] = activation[name] * 1.6 };
            bool changed = baseline.Zip(Stamped(perturbed)).Any(p => p.First != p.Second);
            Assert.True(changed,
                $"Bead: parameter '{name}' is offered in the palette but changes nothing stamped.");
        }
    }

    /// <summary>The only part of the matrix a one-port impedance writes anything interesting into.</summary>
    private sealed class Recorder : IMnaContext
    {
        public List<Complex> Diagonal { get; } = [];
        private int _branches;

        public int AddBranch() => _branches++;
        public void AddBranchConstraint(int branch, int otherBranch, Complex coeff) => Diagonal.Add(coeff);
        public void AddAdmittance(int a, int b, Complex y) => Diagonal.Add(y);
        public void AddBlockAdmittance(int r, int c, Complex y) => Diagonal.Add(y);
        public void AddBranchCurrent(int branch, int from, int to) { }
        public void AddConstraint(int branch, int node, Complex coeff) { }
        public void AddNodeBranchCoupling(int node, int branch, Complex coeff) { }
        public void AddCurrentInjection(int node, Complex j) => Diagonal.Add(j);
        public void AddSourceValue(int branch, Complex value) => Diagonal.Add(value);
    }

    // ── W5 ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SymbolKind.Mos1N)]
    [InlineData(SymbolKind.Mos1P)]
    [InlineData(SymbolKind.Mos3N)]
    [InlineData(SymbolKind.Mos3P)]
    [InlineData(SymbolKind.VdmosN)]
    [InlineData(SymbolKind.VdmosP)]
    [InlineData(SymbolKind.JfetN)]
    [InlineData(SymbolKind.JfetP)]
    [InlineData(SymbolKind.IgbtN)]
    [InlineData(SymbolKind.IgbtP)]
    [InlineData(SymbolKind.PFetCurtice)]
    [InlineData(SymbolKind.PFetStatz)]
    [InlineData(SymbolKind.PFetMaterka)]
    public void W5_AFreshlyPlacedDeviceIsAWorkingDevice(SymbolKind kind)
    {
        // Straight off the palette, with nothing edited: the defaults must parse as numbers, build,
        // and evaluate finitely. A default that is merely a placeholder gives a broken part on the
        // first drag.
        var defaults = ComponentTypeRegistry.DefaultParameters(kind, 0)
            .ToDictionary(p => p.Name, p => double.Parse(p.Expression, CultureInfo.InvariantCulture));

        var m = Build(kind, defaults);
        Assert.Equal(ModelKind.Nonlinear, m.Kind);

        foreach (var x in Probe(kind, m))
            Assert.True(double.IsFinite(x), $"{kind}: default parameters give a non-finite result");
    }
}
