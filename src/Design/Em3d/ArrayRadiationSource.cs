// brief-paa-7 — fills PhasedArrayModel.RadiationSource: the one upward seam src/Core declares for a Phased Antenna Array's receive and noise halves,
// which need its record's patterns, and src/Core reads no result. The patterns are the ones a fields analysis on the same block reads
// (FieldCosimulation.Elements: each element's terminated embedded pattern referred to its own position, one per distinct record port), and the
// grid's solid angles are the radiated-power quadrature (PlanarFarFieldPattern.SolidAngles), so a received power and the realized gain of the
// same array use one pattern and one integral.
//
// A module initializer, as RenderTypefaceInstaller's, because nothing else is reached before every run: every path that elaborates a schematic,
// a netlist or a .cnl in this repository passes through this assembly first.

using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Arrays;
using CircuitRF.Engine.AntennaArray;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Design.Em3d;

internal static class ArrayRadiationSource
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
                     Justification = "Fills a seam src/Core declares; see the comment above.")]
    internal static void Install() => PhasedArrayModel.RadiationSource = Radiation;

    /// <summary>The record's patterns at its frequency nearest <paramref name="hz"/>.</summary>
    internal static ArrayRadiation Radiation(PhasedArrayModel pa, double hz)
    {
        var (far, refused) = PortFarFieldSet.Open(pa.RecordPath);
        if (far is null) throw new InvalidOperationException(refused ?? $"'{Path.GetFileName(pa.RecordPath)}' holds no far field.");
        if (far.PortCount != pa.Record.PortCount)
            throw new InvalidOperationException($"'{far.Name}' lists {pa.Record.PortCount} ports and its far field holds {far.PortCount}. Run its EM setup again.");
        int i = far.IndexOf(hz);
        if (i < 0) i = Enumerable.Range(0, far.FrequenciesHz.Count).MinBy(k => Math.Abs(far.FrequenciesHz[k] - hz));
        double f = far.FrequenciesHz[i];
        var elements = FieldCosimulation.Elements(pa, far, f);
        var grid = far.Grid;
        int nd = grid.DirectionCount;
        var theta = new double[nd];
        var phi = new double[nd];
        for (int t = 0; t < grid.ThetaDeg.Count; t++)
            for (int p = 0; p < grid.PhiDeg.Count; p++)
            {
                int d = grid.IndexOf(t, p);
                theta[d] = grid.ThetaDeg[t] * Math.PI / 180;
                phi[d] = grid.PhiDeg[p] * Math.PI / 180;
            }
        var patterns = elements.Patterns;
        return new ArrayRadiation
        {
            FrequencyHz = f,
            Record = far.Name,
            ThetaRad = theta,
            PhiRad = phi,
            Weight = PlanarFarFieldPattern.SolidAngles(grid),
            ETheta = [.. patterns.Select(e => e.ETheta.ToArray())],
            EPhi = [.. patterns.Select(e => e.EPhi.ToArray())],
            PatternOf = [.. elements.PatternOf],
            At = (q, th, ph) => patterns[q].At(th, ph),
            Eta0 = ArrayWaves.Eta0,
            C0 = EmConstants.C0,
        };
    }
}
