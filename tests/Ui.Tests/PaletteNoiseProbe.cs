using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core;
using CircuitRF.Core.Expressions;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// What the palette-wiring tests share about a device's NOISE parameters (brief NZ-4): Ri, Tg, Td, Kf, Af and GammaN reach
/// a model's noise sources rather than its currents and charges, so a probe of <c>Evaluate</c> alone reads every one of
/// them as unwired. Each is set away from its default here, the probe adds the noise densities, and a default is read
/// the way the application reads it — a blank is the model's own default and an expression (<c>2/3</c>) is evaluated.
/// </summary>
internal static class PaletteNoiseProbe
{
    /// <summary>Live values. Ri puts a FET's gate on an internal node, so Tg (its noise temperature) is live too; Kf turns
    /// the flicker source on, so Af is live.</summary>
    private static readonly Dictionary<string, double> Live = new(StringComparer.Ordinal)
    {
        ["Ri"] = 2.0, ["Tg"] = 400.0, ["Td"] = 1500.0, ["Kf"] = 1e-12, ["Af"] = 1.2, ["GammaN"] = 0.8,
    };

    /// <summary>Adds a live value for each noise parameter the registry offers on this kind.</summary>
    public static void AddTo(Dictionary<string, double> activation, IEnumerable<string> registryNames)
    {
        foreach (string name in registryNames)
            if (Live.TryGetValue(name, out double v)) activation.TryAdd(name, v);
    }

    /// <summary>A FET with Ri has four ports — the resistor, the intrinsic gate-source, the drain-source and Cgd's
    /// charge-only port — where the probe's grid states two.</summary>
    public static double[] Ports(ComponentModel m, double[] v)
        => m.PortCount == 4 && v.Length == 2 ? [0.01, v[0], v[1], v[0] - v[1]] : v;

    /// <summary>The noise at one state, as logs of each density at 1 kHz and 1 GHz: the densities are of order 1e-20 A²/Hz,
    /// far below any absolute floor a probe of currents uses.</summary>
    public static void Append(ComponentModel m, double[] v, List<double> probe)
    {
        var declared = m.NoiseSources;
        probe.Add(declared.Count);
        if (declared.Count == 0) return;
        var d = new NoiseDensity[declared.Count];
        m.EvaluateNoise(new PortVoltages(v), 300.0, d);
        foreach (var x in d)
            foreach (double hz in (double[])[1e3, 1e9])
            {
                double p = x.At(hz);
                probe.Add(p > 0 ? Math.Log(p) : 0.0);
            }
    }

    /// <summary>The palette's defaults as the application reads them: a blank omitted, so the model takes its own
    /// default, and anything else evaluated by the expression engine.</summary>
    public static Dictionary<string, double> Defaults(SymbolKind kind)
        => ComponentTypeRegistry.DefaultParameters(kind, 0)
            .Where(p => !string.IsNullOrWhiteSpace(p.Expression))
            .ToDictionary(p => p.Name, p => new Evaluator().Eval(p.Expression, new Scope("palette")).AsReal(),
                          StringComparer.Ordinal);
}
