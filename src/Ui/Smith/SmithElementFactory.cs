using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using CircuitRF.Design.Smith;
using CircuitRF.Ui.Matching;

namespace CircuitRF.Ui.Smith;

/// <summary>
/// What a freshly placed element is (<c>brief-smith-6-network-strip.md</c> <c>R-smith6-3</c>).
/// </summary>
/// <remarks>
/// <b>There is no second set of component defaults.</b> Every number here is read out of
/// <c>ComponentTypeRegistry.DefaultParameters</c> — the same table a component placed on a schematic
/// page is seeded from — so a 1 pF capacitor is 1 pF in both places and stays that way if the registry
/// is ever re-tuned. What this file adds is the two things the registry cannot answer:
///
/// <list type="bullet">
///   <item><b>A TLIN's <c>F_ref</c> is the DESIGN frequency at the moment of placement</b>, not the
///   registry's 1 GHz — and thereafter it does not follow it (<c>R-smith2-3</c>). A line whose
///   reference frequency moved whenever the chart was retuned would be a different physical line each
///   time, and the load points would stop meaning anything.</item>
///   <item><b>The instance name</b>, which the registry supplies a PREFIX for and nothing more.</item>
/// </list>
///
/// <para><b>The registry's units are display units and this converts them.</b> The document is base SI
/// (<c>L</c> = 1e-9, not 1) — with the one named exception of an electrical length, which is stored in
/// DEGREES and therefore must NOT go through <see cref="MatchValueFormat.Scale"/>: that resolves
/// <c>deg</c> to π/180, which is right for the expression engine and wrong by a factor of 57 here.</para>
/// </remarks>
public static class SmithElementFactory
{
    /// <summary>
    /// A new element of <paramref name="kind"/>, named so as not to collide with
    /// <paramref name="existingNames"/>.
    /// </summary>
    /// <param name="designFrequencyHz">What a line's <c>F_ref</c> is born at.</param>
    public static SmithElement Create(
        SmithElementKind kind, SmithPlacement placement, double designFrequencyHz,
        IEnumerable<string> existingNames)
    {
        var binding = SmithComponentMap.Component(kind);

        var e = new SmithElement
        {
            Kind            = kind,
            Placement       = SmithComponentMap.AllowedPlacement(kind) ?? placement,
            Name            = NextName(kind, existingNames),
            Enabled         = true,
            ActiveParameter = SmithComponentMap.DefaultParameter(kind),
        };

        var v = e.Values;

        v.ROhm   = Registry(binding.SymbolKind, binding.NumPorts, "R") ?? 0.0;
        v.LHenry = Registry(binding.SymbolKind, binding.NumPorts, "L") ?? 0.0;
        v.CFarad = Registry(binding.SymbolKind, binding.NumPorts, "C") ?? 0.0;

        if (SmithComponentMap.IsLine(kind))
        {
            v.Z0Ohm = Registry(binding.SymbolKind, binding.NumPorts, "Z") ?? 50.0;

            // DEGREES, unscaled — see this type's own remarks.
            v.ElectricalLengthDeg = Raw(binding.SymbolKind, binding.NumPorts, "E") ?? 90.0;

            // The registry's own F is 1 GHz; this is the one default the tool overrides, and
            // R-smith2-3 is why.
            v.ReferenceFrequencyHz = designFrequencyHz > 0 ? designFrequencyHz
                                   : Registry(binding.SymbolKind, binding.NumPorts, "F") ?? 1e9;
        }

        if (kind == SmithElementKind.Z1P)
            v.ImpedanceOhm = new Complex(Registry(SymbolKind.ZPort, 1, "Z[1,1]") ?? 50.0, 0.0);

        return e;
    }

    /// <summary>
    /// The lowest unused instance name for <paramref name="kind"/> — <c>L1</c>, <c>L2</c>, <c>TL1</c>.
    /// </summary>
    /// <remarks>
    /// <b>The prefix is <c>ComponentTypeRegistry.InstancePrefix</c>'s</b>, which is why an open stub and
    /// a shorted stub and a TLIN all number in one <c>TL</c> series: they are one component with its far
    /// end wired three ways, and giving them separate series here would invent a distinction the
    /// registry does not make and brief 7 could not carry into a schematic.
    ///
    /// <para>Lowest unused rather than highest-plus-one, so deleting <c>L1</c> and adding an inductor
    /// gives <c>L1</c> back instead of walking the numbers up forever.</para>
    /// </remarks>
    public static string NextName(SmithElementKind kind, IEnumerable<string> existingNames)
    {
        string prefix = ComponentTypeRegistry.InstancePrefix(SmithComponentMap.Component(kind).SymbolKind);
        var used = new HashSet<string>(existingNames ?? [], StringComparer.OrdinalIgnoreCase);

        for (int n = 1; ; n++)
        {
            string candidate = prefix + n.ToString(CultureInfo.InvariantCulture);
            if (used.Add(candidate)) return candidate;
        }
    }

    // ── Reading the registry ─────────────────────────────────────────────────

    /// <summary>The registry's default for one parameter, converted to base SI, or null when the
    /// component has no such parameter.</summary>
    private static double? Registry(SymbolKind symbol, int portCount, string name)
    {
        if (Raw(symbol, portCount, name) is not { } raw) return null;

        var p = ComponentTypeRegistry.DefaultParameters(symbol, portCount)
                                     .First(d => d.Name == name);
        return raw * MatchValueFormat.Scale(p.Unit);
    }

    /// <summary>The registry's default for one parameter as WRITTEN, with no unit applied.</summary>
    private static double? Raw(SymbolKind symbol, int portCount, string name)
    {
        foreach (var d in ComponentTypeRegistry.DefaultParameters(symbol, portCount))
            if (d.Name == name
             && double.TryParse(d.Expression, NumberStyles.Float, CultureInfo.InvariantCulture,
                                out double value))
                return value;
        return null;
    }
}
