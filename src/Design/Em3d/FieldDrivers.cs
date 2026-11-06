// Framework-free. No Avalonia, no SkiaSharp.

using System.Globalization;
using CircuitRF.Core.Devices;
using CircuitRF.Core;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.Em3d;

/// <summary>One thing a Fields analysis's Drive port control lists.</summary>
/// <param name="Label">What the control shows: the instance, with its port number when it has one.</param>
/// <param name="Value">What <c>drive=</c> says for it: the port number, or the instance name of a source with none.</param>
/// <param name="Instance">The instance, which <c>drive=</c> also takes.</param>
/// <param name="Problem">Why a run would refuse it; null for a port.</param>
/// <param name="HasPavl">A P1Tone, whose Pavl and Phase are its amplitude when none is stated.</param>
public sealed record FieldDriver(string Label, string Value, string Instance, string? Problem, bool HasPavl = false);

/// <summary>
/// What can be named as a Fields analysis's drive. A fields analysis sends a wave in from a circuit port's Z0 at the frequency it
/// recombines, so the drivers are the components the S-parameter engine makes ports of: Term, TermG and P1Tone, at the top level. The
/// other RF sources are listed too, since a user looking for their source should find it and be told why not, rather than not find it.
/// </summary>
public static class FieldDrivers
{
    /// <summary>A schematic's drivers as it stands in the editor: its ports by number, then the sources that are not ports.</summary>
    public static IReadOnlyList<FieldDriver> Of(SchematicEditModel model)
    {
        var ports = new List<FieldDriver>();
        var others = new List<FieldDriver>();
        foreach (var c in model.Components)
        {
            if (SchematicExclusions.IsExcluded(c) || c.InstanceName.Length == 0) continue;
            if (c.Symbol is SymbolKind.Term or SymbolKind.TermG or SymbolKind.P1Tone)
            {
                string num = c.Parameters.FirstOrDefault(p => p.Name == "Num")?.Expression.Trim() ?? "";
                bool hasPavl = c.Symbol == SymbolKind.P1Tone;
                ports.Add(num.Length > 0 ? new FieldDriver($"{c.InstanceName} (port {num})", num, c.InstanceName, null, hasPavl)
                                         : new FieldDriver(c.InstanceName, c.InstanceName, c.InstanceName, null, hasPavl));
            }
            else if (SourceName(c.Symbol) is { } kind)
                others.Add(new FieldDriver($"{c.InstanceName} ({kind})", c.InstanceName, c.InstanceName, $"A {kind} is not a circuit port. Use a Term, TermG or P1Tone."));
        }
        return [.. ports.OrderBy(d => double.TryParse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.MaxValue), .. others];
    }

    /// <summary>What a run says when <c>drive=</c> names <paramref name="instance"/>, a source that is not a port; null when
    /// <paramref name="model"/> is not such a source.</summary>
    public static string? Refusal(string instance, ComponentModel model) => SourceName(model) is { } kind
        ? $"'{instance}' is a {kind}, not a circuit port. A fields analysis sends one wave in from a port's Z0 at the frequency it recombines, so use a Term, TermG or P1Tone."
        : null;

    private static string? SourceName(SymbolKind symbol) => symbol switch
    {
        SymbolKind.ToneSource => "VTone",
        SymbolKind.CurrentToneSource => "ITone",
        SymbolKind.PnTone => "PnTone",
        SymbolKind.ModSource => "MOD_Source",
        _ => null,
    };

    private static string? SourceName(ComponentModel model) => model switch
    {
        ToneSourceModel => "VTone",
        CurrentToneSourceModel => "ITone",
        PnToneModel => "PnTone",
        ModSourceModel => "MOD_Source",
        _ => null,
    };
}
