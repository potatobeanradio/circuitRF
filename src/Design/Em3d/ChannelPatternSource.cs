// brief-pch-6 R-pch6-2 — fills PropagationChannelModel.PatternSource: a Propagation Channel's pin end reads one port of an EM result's
// far field (PortFarFieldSet), at the record frequency nearest the one asked. The pattern is the port's terminated embedded pattern per
// unit incident wave (EmbeddedPatterns, as a fields analysis forms it), scaled to a realized-gain field: in the co-simulation's
// peak-wave convention U = |rE|²/2η₀ and P_inc = |a|²/2, so e = rE·√(4π/η₀) has |e|² = G(θ, φ), the port's mismatch in it.
// A module initializer, as ArrayRadiationSource's, for the same reason: every path that elaborates passes through this assembly.

using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using CircuitRF.Core.Devices;
using CircuitRF.Engine.AntennaArray;

namespace CircuitRF.Design.Em3d;

internal static class ChannelPatternSource
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
                     Justification = "Fills a seam src/Core declares; see the comment above.")]
    internal static void Install() => PropagationChannelModel.PatternSource = Pattern;

    internal static ChannelPattern Pattern(string recordPath, int port, double hz)
    {
        var (far, refused) = PortFarFieldSet.Open(recordPath);
        if (far is null) throw new InvalidOperationException(refused ?? $"'{Path.GetFileName(recordPath)}' holds no far field.");
        if (port < 0 || port >= far.PortCount)
            throw new InvalidOperationException(PropagationChannelDiagnostics.PatternPortMissing(far.Name, far.PortCount, port + 1).Render());
        int i = far.IndexOf(hz);
        if (i < 0) i = Enumerable.Range(0, far.FrequenciesHz.Count).MinBy(k => Math.Abs(far.FrequenciesHz[k] - hz));
        double f = far.FrequenciesHz[i];
        var pattern = EmbeddedPatterns.Of(far, [port], f)[0];
        double scale = Math.Sqrt(4 * Math.PI / ArrayWaves.Eta0);
        return new ChannelPattern
        {
            FrequencyHz = f,
            Record = far.Name,
            S11 = far.S[i][port, port],
            Z0 = far.Z0.Real,
            Field = (th, ph) => { var (et, ep) = pattern.At(th, ph); return (et * scale, ep * scale); },
        };
    }
}
