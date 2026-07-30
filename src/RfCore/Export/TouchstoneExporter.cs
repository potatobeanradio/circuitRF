// ================================================================
//  TouchstoneExporter.cs  —  S-cube → Touchstone file(s)
//
//  Slices the grouped S DataCube to [freq, i, j], renormalises to a
//  user-specified real Z0 when necessary, and writes via TouchstoneIO.
//
//  Framework-free (no Avalonia dependency).
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using NumFlat;
using RfCore.Data;

namespace RfCore.Export;

// ── Options & result types ────────────────────────────────────────────────────

public sealed record TouchstoneExportOptions(
    double       Z0Ohms,
    int          Digits,
    char         DigitFormat,   // 'f' | 'g' | 'e'
    MatrixFormat MatrixFormat);

public enum TouchstoneExportStatus { Ok, NoSCube, NameCollision }

public sealed record TouchstoneExportResult(
    TouchstoneExportStatus   Status,
    IReadOnlyList<string>    WrittenPaths,
    IReadOnlyList<string>    CollidingNames,
    Z0Kind                   SourceZ0Kind,
    bool                     Renormalized);

public sealed record TouchstoneInspectResult(
    IReadOnlyList<Axis> SweepAxes,
    Z0Kind              SourceZ0Kind);

// ── Exporter ─────────────────────────────────────────────────────────────────

public static class TouchstoneExporter
{
    /// <summary>
    /// Inspect the S cube in the given analysis group: returns sweep axes
    /// (non-freq/i/j) and the Z0Kind of the group's Z0 cube.
    /// Returns default result (empty sweep axes, UniformReal) when no S cube exists.
    /// </summary>
    public static TouchstoneInspectResult Inspect(DataSet ds, string group)
    {
        if (!ds.ContainsGroup(group))
            return new TouchstoneInspectResult(Array.Empty<Axis>(), Z0Kind.UniformReal);

        var cubes = ds.CubesIn(group);
        if (!cubes.TryGetValue("S", out var sCube))
            return new TouchstoneInspectResult(Array.Empty<Axis>(), Z0Kind.UniformReal);

        var sweepAxes = FindSweepAxes(sCube);

        var z0Kind = Z0Kind.UniformReal;
        if (cubes.TryGetValue("Z0", out var z0Cube))
            z0Kind = DataSetBuilder.ClassifyZ0(z0Cube);

        return new TouchstoneInspectResult(sweepAxes.AsReadOnly(), z0Kind);
    }

    /// <summary>
    /// Export the S cube in the given analysis group to one or more Touchstone files.
    /// <para>
    /// Single file: pin every sweep axis via <paramref name="pinnedIndexByAxis"/>.
    /// All-sweep: <paramref name="allSweepFiles"/> = true → Cartesian product over all
    /// sweep axes, one file per combination.
    /// </para>
    /// <para>
    /// Collision check is performed before any file is written.  If two combinations
    /// produce the same target path, <see cref="TouchstoneExportStatus.NameCollision"/>
    /// is returned and nothing is written.
    /// </para>
    /// </summary>
    public static TouchstoneExportResult Export(
        DataSet                       ds,
        string                        group,
        TouchstoneExportOptions       opts,
        IReadOnlyDictionary<string,int> pinnedIndexByAxis,
        bool                          allSweepFiles,
        string                        baseFilePathNoSuffix)
    {
        if (!ds.ContainsGroup(group))
            return new TouchstoneExportResult(
                TouchstoneExportStatus.NoSCube,
                Array.Empty<string>(), Array.Empty<string>(),
                Z0Kind.UniformReal, false);

        var cubes = ds.CubesIn(group);

        if (!cubes.TryGetValue("S", out var sCube))
            return new TouchstoneExportResult(
                TouchstoneExportStatus.NoSCube,
                Array.Empty<string>(), Array.Empty<string>(),
                Z0Kind.UniformReal, false);

        // Locate network axes by name
        int freqDim = FindDim(sCube, "freq");
        int iDim    = FindDim(sCube, "i");
        int jDim    = FindDim(sCube, "j");
        int nPorts  = sCube.Axes[iDim].Length;

        var sweepAxes = FindSweepAxes(sCube);

        // Per-port source Z0
        Complex[] srcZ0;
        Z0Kind    z0Kind;
        if (cubes.TryGetValue("Z0", out var z0Cube))
        {
            srcZ0  = z0Cube.ComplexValues;
            z0Kind = DataSetBuilder.ClassifyZ0(z0Cube);
        }
        else
        {
            srcZ0  = RFNetwork.Z0Array(new Complex(50, 0), nPorts);
            z0Kind = Z0Kind.UniformReal;
        }

        var toZ0 = RFNetwork.Z0Array(new Complex(opts.Z0Ohms, 0), nPorts);
        bool renormNeeded = NeedsRenorm(z0Kind, srcZ0, opts.Z0Ohms);

        // Build all sweep-index combinations
        var combos = allSweepFiles
            ? CartesianProduct(sweepAxes)
            : new List<int[]> { BuildSingleCombo(sweepAxes, pinnedIndexByAxis) };

        string ext = $".s{nPorts}p";

        // Build target paths and collision-check before writing anything
        var targetPaths = new List<string>(combos.Count);
        foreach (var combo in combos)
        {
            string suffix = allSweepFiles
                ? BuildSuffix(combo, sweepAxes)
                : string.Empty;
            targetPaths.Add(baseFilePathNoSuffix + suffix + ext);
        }

        var collisions = targetPaths
            .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (collisions.Count > 0)
            return new TouchstoneExportResult(
                TouchstoneExportStatus.NameCollision,
                Array.Empty<string>(), collisions, z0Kind, false);

        // Write files
        string precision = $"{char.ToUpperInvariant(opts.DigitFormat)}{opts.Digits}";
        var writtenPaths = new List<string>(combos.Count);
        bool anyRenorm   = false;

        for (int ci = 0; ci < combos.Count; ci++)
        {
            var combo  = combos[ci];
            string path = targetPaths[ci];

            DataCube sliced = SliceToCube(sCube, freqDim, iDim, jDim, sweepAxes, combo);

            // After slicing, find axis positions by name (order preserved)
            int sFreq = FindDim(sliced, "freq");
            int sI    = FindDim(sliced, "i");
            int sJ    = FindDim(sliced, "j");
            int nFreq = sliced.Axes[sFreq].Length;
            double[] freqs = sliced.Axes[sFreq].Values;

            int[] strides = ComputeStrides(sliced);

            var vals = sliced.ComplexValues;
            var mats = new Mat<Complex>[nFreq];
            for (int fi = 0; fi < nFreq; fi++)
            {
                mats[fi] = new Mat<Complex>(nPorts, nPorts);
                for (int r = 0; r < nPorts; r++)
                for (int c = 0; c < nPorts; c++)
                {
                    int flat = fi * strides[sFreq] + r * strides[sI] + c * strides[sJ];
                    mats[fi][r, c] = vals[flat];
                }
            }

            if (renormNeeded)
            {
                anyRenorm = true;
                for (int fi = 0; fi < nFreq; fi++)
                    mats[fi] = RFNetwork.SToS(mats[fi], srcZ0, toZ0);
            }

            var snp = new SNP(freqs, mats, MatrixType.S, opts.MatrixFormat,
                              new Complex(opts.Z0Ohms, 0));

            // Ensure parent directory exists
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            TouchstoneIO.WriteFile(snp, path,
                formatOverride:         opts.MatrixFormat,
                touchstone11Compatible: true,
                precision:              precision);

            writtenPaths.Add(path);
        }

        return new TouchstoneExportResult(
            TouchstoneExportStatus.Ok, writtenPaths, Array.Empty<string>(),
            z0Kind, anyRenorm);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static int FindDim(DataCube cube, string name)
    {
        for (int d = 0; d < cube.Axes.Count; d++)
            if (cube.Axes[d].Name == name) return d;
        throw new ArgumentException($"Axis '{name}' not found in DataCube.");
    }

    private static List<Axis> FindSweepAxes(DataCube sCube)
    {
        var result = new List<Axis>();
        for (int d = 0; d < sCube.Axes.Count; d++)
        {
            string n = sCube.Axes[d].Name;
            if (n != "freq" && n != "i" && n != "j")
                result.Add(sCube.Axes[d]);
        }
        return result;
    }

    private static int[] BuildSingleCombo(
        List<Axis> sweepAxes,
        IReadOnlyDictionary<string, int> pinnedIndexByAxis)
    {
        var combo = new int[sweepAxes.Count];
        for (int s = 0; s < sweepAxes.Count; s++)
        {
            combo[s] = pinnedIndexByAxis.TryGetValue(sweepAxes[s].Name, out var idx)
                ? idx : 0;
        }
        return combo;
    }

    private static List<int[]> CartesianProduct(List<Axis> sweepAxes)
    {
        var result = new List<int[]> { Array.Empty<int>() };
        foreach (var ax in sweepAxes)
        {
            var next = new List<int[]>(result.Count * ax.Length);
            foreach (var prefix in result)
            {
                for (int k = 0; k < ax.Length; k++)
                {
                    var extended = new int[prefix.Length + 1];
                    prefix.CopyTo(extended, 0);
                    extended[prefix.Length] = k;
                    next.Add(extended);
                }
            }
            result = next;
        }
        return result;
    }

    private static DataCube SliceToCube(
        DataCube sCube,
        int freqDim, int iDim, int jDim,
        List<Axis> sweepAxes,
        int[] combo)
    {
        var sliceArgs = new object[sCube.Rank];
        int sweepIdx = 0;
        for (int d = 0; d < sCube.Rank; d++)
        {
            if (d == freqDim || d == iDim || d == jDim)
                sliceArgs[d] = Range.All;
            else
                sliceArgs[d] = combo[sweepIdx++];
        }

        DataCube sliced = sCube[sliceArgs];  // SliceResult → DataCube
        return sliced;
    }

    private static int[] ComputeStrides(DataCube cube)
    {
        int rank    = cube.Axes.Count;
        var strides = new int[rank];
        if (rank == 0) return strides;
        strides[rank - 1] = 1;
        for (int d = rank - 2; d >= 0; d--)
            strides[d] = strides[d + 1] * cube.Axes[d + 1].Length;
        return strides;
    }

    private static bool NeedsRenorm(Z0Kind kind, Complex[] srcZ0, double targetOhms)
    {
        if (kind != Z0Kind.UniformReal) return true;
        if (srcZ0.Length == 0) return false;
        return Math.Abs(srcZ0[0].Real - targetOhms) > 1e-9;
    }

    private static string BuildSuffix(int[] combo, List<Axis> sweepAxes)
    {
        var sb = new System.Text.StringBuilder();
        for (int s = 0; s < sweepAxes.Count; s++)
        {
            var ax  = sweepAxes[s];
            int idx = combo[s];

            string valStr = ax.Labels != null
                ? ax.Labels[idx]
                : ax.Values[idx].ToString(CultureInfo.InvariantCulture);

            string unit = ax.Unit ?? string.Empty;

            sb.Append("__");
            sb.Append(Sanitize(ax.Name));
            sb.Append('=');
            sb.Append(Sanitize(valStr));
            if (!string.IsNullOrEmpty(unit))
                sb.Append(Sanitize(unit));
        }
        return sb.ToString();
    }

    private static string Sanitize(string s)
    {
        // Drop path-separator, wildcard, and quote chars; spaces → _
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
        {
            switch (c)
            {
                case '/': case '\\': case ':': case '*': case '?':
                case '"': case '<':  case '>': case '|': case '\'':
                    break;
                case ' ':
                    sb.Append('_');
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
