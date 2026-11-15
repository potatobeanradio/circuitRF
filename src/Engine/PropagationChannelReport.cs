// brief-pch-6 R-pch6-4 — what a run says of each Propagation Channel (design note §5.6), as cubes beside its results: the loss
// breakdown at the channel's reference frequency, the total path loss, the bulk delay and the common Doppler, and the rays.
// Named link:<instance>:<quantity>, the instance's own path, so a sweep stacks them as it stacks every other cube.

using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using RfCore.Data;

namespace CircuitRF.Engine;

public static class PropagationChannelReport
{
    /// <summary>
    /// Adds each channel's report to <paramref name="ds"/>, at the reference frequency of <paramref name="hz"/> (its f0, else
    /// the run's carrier, else <paramref name="hz"/>), and says once that harmonic balance holds the channel at TimeOffset with
    /// no Doppler shift when <paramref name="hb"/>.
    /// </summary>
    public static void Add(DataSet ds, ElaboratedNetlist nl, double hz, bool hb = false)
    {
        foreach (var ec in nl.Components)
        {
            if (ec.Model is not PropagationChannelModel ch) continue;
            PropagationChannelModel.Realization r;
            try { r = ch.At(ch.ReferenceHz(hz)); }
            catch (ParameterRefusalException) { continue; }   // the run itself refused it, naming why
            string key = $"link:{ec.InstancePath}:";
            var terms = r.LargeScale.Terms;
            ds.Add(key + "PathLoss_dB", DataCube.Scalar(r.LargeScale.LossDb));
            if (terms.Count > 0)
                ds.Add(key + "Loss_dB", new DataCube([new Axis("term", [.. Enumerable.Range(0, terms.Count).Select(i => (double)i)], "",
                                                               [.. terms.Select(t => t.Name)])], [.. terms.Select(t => t.Db)]) { Unit = "dB" });
            ds.Add(key + "Delay0_s", DataCube.Scalar(r.BulkDelayS));
            ds.Add(key + "Doppler0_Hz", DataCube.Scalar(r.CommonDopplerHz));
            ds.Add(key + "Reference_Hz", DataCube.Scalar(r.ReferenceHz));
            int n = r.Rays.Count;
            var rayAxis = new Axis("ray", [.. Enumerable.Range(0, n).Select(i => (double)i)], "", [.. r.Rays.Select(x => x.Name)]);
            var terms0 = ch.Terms(r.ReferenceHz);
            ds.Add(key + "RayDelay_s", new DataCube([rayAxis], [.. r.Rays.Select(x => x.ExcessDelayS)]) { Unit = "s" });
            ds.Add(key + "RayDoppler_Hz", new DataCube([rayAxis], [.. r.Rays.Select(x => x.DopplerHz)]) { Unit = "Hz" });
            ds.Add(key + "RayGain", new DataCube([rayAxis], terms0.Length == n ? [.. terms0.Select(t => t.Amplitude)] : new Complex[n]));
            if (hb && r.Rays.Any(x => x.DopplerHz != 0))
                nl.AddNoteOnce("hb-channel-doppler:" + ec.InstancePath,
                    $"Propagation Channel '{ec.InstancePath}': harmonic balance holds it at TimeOffset with no Doppler shift (a shifted tone would " +
                    "leave the harmonic grid); an envelope run carries the Doppler.");
        }
    }
}
