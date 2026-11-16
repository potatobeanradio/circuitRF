// ================================================================
//  TransientSpiceFits.cs  —  the transient engine's fits, handed to the SPICE
//  deck writer for SpiceFit=yes (docs/design/transient.md §9.2.3; brief TRN-6
//  R-trn6-8)
//
//  The deck realizes exactly what a native run would have stepped: the same
//  TransientRealization runs on the netlist, every S-matrix fit and lossy-line
//  residual it swaps in is read back by instance path, and the netlist is put
//  back as it was. No second fitter exists.
// ================================================================

using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist.Spice;
using CircuitRF.Engine.Envelope;

namespace CircuitRF.Engine.Transient;

/// <summary>The transient engine's fits as an <see cref="ISpiceFitSource"/>.</summary>
public sealed class TransientSpiceFits : ISpiceFitSource
{
    public SpiceFitSet Realize(ElaboratedNetlist netlist, double fMaxHz)
    {
        var set = new SpiceFitSet();
        var before = netlist.Components.ToDictionary(c => c.InstancePath, c => c, StringComparer.Ordinal);
        foreach (var ec in netlist.Components)
            if (ec.Model is P1ToneModel p1) p1.SetToneContext(fc: p1.FreqHz, driveFreqHz: p1.FreqHz);
        using var r = TransientRealization.Apply(netlist, fMaxHz);
        foreach (var ec in netlist.Components)
        {
            switch (ec.Model)
            {
                case RationalSnpModel rs when r.FitZ0.TryGetValue(ec.InstancePath, out double z0):
                {
                    var fit = rs.Fit;
                    set.Blocks[ec.InstancePath] = new SpiceBlockFit(
                        ec.Nodes, ec.ReferenceNode, z0, fit.Poles, [.. fit.Residues.Select(x => (IReadOnlyList<System.Numerics.Complex>)x)], fit.D);
                    break;
                }
                case TransientLineModel tl when tl.Z.Length == 1 && before.TryGetValue(ec.InstancePath, out var orig)
                                               && orig.Model is IUniformLine line && line.Ends(orig.Nodes) is { } ends:
                {
                    var res = tl.Residual.Length > 0 ? tl.Residual[0] : null;
                    set.Lines[ec.InstancePath] = new SpiceLineResidual(
                        ends.A, ends.B, tl.Z[0], tl.Tau[0], res?.Poles ?? [], res is null ? [] : res.Residues[0], res is null ? 1 : res.D[0]);
                    break;
                }
            }
        }
        foreach (var p in r.Problems)
            foreach (var path in before.Keys)
                if (p.StartsWith(path + " (", StringComparison.Ordinal) && !set.Refusals.ContainsKey(path))
                    set.Refusals[path] = p[(p.IndexOf(')') + 1)..].TrimStart(':', ' ');
        return set;
    }
}
