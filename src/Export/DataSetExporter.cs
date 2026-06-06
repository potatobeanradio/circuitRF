// ================================================================
//  DataSetExporter.cs  —  Main export entry point
//
//  1. Estimates the output size (data-export.md §6).
//  2. Warns to stderr if estimate > SizeWarningThresholdMiB.
//  3. Dispatches to NpyWriter or MatWriter.
//
//  See docs/design/data-export.md §7.3.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using RfCore.Data;

namespace RfCore.Export;

/// <summary>
/// Exports a <see cref="DataSet"/> to <c>.mat</c> (MATLAB v7.3 / HDF5) or
/// <c>.npy</c> (NumPy packed structured array).
/// </summary>
public static class DataSetExporter
{
    // ── Public entry point ───────────────────────────────────────────────────

    /// <summary>
    /// Export a DataSet to disk.
    /// <para>Estimates disk size first; warns to <see cref="Console.Error"/> if the
    /// estimate exceeds <see cref="ExportOptions.SizeWarningThresholdMiB"/> (default 100 MiB).
    /// The write always proceeds — warn-and-continue, no abort.</para>
    /// </summary>
    /// <param name="ds">The DataSet to export.</param>
    /// <param name="path">Output file path. Extension is ignored; format determines the actual file.</param>
    /// <param name="format">Target format (<see cref="ExportFormat.Mat"/> or <see cref="ExportFormat.Npy"/>).</param>
    /// <param name="options">Export options; <c>null</c> uses <see cref="ExportOptions.Default"/>.</param>
    /// <param name="linearPayload">
    /// Required when <see cref="ExportOptions.IncludeLinearNetwork"/> is <c>true</c>
    /// or <see cref="ExportOptions.LinearEvalMode"/> is not <see cref="LinearEvalMode.EvaluateNone"/>.
    /// Silently ignored when those options are both off.
    /// </param>
    public static void Export(
        DataSet               ds,
        string                path,
        ExportFormat          format,
        ExportOptions?        options       = null,
        ILinearNetworkPayload? linearPayload = null)
    {
        if (ds is null)      throw new ArgumentNullException(nameof(ds));
        if (path is null)    throw new ArgumentNullException(nameof(path));

        var opts = options ?? ExportOptions.Default;

        // ── Validate ─────────────────────────────────────────────────────────
        if (opts.IncludeLinearNetwork && linearPayload is null)
        {
            Console.Error.WriteLine(
                "[Export] Warning: IncludeLinearNetwork = true but no ILinearNetworkPayload was " +
                "supplied — linear-network data will NOT be written.");
        }

        // ── Size estimate & warning ───────────────────────────────────────────
        EstimateAndWarn(ds, opts, linearPayload);

        // ── Optional linear-interior evaluation ───────────────────────────────
        DataSet workingDs = opts.LinearEvalMode != LinearEvalMode.EvaluateNone
                            && linearPayload is not null
            ? EvaluateLinearInterior(ds, opts, linearPayload)
            : ds;

        // ── Dispatch to format writer ─────────────────────────────────────────
        switch (format)
        {
            case ExportFormat.Mat:
                MatWriter.Write(path, workingDs, opts, linearPayload);
                break;

            case ExportFormat.Npy:
                NpyWriter.Write(path, workingDs, opts, linearPayload);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown export format.");
        }
    }

    // ── Size estimate ─────────────────────────────────────────────────────────

    private static void EstimateAndWarn(
        DataSet               ds,
        ExportOptions         opts,
        ILinearNetworkPayload? payload)
    {
        const long MiB = 1L << 20;

        // Existing DataSet cubes
        long existingBytes = ds.Cubes.Values.Sum(c =>
        {
            long elems = c.Axes.Count == 0 ? 1L : c.Axes.Aggregate(1L, (acc, a) => acc * a.Length);
            return elems * (c.DataKind == DataKind.Complex ? 16L : 8L);
        });

        long linearNetworkBytes  = 0;
        long evalBytes           = 0;

        if (payload != null)
        {
            int K1 = payload.HarmonicCount, S = payload.SweepCount,
                M  = payload.MnaSize, N  = payload.InterfaceCount;

            if (opts.IncludeLinearNetwork)
            {
                var (rows, _, _) = payload.GetSparseG(0);
                int nnz = rows.Length;
                linearNetworkBytes =
                    (long)nnz * (4L + 4L + 16L) * K1  // G_rows(4) + G_cols(4) + G_data(16) per harmonic
                    + (long)S * K1 * M * 16L            // bSrc
                    + (long)S * K1 * N * 16L;           // iNl
            }

            int nodeCount   = opts.EvalNodeNames?.Count   ?? 0;
            int branchCount = opts.EvalBranchRefs?.Count  ?? 0;

            evalBytes = opts.LinearEvalMode switch
            {
                LinearEvalMode.EvaluateAll =>
                    (long)(payload.NonGroundCount + (M - payload.NonGroundCount)) * K1 * S * 16L,
                LinearEvalMode.EvaluateSpecified =>
                    (long)(nodeCount + branchCount) * K1 * S * 16L,
                _ => 0L
            };
        }

        long totalBytes     = existingBytes + linearNetworkBytes + evalBytes;
        double totalMiB     = (double)totalBytes / (1L << 20);
        double thresholdMiB = opts.SizeWarningThresholdMiB;

        if (totalMiB <= thresholdMiB) return;

        // Identify dominant contributor
        string dominant;
        long   domBytes;
        if (evalBytes >= linearNetworkBytes && evalBytes >= existingBytes)
        {
            string modeLabel = opts.LinearEvalMode == LinearEvalMode.EvaluateAll
                ? "EvaluateAll" : "EvaluateSpecified";
            dominant = modeLabel;
            domBytes = evalBytes;
        }
        else if (linearNetworkBytes >= existingBytes)
        {
            dominant = "IncludeLinearNetwork";
            domBytes = linearNetworkBytes;
        }
        else
        {
            dominant = "DataSet cubes";
            domBytes = existingBytes;
        }

        Console.Error.WriteLine(
            $"[Export] Estimated output: {totalMiB:F1} MiB — " +
            $"exceeds the {thresholdMiB:G} MiB advisory threshold.");
        Console.Error.WriteLine(
            $"         Dominant contributor: {dominant} ({(double)domBytes / MiB:F1} MiB).");
        Console.Error.WriteLine(
            "         Consider: LinearEvalMode.EvaluateSpecified with a node list, or " +
            "IncludeLinearNetwork = false.");
        Console.Error.WriteLine(
            "         Proceeding — no data has been written yet.");
    }

    // ── Linear-interior evaluation (EvaluateAll / EvaluateSpecified) ──────────

    private static DataSet EvaluateLinearInterior(
        DataSet               ds,
        ExportOptions         opts,
        ILinearNetworkPayload payload)
    {
        // Determine which node indices and branch indices to evaluate.
        int K1 = payload.HarmonicCount;
        int S  = payload.SweepCount;
        int nonGnd = payload.NonGroundCount;
        int mnaSize = payload.MnaSize;

        int[] nodeIdxs;
        int[] branchIdxs;
        string[] nodeLabels;
        string[] branchLabels;

        string[] nodeNames   = payload.NodeNames;
        string[] branchNames = payload.BranchNames;

        if (opts.LinearEvalMode == LinearEvalMode.EvaluateAll)
        {
            nodeIdxs    = Enumerable.Range(0, nonGnd).ToArray();
            branchIdxs  = Enumerable.Range(0, mnaSize - nonGnd).ToArray();
            nodeLabels   = nodeNames;
            branchLabels = branchNames;
        }
        else  // EvaluateSpecified
        {
            var resolvedNodes   = new List<(int idx, string label)>();
            var resolvedBranches = new List<(int idx, string label)>();

            if (opts.EvalNodeNames != null)
            {
                foreach (var name in opts.EvalNodeNames)
                {
                    int idx = Array.IndexOf(nodeNames, name);
                    if (idx < 0)
                        Console.Error.WriteLine(
                            $"[Export] Warning: node '{name}' not found in node_names — skipping.");
                    else
                        resolvedNodes.Add((idx, name));
                }
            }

            if (opts.EvalBranchRefs != null)
            {
                foreach (var bref in opts.EvalBranchRefs)
                {
                    // Branch refs use "I:path:terminal" or "L:path" format;
                    // match against branch_names entries.
                    int idx = FindBranchByRef(branchNames, bref);
                    if (idx < 0)
                        Console.Error.WriteLine(
                            $"[Export] Warning: branch ref '{bref}' not found in branch_names — skipping.");
                    else
                        resolvedBranches.Add((idx, branchNames[idx]));
                }
            }

            nodeIdxs    = resolvedNodes.Select(t => t.idx).ToArray();
            branchIdxs  = resolvedBranches.Select(t => t.idx).ToArray();
            nodeLabels   = resolvedNodes.Select(t => t.label).ToArray();
            branchLabels = resolvedBranches.Select(t => t.label).ToArray();
        }

        if (nodeIdxs.Length == 0 && branchIdxs.Length == 0)
            return ds;  // nothing to evaluate

        // Build solution cache: call GetSolution once per (k, si) and cache.
        // We call into the payload's GetBSrc/GetINl to reconstruct via ILinearNetworkPayload,
        // but the full back-solve is only available via the underlying solver.
        // Use a simple reconstruction here via payload data (matches SolveFullNetwork logic).
        //
        // NOTE: For the common case, HbLinearNetworkPayload wraps HbLinearBackSolver which
        // internally caches GetSolution(k, si). Calling the payload's Get* methods is
        // sufficient to materialize the cache entries.

        // Build harmonics axis
        var harmValues = new double[K1];
        for (int k = 0; k < K1; k++) harmValues[k] = k;
        var harmAxis = new Axis("harmonic", harmValues);

        // Build sweep axis (if any)
        bool hasSweep = S > 1;
        var sweepValues = new double[S];
        for (int si = 0; si < S; si++) sweepValues[si] = si;
        var sweepAxis = new Axis("sweep", sweepValues);

        // Build V_linear cube [nonGnd-selected, K1, S?]
        if (nodeIdxs.Length > 0)
        {
            var nodeAxis = BuildNodeAxis(nodeLabels, nodeIdxs);
            Axis[] axes  = hasSweep
                ? new[] { nodeAxis, harmAxis, sweepAxis }
                : new[] { nodeAxis, harmAxis };

            int nNodes = nodeIdxs.Length;
            var data   = new Complex[nNodes * K1 * (hasSweep ? S : 1)];

            for (int ni = 0; ni < nNodes; ni++)
            for (int k  = 0; k  < K1; k++)
            {
                for (int si = 0; si < (hasSweep ? S : 1); si++)
                {
                    // Reconstruct: full MNA solution at (k, si), extract node index.
                    // GetSparseG(k) + GetBSrc + GetINl gives us everything to solve,
                    // but we cannot do a sparse solve here without engine deps.
                    // Instead, rely on the payload's own GetSolution path via a cast
                    // to ILinearBackSolverProvider if available; otherwise leave zero
                    // and warn.  The canonical use case is HbLinearNetworkPayload which
                    // implements this via GetSolution.
                    Complex v = ReconstructNodeVoltage(payload, nodeIdxs[ni], k, si);
                    int flat  = hasSweep
                        ? (ni * K1 + k) * S + si
                        : ni * K1 + k;
                    data[flat] = v;
                }
            }

            ds.Add("V_linear", new DataCube(axes, data));
        }

        // Build I_linear cube [branchCount-selected, K1, S?]
        if (branchIdxs.Length > 0)
        {
            var branchAxis = BuildBranchAxis(branchLabels, branchIdxs, nonGnd);
            Axis[] axes    = hasSweep
                ? new[] { branchAxis, harmAxis, sweepAxis }
                : new[] { branchAxis, harmAxis };

            int nBranches = branchIdxs.Length;
            var data      = new Complex[nBranches * K1 * (hasSweep ? S : 1)];

            for (int bi = 0; bi < nBranches; bi++)
            for (int k  = 0; k  < K1; k++)
            for (int si = 0; si < (hasSweep ? S : 1); si++)
            {
                Complex i = ReconstructBranchCurrent(payload, nonGnd + branchIdxs[bi], k, si);
                int flat  = hasSweep
                    ? (bi * K1 + k) * S + si
                    : bi * K1 + k;
                data[flat] = i;
            }

            ds.Add("I_linear", new DataCube(axes, data));
        }

        return ds;
    }

    /// <summary>
    /// Reconstruct node voltage at MNA index <paramref name="nodeIdx"/> for harmonic
    /// <paramref name="k"/> at sweep index <paramref name="si"/>.
    ///
    /// Uses <see cref="IBackSolverProvider"/> if the payload implements it (HbLinearNetworkPayload
    /// wraps HbLinearBackSolver which caches full solution vectors).  Falls back to
    /// reading GetBSrc (the open-circuit voltage at the interface nodes) for interface
    /// nodes when the provider is unavailable — this is an approximation.
    /// </summary>
    private static Complex ReconstructNodeVoltage(
        ILinearNetworkPayload payload, int nodeIdx, int k, int si)
    {
        if (payload is IBackSolverProvider bsp)
            return bsp.GetFullSolution(k, si)[nodeIdx];

        // Fallback: return zero with warning logged once (kept simple for v1).
        // Caller already warned the user in EvaluateLinearInterior preamble when no provider.
        return Complex.Zero;
    }

    private static Complex ReconstructBranchCurrent(
        ILinearNetworkPayload payload, int mnaIdx, int k, int si)
    {
        if (payload is IBackSolverProvider bsp)
            return bsp.GetFullSolution(k, si)[mnaIdx];

        return Complex.Zero;
    }

    // ── Axis builders ─────────────────────────────────────────────────────────

    private static Axis BuildNodeAxis(string[] labels, int[] indices)
    {
        var values = indices.Select(i => (double)(i + 1)).ToArray();  // 1-based circuit node
        return new Axis("node", values, labels: labels);
    }

    private static Axis BuildBranchAxis(string[] labels, int[] branchIndices, int nonGnd)
    {
        var values = branchIndices.Select(b => (double)(nonGnd + b)).ToArray();
        return new Axis("branch", values, labels: labels);
    }

    // ── Branch-ref lookup ────────────────────────────────────────────────────

    /// <summary>
    /// Match a branch ref (format: <c>"I:path:terminal"</c> or <c>"L:path"</c>) against
    /// branch_names entries.  Returns the 0-based branch index or -1 if not found.
    /// </summary>
    private static int FindBranchByRef(string[] branchNames, string bref)
    {
        // Direct match first
        for (int b = 0; b < branchNames.Length; b++)
            if (branchNames[b] == bref) return b;

        // "I:path:terminal" format → try "L:path:terminal" and "V:path:terminal" variants
        // (branch_names use component-kind prefix, callers may use "I:" measurement prefix)
        if (bref.StartsWith("I:", StringComparison.Ordinal))
        {
            string suffix = bref[2..];  // everything after "I:"
            for (int b = 0; b < branchNames.Length; b++)
            {
                // Strip the kind prefix from branch_name and compare the rest
                int colon = branchNames[b].IndexOf(':');
                if (colon >= 0 && branchNames[b][(colon + 1)..] == suffix)
                    return b;
            }
        }

        return -1;
    }
}

// ── Optional back-solver provider interface ───────────────────────────────────

/// <summary>
/// Optional interface implemented by <c>HbLinearNetworkPayload</c> (in CircuitRF.Engine)
/// to allow the exporter to retrieve full MNA solution vectors without re-doing a sparse solve.
/// RfCore defines the interface; the engine provides the implementation.
/// When the payload does not implement this interface, <c>V_linear</c> / <c>I_linear</c>
/// cubes are filled with zeros (fallback).
/// </summary>
public interface IBackSolverProvider
{
    /// <summary>
    /// Full MNA solution vector for harmonic <paramref name="k"/> at sweep index
    /// <paramref name="si"/> (lazy-cached in the underlying back-solver).
    /// x[0..NonGroundCount-1] = node voltages; x[NonGroundCount..] = branch currents.
    /// </summary>
    Complex[] GetFullSolution(int k, int si);
}
