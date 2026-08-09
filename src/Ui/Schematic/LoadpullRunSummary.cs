// Turns a finished Loadpull / Loadpull-Pursuit DataSet into the one-line outcome report the
// Messages pane shows after a run. Framework-free (no Avalonia, no Skia): it reads only the cubes
// the engine already publishes, so nothing in src/Engine had to change to make this possible.
//
// The compression count is the number the owner actually asked for and the reason this exists: it
// is what tells you whether the Pin drive level you chose was high enough, and therefore what to
// set it to on the next run. A grid where nothing compressed and everything stopped at max drive
// is a sweep that answered a different question than the one intended — and, before this, said so
// nowhere at all.

using System.Linq;
using RfCore.Data;

namespace CircuitRF.Ui.Schematic;

public static class LoadpullRunSummary
{
    // The engine's own StopCode encoding (LoadpullEngine.BuildLoadpullDataSet). Mirrored here rather
    // than shared because it is a wire value in a published DataCube — a .npy written by an older
    // build must keep decoding the same way, which a shared enum could silently renumber.
    private const double StopPinMax          = 0;
    private const double StopCompression     = 1;
    private const double StopNonConvergence  = 2;
    private const double StopNoConvergedSeed = 3;

    /// <summary>
    /// A one-line summary of a loadpull or loadpull-pursuit run, or null when <paramref name="ds"/>
    /// is neither (every other analysis type reaches this and must produce nothing).
    ///
    /// <para>A Loadpull-Pursuit result embeds the follow-on loadpull's own cubes under their
    /// original names, so when that grid exists it is summarised by exactly the same path as a
    /// plain loadpull. <b>When it does NOT exist the pursuit is summarised on its own terms</b> —
    /// see <see cref="DescribePursuit"/>. This used to return null there, which is the case the
    /// owner reported: a pursuit where nothing reached compression scores nothing, so no optimum
    /// converges, so no follow-on loadpull runs, so the whole run finished in silence — at exactly
    /// the moment the user most needs to be told what happened.</para>
    /// </summary>
    public static string? Describe(DataSet? ds)
    {
        if (ds is null) return null;
        if (ds.Contains("StopCode")) return DescribeGrid(ds);

        // No grid. A pursuit still has plenty to report; anything else is not ours to describe.
        return IsPursuit(ds) ? DescribePursuit(ds) : null;
    }

    /// <summary>True for a Loadpull-Pursuit result — keyed on the optimum-convergence scalars, which
    /// <c>BuildPursuitDataSet</c> always emits and no other analysis publishes.</summary>
    private static bool IsPursuit(DataSet ds)
        => ds.Contains("MXP_Converged") || ds.Contains("MXE_Converged");

    private static string? DescribeGrid(DataSet ds)
    {
        var stop = ds["StopCode"];
        if (stop.DataKind != DataKind.Real) return null;

        var codes = stop.RealValues;

        int compressed  = codes.Count(c => Same(c, StopCompression));
        int maxDrive    = codes.Count(c => Same(c, StopPinMax));
        int notConverged = codes.Count(c => Same(c, StopNonConvergence) || Same(c, StopNoConvergedSeed));

        var parts = new List<string> { $"{codes.Length} point(s) simulated" };
        parts.Add($"{compressed} reached compression");

        // Only mentioned when they happened: a clean sweep should read as a clean sweep, not as a
        // list of zeros the reader has to check every time.
        if (maxDrive > 0)     parts.Add($"{maxDrive} stopped at max drive");
        if (notConverged > 0) parts.Add($"{notConverged} did not converge");

        string line = string.Join(" · ", parts);

        // The actionable half. Nothing compressing means the drive level was the limit, which is
        // exactly what the count is for; everything compressing is worth knowing too, since the
        // sweep then says nothing about behaviour below compression.
        if (compressed == 0)
            line += " — raise the Pin drive level to reach compression.";
        else if (compressed == codes.Length && codes.Length > 1)
            line += " — every point compressed; lower the starting Pin to see the drive-up.";

        return line;
    }

    /// <summary>
    /// The pursuit's own outcome, for a run that produced no follow-on loadpull grid.
    ///
    /// <para>A pursuit scores each candidate termination by extracting a compression-referenced
    /// criterion from its drive-up sweep, so a termination that never reaches compression is
    /// UNSCORABLE. If enough of them are, no optimum converges, and the follow-on loadpull — which
    /// runs only when both optima converged — never happens. The three reasons a grid can be absent
    /// are distinguishable from the published scalars and are reported separately, because they call
    /// for completely different responses: raise the drive, widen the search, or switch the
    /// follow-on back on.</para>
    /// </summary>
    private static string DescribePursuit(DataSet ds)
    {
        int  queried    = ScalarInt(ds, "CacheCount");
        int  unscorable = ScalarInt(ds, "UnscorableCount");
        int  recommended = ScalarInt(ds, "RecommTermCount");
        bool mxp = ScalarBool(ds, "MXP_Converged");
        bool mxe = ScalarBool(ds, "MXE_Converged");

        var parts = new List<string> { $"{queried} termination(s) queried" };
        if (unscorable > 0) parts.Add($"{unscorable} could not be scored");

        parts.Add((mxp, mxe) switch
        {
            (true,  true)  => "MXP and MXE both converged",
            (true,  false) => "MXP converged, MXE did not",
            (false, true)  => "MXE converged, MXP did not",
            _              => "neither MXP nor MXE converged",
        });

        if (recommended > 0) parts.Add($"{recommended} recommended termination(s)");

        string line = string.Join(" · ", parts);

        // Why there is no loadpull grid — the actionable half, and the whole point of the message.
        if (!mxp || !mxe)
        {
            line += " — no follow-on loadpull ran, because an optimum did not converge.";

            // The specific cause the owner hit: the criterion is compression-referenced, so a sweep
            // where nothing compressed has nothing to score and the drive level is the thing to change.
            if (queried > 0 && unscorable >= queried)
                line += " No termination reached compression — raise the Pin drive level.";
        }
        else if (recommended == 0)
        {
            line += " — no follow-on loadpull ran: the search produced no recommended terminations.";
        }
        else
        {
            line += " — no follow-on loadpull was requested (CreateLoadpullResult is off).";
        }

        return line;
    }

    /// <summary>Reads a rank-0 Real scalar, tolerating an absent or wrong-kinded cube — this runs on a
    /// result that has already been published and must never be the thing that fails a finished run.</summary>
    private static double Scalar(DataSet ds, string name)
        => ds.Contains(name)
           && ds[name] is { DataKind: DataKind.Real } c
           && c.RealValues.Length > 0
            ? c.RealValues[0]
            : 0.0;

    private static int  ScalarInt(DataSet ds, string name)  => (int)System.Math.Round(Scalar(ds, name));
    private static bool ScalarBool(DataSet ds, string name) => Scalar(ds, name) >= 0.5;

    private static bool Same(double a, double b) => System.Math.Abs(a - b) < 0.5;
}
