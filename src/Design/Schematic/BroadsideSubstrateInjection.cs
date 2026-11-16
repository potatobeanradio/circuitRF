using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.PCells;

namespace CircuitRF.Design.Schematic;

/// <summary>
/// The broadside-coupled pairs' side of the substrate seam: what <see cref="MicrostripSubstrateInjection"/>
/// is to MLIN and <see cref="ViaSubstrateInjection"/> to a via. At extraction, a <c>BCLIN</c> or
/// <c>MBCLIN</c> on a technology has its cross-section resolved from two adjacent conductor layers of the
/// stackup (<see cref="SubstrateResolver.ResolveBroadsidePair"/>) and injected as plain SI overrides.
///
/// <para><b>What the user states.</b> The two layer names (<c>LayerA</c>, <c>LayerB</c>; empty = the
/// stackup's default pair) and the strips' <c>W</c>, <c>O</c>, <c>L</c>. Every cross-section parameter
/// (<c>Hs</c>, <c>Ha</c>/<c>Hd</c>, <c>Hb</c>, <c>T</c>, <c>Er</c>) may be left EMPTY, which means
/// "follow the technology"; a value typed into one wins over the stackup for that one only. With no
/// technology (or no pair in it) an empty parameter takes the model's own default, and the message
/// returned beside the overrides says so.</para>
/// </summary>
public static class BroadsideSubstrateInjection
{
    public const string LayerAParam = "LayerA";
    public const string LayerBParam = "LayerB";

    /// <summary>The layer-choice parameters — resolution inputs, never engine parameters.</summary>
    public static readonly IReadOnlySet<string> LayerParams =
        new HashSet<string>(StringComparer.Ordinal) { LayerAParam, LayerBParam };

    public static bool IsBroadsideKind(SymbolKind kind) => kind is SymbolKind.BCLine or SymbolKind.MBCLine;

    /// <summary>The overrides to append to the instance line, and the sentences to post.</summary>
    public sealed record Injection(IReadOnlyList<ParameterAssignment> Overrides, IReadOnlyList<string> Messages,
                                   ResolvedBroadsidePair? Pair);

    public static Injection Build(Technology? technology, SymbolKind kind, IEnumerable<EditableParameter> parameters)
    {
        var list = parameters as IReadOnlyList<EditableParameter> ?? parameters.ToList();
        string? Text(string name) => list.FirstOrDefault(p => p.Name == name)?.Expression is { } e && e.Trim().Length > 0
            ? e.Trim() : null;
        bool Empty(string name) => list.Any(p => p.Name == name) && Text(name) is null;

        bool stripline = kind == SymbolKind.BCLine;
        var overrides = new List<ParameterAssignment>();
        var messages = new List<string>();
        var (pair, failure, warnings) = SubstrateResolver.ResolveBroadsidePair(
            technology, Text(LayerAParam), Text(LayerBParam), stripline);
        messages.AddRange(warnings);

        var stackParams = stripline ? (string[])["Hs", "Ha", "Hb", "T", "Er"] : ["Hs", "Hb", "Hd", "T", "Er"];
        if (pair is null)
        {
            var defaulted = stackParams.Where(Empty).ToList();
            if (defaulted.Count > 0)
                messages.Add($"{failure?.Reason ?? "no stackup resolved"} — {string.Join(", ", defaulted)} " +
                             $"{(defaulted.Count == 1 ? "takes its" : "take their")} standalone default" +
                             $"{(defaulted.Count == 1 ? "" : "s")}. Set {(defaulted.Count == 1 ? "it" : "them")} on the component to use your own.");
            return new Injection(overrides, messages, null);
        }

        void Add(string name, double value) => overrides.Add(new ParameterAssignment(name, Fmt(value)));
        if (Empty("Hs")) Add("Hs", pair.Hs);
        if (Empty("Hb")) Add("Hb", pair.Hb);
        if (stripline && Empty("Ha")) Add("Ha", pair.Ha);
        // MBCLIN's Hd matters only when the stack puts dielectric above strip A; otherwise the model's
        // own default (A on the surface) is the stack's answer too.
        if (!stripline && Empty("Hd") && pair.Hd > pair.Hb + pair.Tb + pair.Hs * (1 + 1e-9)) Add("Hd", pair.Hd);
        if (Empty("T"))
        {
            Add("Ta", pair.Ta);
            Add("Tb", pair.Tb);
        }
        if (Empty("Er")) Add("Er", pair.EpsR);
        // Materials the parameter list does not show: always from the stack.
        Add("TanD", pair.TanD);
        if (pair.Sigma > 0) Add("Sigma", pair.Sigma);
        return new Injection(overrides, messages, pair);
    }

    private static string Fmt(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>A resolved broadside stack as engine overrides, the names BCLIN and MBCLIN take (Ha only on a stripline,
    /// Hd only where dielectric lies above strip A): what a broadside coupler (MCLC) is given at extraction.</summary>
    public static IReadOnlyList<ParameterAssignment> Overrides(CircuitRF.Core.Devices.Microstrip.BroadsideStack stack)
    {
        var o = new List<ParameterAssignment>
        {
            new("Hs", Fmt(stack.Hs)), new("Hb", Fmt(stack.Hb)), new("Ta", Fmt(stack.Ta)), new("Tb", Fmt(stack.Tb)),
            new("Er", Fmt(stack.EpsR)), new("TanD", Fmt(stack.TanD)), new("Sigma", Fmt(stack.SigmaSPerM)),
        };
        if (stack.Stripline) o.Add(new("Ha", Fmt(stack.Ha)));
        if (!double.IsNaN(stack.Hd)) o.Add(new("Hd", Fmt(stack.Hd)));
        return o;
    }
}
