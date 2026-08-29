using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Expressions;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Palette wiring for the built-in semiconductor devices: the diode, the five FET laws, and the
/// two bipolar polarities.
///
/// <para><b>The load-bearing test here is P4</b> — every parameter name the registry offers the
/// user is proven to REACH the model, by perturbing it and requiring the device's behaviour to
/// move. A registry name that the factory does not read compiles, saves, loads, appears in the
/// parameter dialog, accepts an edit, and then does nothing at all: the user sets Beta and the
/// simulation ignores it. Nothing else in the codebase catches that, because the two lists are
/// plain strings on opposite sides of a dictionary lookup.</para>
///
///   P1 — every new kind is a placeable palette item, in the Devices category.
///   P2 — geometry: pin count, pin identity, and the FET's source being an ordinary pin.
///   P3 — each FET tile places a DIFFERENT engine component; the diode places "Diode".
///   P4 — every registry parameter name is read by the factory (see above), and the registry's
///        name set matches this test's activation table exactly, so a name added on one side
///        without the other fails loudly rather than silently.
///   P5 — a freshly placed device is a WORKING device: the defaults build and evaluate.
/// </summary>
public class DevicePaletteWiringTests
{
    private static readonly SymbolKind[] Fets =
    [
        SymbolKind.FetCurtice, SymbolKind.FetCurticeCubic,
        SymbolKind.FetStatz, SymbolKind.FetMaterka, SymbolKind.FetAngelov,
    ];

    /// <summary>The two bipolar tiles. Unlike the FET laws these share one parameter list and one
    /// set of equations — see <see cref="SymbolKind.BjtNpn"/> for why they are still two kinds.</summary>
    private static readonly SymbolKind[] Bjts = [SymbolKind.BjtNpn, SymbolKind.BjtPnp];

    private static SymbolKind[] AllDevices() => [SymbolKind.Diode, .. Fets, .. Bjts];

    // ── P1 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void P1_EveryDeviceIsAPlaceablePaletteItemUnderDevices()
    {
        var items = LibraryCatalog.AllItems;
        foreach (var kind in AllDevices())
        {
            var item = items.FirstOrDefault(i => i.Kind == kind);
            Assert.True(item is not null, $"{kind} is missing from the palette catalog");
            Assert.Equal(ComponentCategory.Devices, item!.Category);
        }

        // The category filter is what the palette's ComboBox actually calls.
        var devices = LibraryCatalog.ByCategory(ComponentCategory.Devices).Select(i => i.Kind).ToList();
        foreach (var kind in AllDevices())
            Assert.Contains(kind, devices);

        // Findable by the words a user would type, not only by exact display name.
        foreach (var q in new[] { "FET", "MESFET", "transistor" })
            Assert.True(LibraryCatalog.Search(q).Count(i => Fets.Contains(i.Kind)) == Fets.Length,
                $"search '{q}' must reach all five FET laws");
        Assert.Contains(LibraryCatalog.Search("diode"), i => i.Kind == SymbolKind.Diode);

        // Either polarity's name finds BOTH bipolar tiles — somebody who types "PNP" is looking
        // for the pair, and the two sit next to each other in the palette.
        foreach (var q in new[] { "BJT", "bipolar", "NPN", "PNP" })
            Assert.True(LibraryCatalog.Search(q).Count(i => Bjts.Contains(i.Kind)) == Bjts.Length,
                $"search '{q}' must reach both bipolar polarities");
    }

    // ── P2 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void P2_Geometry_DiodeIsTwoPin_FetIsThreePinWithARealSource()
    {
        var d = SymbolPortDefs.For(SymbolKind.Diode);
        Assert.Equal(["a", "c"], d.Select(p => p.Name));

        foreach (var kind in Fets)
        {
            var ports = SymbolPortDefs.For(kind);
            // Order IS the contract the elaborator reads: gate, drain, source.
            Assert.Equal(["g", "d", "s"], ports.Select(p => p.Name));

            // The source is a pin at a distinct location — NOT an implicit ground. A common-source-
            // only device would have two pins here, and this is the assertion that says it doesn't.
            Assert.Equal(3, ports.Length);
            Assert.Equal(3, ports.Select(p => (p.LocalX, p.LocalY)).Distinct().Count());
        }

        foreach (var kind in Bjts)
        {
            var ports = SymbolPortDefs.For(kind);
            // Order IS the contract the elaborator reads: collector, base, emitter.
            Assert.Equal(["c", "b", "e"], ports.Select(p => p.Name));
            Assert.Equal(3, ports.Select(p => (p.LocalX, p.LocalY)).Distinct().Count());
        }

        // Every device has a glyph; none falls through to the generic placeholder.
        foreach (var kind in AllDevices())
            Assert.NotEmpty(BuiltInSymbols.Primitives(kind).Primitives);

        // The two bipolar polarities do NOT share a glyph. The emitter arrow is the only cue that
        // separates them on a schematic, so a shared glyph would draw one of them wrongly — this is
        // the assertion that stops a future "they're the same topology" tidy-up.
        Assert.NotEqual(BuiltInSymbols.Primitives(SymbolKind.BjtNpn).Primitives.Count,
                        0);
        Assert.False(ReferenceEquals(BuiltInSymbols.Primitives(SymbolKind.BjtNpn),
                                     BuiltInSymbols.Primitives(SymbolKind.BjtPnp)));
    }

    // ── P3 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void P3_EachTilePlacesItsOwnEngineComponent()
    {
        Assert.Equal("Diode", ComponentTypeRegistry.EngineReference(SymbolKind.Diode));

        var refs = Fets.Select(k => ComponentTypeRegistry.EngineReference(k)).ToList();
        Assert.Equal(refs.Count, refs.Distinct().Count());   // five laws, five components
        foreach (var r in refs)
            Assert.True(ComponentModelFactory.IsPrimitive(r), $"engine reference '{r}' is not a primitive");

        // One law, two components — the polarity is in the NETLIST, not in a parameter that a later
        // edit could leave disagreeing with the symbol on screen.
        Assert.Equal("BJT_NPN", ComponentTypeRegistry.EngineReference(SymbolKind.BjtNpn));
        Assert.Equal("BJT_PNP", ComponentTypeRegistry.EngineReference(SymbolKind.BjtPnp));
        foreach (var k in Bjts)
            Assert.True(ComponentModelFactory.IsPrimitive(ComponentTypeRegistry.EngineReference(k)));
    }

    // ── P4 ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Non-zero, non-default working values for EVERY parameter the registry offers. Non-zero
    /// matters: a parameter left at zero can be structurally inert (Vds0 does nothing when Beta is
    /// zero), and a perturbation test on an inert parameter proves nothing.
    /// </summary>
    private static Dictionary<string, double> Activation(SymbolKind kind)
    {
        // Shared FET block — gate charge, gate conduction, and a genuine temperature rise so the
        // temperature coefficients are live rather than collapsed to the identity.
        var shared = new Dictionary<string, double>
        {
            ["Cgs"] = 3e-13, ["Cgd"] = 1e-13, ["CapModel"] = 2,
            ["Vbi"] = 0.9, ["Mj"] = 0.4, ["Fc"] = 0.6,
            ["Is"] = 1e-14, ["N"] = 1.1, ["Xti"] = 3.0, ["Eg"] = 1.2,
            ["Temp"] = 80.0, ["Tnom"] = 25.0,
        };

        Dictionary<string, double> own = kind switch
        {
            SymbolKind.Diode => new()
            {
                ["Is"] = 2.5e-15, ["N"] = 1.12, ["Rs"] = 12.0, ["Cj0"] = 1.4e-13,
                ["Vj"] = 0.72, ["M"] = 0.33, ["Fc"] = 0.6,
                ["Bv"] = 5.0, ["Ibv"] = 1e-3, ["Tt"] = 1e-11, ["Temp"] = 40.0,
            },
            SymbolKind.FetCurtice => new()
            {
                ["Vto"] = -2.0, ["Beta"] = 0.02, ["Alpha"] = 2.0, ["Lambda"] = 0.05,
                ["Betatc"] = 1.5, ["Alphatc"] = -1.2, ["Vtotc"] = 1e-3,
            },
            SymbolKind.FetCurticeCubic => new()
            {
                ["A0"] = 0.1, ["A1"] = 0.05, ["A2"] = 0.01, ["A3"] = 0.002,
                ["Gamma"] = 2.0, ["Beta"] = 0.05, ["Vds0"] = 5.0, ["Gammatc"] = 1e-3,
            },
            SymbolKind.FetStatz => new()
            {
                ["Vto"] = -2.0, ["Beta"] = 0.02, ["B"] = 0.3, ["Alpha"] = 2.0, ["Lambda"] = 0.05,
                ["Betatc"] = 1.5, ["Alphatc"] = -1.2, ["Vtotc"] = 1e-3,
            },
            SymbolKind.FetMaterka => new()
            {
                ["Idss"] = 0.1, ["Vp0"] = -2.0, ["Gamma"] = 0.05, ["Alpha"] = 2.0,
                ["Alphatc"] = -1.2, ["Gammatc"] = 1e-3, ["Vtotc"] = 1e-3,
            },
            SymbolKind.FetAngelov => new()
            {
                ["Ipk"] = 0.1, ["Vpk"] = -1.0, ["P1"] = 1.0, ["P2"] = 0.2, ["P3"] = 0.05,
                ["Alpha"] = 2.0, ["Lambda"] = 0.05, ["Alphatc"] = -1.2, ["Vtotc"] = 1e-3,
            },
            // One list for both polarities — they ARE one parameter list. Temp is deliberately far
            // from Tnom so Xtb, Xti and Eg are live rather than collapsed to the identity, and Rbm
            // sits below Rb so the base-resistance modulation is switched on.
            SymbolKind.BjtNpn or SymbolKind.BjtPnp => new()
            {
                ["Is"] = 9.57e-17, ["Bf"] = 131.1, ["Nf"] = 1.0,
                ["Vaf"] = 71.02, ["Ikf"] = 0.09745, ["Ise"] = 1.618e-15, ["Ne"] = 1.692,
                ["Br"] = 3.287, ["Nr"] = 0.959, ["Var"] = 4.081, ["Ikr"] = 0.07617,
                ["Isc"] = 5.969e-15, ["Nc"] = 1.974,
                ["Rb"] = 9.72444, ["Irb"] = 3.017e-6, ["Rbm"] = 6.94667,
                ["Re"] = 0.7979, ["Rc"] = 2.089,
                ["Cje"] = 8.287e-14, ["Vje"] = 0.8281, ["Mje"] = 0.7138,
                ["Cjc"] = 8.781e-14, ["Vjc"] = 0.7715, ["Mjc"] = 0.7552,
                ["Xcjc"] = 0.6209, ["Fc"] = 0.6275,
                // Vtf is deliberately NOT the shipped default here. That default is a few
                // millivolts, which makes exp(Vbc/(1.44*Vtf)) a step: it underflows to zero
                // everywhere in forward active and hits the model's transit-time ceiling anywhere
                // in saturation, so at every bias a probe could use the parameter reads as unwired
                // when it is doing exactly what it says. Half a volt puts the term in its own range
                // and is an ordinary value for it.
                ["Tf"] = 1.72653e-11, ["Xtf"] = 0.07, ["Vtf"] = 0.5, ["Itf"] = 0.027024,
                ["Tr"] = 1.71536e-8,
                ["Area"] = 1.0, ["Xti"] = 6.548, ["Xtb"] = 1.303, ["Eg"] = 1.11,
                ["Temp"] = 80.0, ["Tnom"] = 25.0,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        if (kind is SymbolKind.Diode or SymbolKind.BjtNpn or SymbolKind.BjtPnp) return own;
        foreach (var (k, v) in shared) own.TryAdd(k, v);
        return own;
    }

    /// <summary>
    /// A behaviour fingerprint: currents, both Jacobian entries, and charge, at several biases.
    /// Wide on purpose — a parameter that moves only the charge, or only the reverse branch, must
    /// still register.
    /// </summary>
    private static double[] Probe(CircuitRF.Core.ComponentModel m)
    {
        var vs = m is BjtModel bjt
            // Seven ports: the four intrinsic ones then Rc, Rb, Re — see BjtModel's port indices.
            // Forward active, then SATURATION (both junctions forward, which is the only place the
            // reverse parameters are anything but a rounding error), then sub-threshold and cut-off.
            // A grid without the saturation row makes Br, Nr, Ikr, Isc and Nc all look unwired.
            //
            // MIRRORED for a p-n-p, and that is not cosmetic: the same node voltages leave it
            // reverse-active, where Tf and everything else that lives in forward conduction reads
            // as an unwired parameter.
            ? BjtBiases(bjt.IsNpn)
            : m is DiodeModel { HasSeriesResistance: true }
            ? new[] { new[] { 0.1, 0.3 }, [0.1, 0.6], [0.1, -1.0], [0.1, -6.0] }
            : m is DiodeModel
                ? [[0.3], [0.6], [-1.0], [-6.0]]
                // The last entry drives the gate ABOVE Fc·Vbi on both junctions. Without it the
                // depletion charge never leaves its normal branch and Fc looks inert — which reads
                // exactly like an unwired parameter. Probe coverage, not the model, was the gap.
                : [[-1.5, 1.0], [-0.5, 3.0], [0.3, 5.0], [0.1, 0.4], [0.8, 0.2]];

        var probe = new List<double>();
        foreach (var v in vs)
        {
            var r = m.Evaluate(new PortVoltages(v));
            probe.AddRange(r.I);
            probe.AddRange(r.Q);
            for (int i = 0; i < r.Dg.GetLength(0); i++)
                for (int j = 0; j < r.Dg.GetLength(1); j++)
                { probe.Add(r.Dg[i, j]); probe.Add(r.Dc[i, j]); }
        }
        return probe.ToArray();
    }

    /// <summary>The bipolar bias grid, in each polarity's own forward sense. The three ohmic ports
    /// keep their sign either way — a resistor has no polarity.</summary>
    private static double[][] BjtBiases(bool npn)
    {
        double s = npn ? 1.0 : -1.0;
        double[][] junction =
        [
            [0.75, -3.0, 3.75, -3.0],
            [0.85,  0.70, 0.15,  0.70],
            [0.30, -1.0,  1.30, -1.0],
            [-0.5, -5.0,  4.50, -5.0],
        ];
        double[][] ohmic =
        [
            [0.20, 0.004, 0.10],
            [0.30, 0.006, 0.15],
            [0.01, 0.001, 0.01],
            [0.00, 0.000, 0.00],
        ];
        return junction.Zip(ohmic, (j, o) => j.Select(x => s * x).Concat(o).ToArray()).ToArray();
    }

    private static CircuitRF.Core.ComponentModel Build(SymbolKind kind, IReadOnlyDictionary<string, double> pars)
    {
        var vals = pars.ToDictionary(kv => kv.Key, kv => new Value(kv.Value));
        var m = ComponentModelFactory.TryCreate(ComponentTypeRegistry.EngineReference(kind), vals);
        Assert.True(m is not null, $"factory did not build {kind}");
        return m!;
    }

    [Theory]
    [InlineData(SymbolKind.Diode)]
    [InlineData(SymbolKind.FetCurtice)]
    [InlineData(SymbolKind.FetCurticeCubic)]
    [InlineData(SymbolKind.FetStatz)]
    [InlineData(SymbolKind.FetMaterka)]
    [InlineData(SymbolKind.FetAngelov)]
    [InlineData(SymbolKind.BjtNpn)]
    [InlineData(SymbolKind.BjtPnp)]
    public void P4_EveryRegistryParameterNameActuallyReachesTheModel(SymbolKind kind)
    {
        var registryNames = ComponentTypeRegistry.DefaultParameters(kind, 0)
                                                 .Select(p => p.Name).ToList();
        var activation = Activation(kind);

        // The two name sets must agree. A parameter added to the registry and not here (or the
        // reverse) means one of the two was updated alone — which is exactly the drift this whole
        // test exists to prevent, so it is a failure, not something to skip over.
        Assert.Equal(registryNames.OrderBy(n => n, StringComparer.Ordinal),
                     activation.Keys.OrderBy(n => n, StringComparer.Ordinal));

        var baseline = Probe(Build(kind, activation));

        foreach (var name in registryNames)
        {
            var perturbed = new Dictionary<string, double>(activation);
            // CapModel selects a scheme rather than scaling — 2 → 1 swaps junction charge for
            // constant charge. Scaling it would land on an unimplemented value.
            perturbed[name] = name == "CapModel" ? 1.0 : activation[name] * 1.6;

            var moved = Probe(Build(kind, perturbed));
            bool changed = baseline.Zip(moved).Any(p =>
                Math.Abs(p.First - p.Second) > 1e-14 * Math.Max(1.0, Math.Abs(p.First)));

            Assert.True(changed,
                $"{kind}: parameter '{name}' is offered in the palette but changes nothing — " +
                "the factory does not read that name.");
        }
    }

    // ── P5 ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SymbolKind.Diode)]
    [InlineData(SymbolKind.FetCurtice)]
    [InlineData(SymbolKind.FetCurticeCubic)]
    [InlineData(SymbolKind.FetStatz)]
    [InlineData(SymbolKind.FetMaterka)]
    [InlineData(SymbolKind.BjtNpn)]
    [InlineData(SymbolKind.BjtPnp)]
    [InlineData(SymbolKind.FetAngelov)]
    public void P5_AFreshlyPlacedDeviceIsAWorkingDevice(SymbolKind kind)
    {
        // Straight off the palette, with nothing edited: the defaults must parse as numbers, build,
        // and evaluate to finite values. A default that is merely a placeholder gives the user a
        // broken part on first drag.
        var defaults = ComponentTypeRegistry.DefaultParameters(kind, 0)
            .ToDictionary(p => p.Name, p => double.Parse(p.Expression,
                                                         System.Globalization.CultureInfo.InvariantCulture));

        var m = Build(kind, defaults);
        Assert.Equal(ModelKind.Nonlinear, m.Kind);

        foreach (var x in Probe(m))
            Assert.True(double.IsFinite(x), $"{kind}: default parameters give a non-finite result");

        // And the defaults must actually conduct somewhere — a device that is off at every bias
        // would pass every finiteness check and still be useless.
        if (kind is SymbolKind.BjtNpn or SymbolKind.BjtPnp)
        {
            // Forward active in the device's OWN polarity: an n-p-n and a p-n-p biased with the
            // same raw voltages are not the same operating point, and only one of them is on.
            double s = ((BjtModel)m).IsNpn ? 1.0 : -1.0;
            var on = m.Evaluate(new PortVoltages(
                [s * 0.80, s * -3.0, s * 3.80, s * -3.0, 0.0, 0.0, 0.0]));
            Assert.True(Math.Abs(on.I[2]) > 1e-6, $"{kind}: default device draws no collector current");
            Assert.True(Math.Abs(on.I[2]) > 20.0 * Math.Abs(on.I[0]),
                $"{kind}: default device has no usable current gain");
        }
        else if (kind != SymbolKind.Diode)
        {
            var on = m.Evaluate(new PortVoltages([-0.5, 3.0]));
            Assert.True(Math.Abs(on.I[1]) > 1e-6, $"{kind}: default device draws no drain current");
        }
    }

    // ── P6 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void P6_Sdd1AndSdd2_ListUnderDevices_Sdd3AndPlainSddDoNot()
    {
        var devices = LibraryCatalog.ByCategory(ComponentCategory.Devices);

        // SDD1 and SDD2 — and only those two — are reachable from the Devices filter.
        // A hand-built SDD carrying device equations IS how a user writes their own 1- or 2-port
        // nonlinear device, so it belongs beside the built-in diode and FETs.
        Assert.Contains(devices, i => i.Kind == SymbolKind.Sdd && i.PortCount == 1);
        Assert.Contains(devices, i => i.Kind == SymbolKind.Sdd && i.PortCount == 2);
        Assert.DoesNotContain(devices, i => i.Kind == SymbolKind.Sdd && i.PortCount == 3);
        Assert.DoesNotContain(devices, i => i.Kind == SymbolKind.Sdd && i.PortCount == 0);

        // They are the SAME kind and the SAME engine component as every other SDD entry point —
        // a filter keyword, never a parallel component. Their own primary category is unchanged,
        // so AllItems still lists each exactly once.
        foreach (var n in new[] { 1, 2 })
        {
            var item = devices.Single(i => i.Kind == SymbolKind.Sdd && i.PortCount == n);
            Assert.Equal($"SDD{n}", item.DisplayName);
            Assert.NotEqual(ComponentCategory.Devices, item.Category);
            Assert.Single(LibraryCatalog.AllItems, i => i.Kind == SymbolKind.Sdd && i.PortCount == n);
        }

        // The plain SDD tile and SDD3 keep their own category untouched.
        Assert.Single(LibraryCatalog.AllItems, i => i.Kind == SymbolKind.Sdd && i.PortCount == 0);
        Assert.Single(LibraryCatalog.AllItems, i => i.Kind == SymbolKind.Sdd && i.PortCount == 3);
    }

    [Fact]
    public void P6_CrossTechnologyPasteDefault_IsUnaffected()
    {
        // The Devices keyword rides on the port-count ENTRY POINT, not on the shared registry
        // entry — so nothing about SDD outside the palette moved. Pinned because the two are one
        // edit apart and a future "tidy-up" could easily push this onto the registry instead.
        var info = ComponentTypeRegistry.Get(SymbolKind.Sdd);
        Assert.NotEqual(ComponentCategory.Devices, info.Category);
        Assert.True(info.ExtraCategories is null ||
                    !info.ExtraCategories.Contains(ComponentCategory.Devices));
    }
}
