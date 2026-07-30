// ================================================================
//  MatWriter.cs  —  MATLAB v7.3 / HDF5 exporter
//
//  Writes a DataSet as an HDF5 v7.3 (.mat) file using PureHDF
//  (pure managed C#, MIT license, no native dependencies).
//
//  HDF5 layout (data-export.md §2.2):
//    /dataset/
//      <cube>               — dataset per DataCube
//      __axes__/<cube>/axes.json  — JSON axis metadata
//      __linear_network__/  — present when IncludeLinearNetwork = true
//        omegas, G_rows, G_cols, G_data, bSrc, iNl,
//        interface_nodes, node_names, branch_names,
//        mna_size, non_ground_count
//
//  Complex cubes are encoded as compound type {real:f64, imag:f64}
//  (the encoding MATLAB and h5py both read natively as complex double).
//
//  Real cubes are plain float64.
//
//  Cube names with '/' are escaped as '__slash__' (valid HDF5 name
//  character; colons ':' are legal in HDF5 and MATLAB h5read).
//
//  See docs/design/data-export.md §2.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using PureHDF;
using RfCore.Data;

namespace RfCore.Export;

/// <summary>
/// Writes a <see cref="DataSet"/> to a MATLAB v7.3 / HDF5 <c>.mat</c> file.
/// Internal — call via <see cref="DataSetExporter.Export"/>.
/// </summary>
internal static class MatWriter
{
    /// <summary>
    /// Format version written as <c>/dataset/format_version</c>.
    /// Increment when the file layout changes.  Consumers should reject mismatches.
    /// </summary>
    internal const int FormatVersion = 2;
    // ── Compound type for complex double (MATLAB-compatible field names) ──────

    /// <summary>
    /// HDF5 compound type that MATLAB reads as complex double.
    /// Field names must be lowercase "real" and "imag" for MATLAB compatibility.
    /// Proved functional in PureHdfSpikeTests (Phase 5-7 Condition 1).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ComplexEntry
    {
        public double real;
        public double imag;

        public ComplexEntry(Complex c) { real = c.Real; imag = c.Imaginary; }
    }

    // ── Entry point ──────────────────────────────────────────────────────────

    public static void Write(
        string                path,
        DataSet               ds,
        ExportOptions         opts,
        ILinearNetworkPayload? payload)
    {
        var datasetGroup  = new H5Group();
        var axesGroup     = new H5Group();
        datasetGroup["__axes__"] = axesGroup;

        // Format version — scalar int64 at /dataset/format_version
        datasetGroup["format_version"] = MakeShapedArray(new long[] { (long)FormatVersion }, Array.Empty<int>());

        // ── Cube datasets (grouped) ──────────────────────────────────────────
        foreach (var group in ds.Groups)
        {
            H5Group target, axesTarget;
            if (group == DataSet.DefaultGroup)
            {
                target     = datasetGroup;
                axesTarget = axesGroup;
            }
            else
            {
                target     = new H5Group(); datasetGroup[EscapeName(group)] = target;
                axesTarget = new H5Group(); axesGroup[EscapeName(group)]    = axesTarget;
            }

            foreach (var kvp in ds.CubesIn(group))
            {
                string h = EscapeName(kvp.Key);
                target[h] = MakeCubeDataset(kvp.Value);
                var cag   = new H5Group(); axesTarget[h] = cag;
                cag["axes.json"] = new string[] { BuildAxesJson(kvp.Value) };
            }
        }

        // Ordered group-name list for readers that need to enumerate groups
        datasetGroup["groups"] = ds.Groups.ToArray();

        // ── Linear-network group ─────────────────────────────────────────────
        if (opts.IncludeLinearNetwork && payload != null)
        {
            datasetGroup["__linear_network__"] = BuildLinearNetworkGroup(payload);
        }

        // ── Write file ───────────────────────────────────────────────────────
        var root = new H5File { ["dataset"] = datasetGroup };
        root.Write(path);
    }

    // ── Cube dataset construction ────────────────────────────────────────────

    private static object MakeCubeDataset(DataCube cube)
    {
        int[] shape = cube.Axes.Select(a => a.Length).ToArray();
        int   total = shape.Length == 0 ? 1 : shape.Aggregate(1, (acc, d) => acc * d);

        if (cube.DataKind == DataKind.Complex)
        {
            var raw     = cube.ComplexValues;     // flat [total], row-major
            var entries = new ComplexEntry[total];
            for (int i = 0; i < total; i++)
                entries[i] = new ComplexEntry(raw[i]);

            return MakeShapedArray(entries, shape);
        }
        else
        {
            var raw = cube.RealValues;            // flat [total], row-major
            return MakeShapedArray(raw, shape);
        }
    }

    /// <summary>
    /// Create a CLR multi-dimensional array of the correct rank so PureHDF's
    /// reflection-based writer produces the right HDF5 shape.
    ///
    /// PureHDF determines the dataset dimensionality from the CLR array rank, so
    /// we must pass a <c>T[,]</c> for 2-D, <c>T[,,]</c> for 3-D, etc.
    /// <c>Array.CreateInstance</c> achieves this without compile-time rank knowledge.
    /// Scalars (empty shape) are written as 1-D <c>T[1]</c>.
    /// </summary>
    private static object MakeShapedArray<T>(T[] flat, int[] shape)
    {
        if (shape.Length == 0)
        {
            // Scalar: write as 1-D dataset with one element.
            return new T[] { flat[0] };
        }

        if (shape.Length == 1)
        {
            // 1-D: return the flat array directly (T[] matches rank-1).
            return flat;
        }

        // N-D (N ≥ 2): create a rank-N array and fill via strides.
        Array ndArr = Array.CreateInstance(typeof(T), shape);
        int[] stride = new int[shape.Length];
        stride[shape.Length - 1] = 1;
        for (int d = shape.Length - 2; d >= 0; d--)
            stride[d] = stride[d + 1] * shape[d + 1];

        var coords = new int[shape.Length];
        for (int flatIdx = 0; flatIdx < flat.Length; flatIdx++)
        {
            int tmp = flatIdx;
            for (int d = shape.Length - 1; d >= 0; d--)
            {
                coords[d] = tmp % shape[d];
                tmp       /= shape[d];
            }
            ndArr.SetValue(flat[flatIdx], coords);
        }
        return ndArr;
    }

    // ── Linear-network group ─────────────────────────────────────────────────

    private static H5Group BuildLinearNetworkGroup(ILinearNetworkPayload p)
    {
        int K1 = p.HarmonicCount;
        int S  = p.SweepCount;
        int M  = p.MnaSize;
        int N  = p.InterfaceCount;

        // Sparse G: union sparsity pattern across all harmonics.
        // G(DC) has fewer nonzeros than G(fundamental+) because capacitors are open
        // at DC (ω=0). Using k=0 alone would undercount and corrupt G_data entries.
        var (canonRows, canonCols) = BuildCanonicalPattern(p, K1);
        int nnz = canonRows.Length;

        // omegas — float64[K+1]
        double[] omegas = p.Omegas;

        // G_rows, G_cols — int32[nnz]
        int[] gRows = canonRows;
        int[] gCols = canonCols;

        // G_data — complex128[K+1, nnz] — flatten as [K1*nnz], row-major.
        // Use canonical pattern with lookup; entries absent from a harmonic (e.g.
        // capacitors at DC where ω₀=0) are zero-padded — physically correct.
        var gDataFlat = new ComplexEntry[K1 * nnz];
        for (int k = 0; k < K1; k++)
        {
            var (rk, ck, dk) = p.GetSparseG(k);
            var lookup = new Dictionary<(int, int), Complex>(rk.Length);
            for (int i = 0; i < rk.Length; i++) lookup[(rk[i], ck[i])] = dk[i];
            for (int nz = 0; nz < nnz; nz++)
            {
                var val = lookup.TryGetValue((canonRows[nz], canonCols[nz]), out var c)
                    ? c : Complex.Zero;
                gDataFlat[k * nnz + nz] = new ComplexEntry(val);
            }
        }

        // bSrc — complex128[S, K+1, mnaSize] — flatten as [S*K1*M]
        var bSrcFlat = new ComplexEntry[S * K1 * M];
        for (int si = 0; si < S; si++)
        for (int k  = 0; k  < K1; k++)
        for (int m  = 0; m  < M; m++)
            bSrcFlat[(si * K1 + k) * M + m] = new ComplexEntry(p.GetBSrc(si, k, m));

        // iNl — complex128[S, K+1, N_interface] — flatten as [S*K1*N]
        var iNlFlat = new ComplexEntry[S * K1 * N];
        for (int si = 0; si < S; si++)
        for (int k  = 0; k  < K1; k++)
        for (int n  = 0; n  < N; n++)
            iNlFlat[(si * K1 + k) * N + n] = new ComplexEntry(p.GetINl(si, n, k));

        // interface_nodes — int32[N]
        int[] ifaceNodes = p.InterfaceNodes;

        // name maps
        string[] nodeNames   = p.NodeNames;
        string[] branchNames = p.BranchNames;

        // scalar metadata
        long[] mnaSizeArr        = { (long)p.MnaSize };
        long[] nonGroundCountArr = { (long)p.NonGroundCount };

        return new H5Group
        {
            ["omegas"]           = MakeShapedArray(omegas,          new[] { K1 }),
            ["G_rows"]           = MakeShapedArray(gRows,           new[] { nnz }),
            ["G_cols"]           = MakeShapedArray(gCols,           new[] { nnz }),
            ["G_data"]           = MakeShapedArray(gDataFlat,       new[] { K1, nnz }),
            ["bSrc"]             = MakeShapedArray(bSrcFlat,        new[] { S, K1, M }),
            ["iNl"]              = MakeShapedArray(iNlFlat,         new[] { S, K1, N }),
            ["interface_nodes"]  = MakeShapedArray(ifaceNodes,      new[] { N }),
            ["node_names"]       = (object)nodeNames,
            ["branch_names"]     = (object)branchNames,
            ["mna_size"]         = MakeShapedArray(mnaSizeArr,      Array.Empty<int>()),
            ["non_ground_count"] = MakeShapedArray(nonGroundCountArr, Array.Empty<int>()),
        };
    }

    // ── Sparse-G canonical pattern ────────────────────────────────────────────

    /// <summary>
    /// Build the union (row, col) sparsity pattern across all harmonics k=0..K1-1,
    /// sorted by (row, col).  G(DC) typically omits capacitor entries (ω₀=0 → open
    /// circuit), so the union is larger than k=0 alone.
    /// </summary>
    private static (int[] rows, int[] cols) BuildCanonicalPattern(
        ILinearNetworkPayload p, int K1)
    {
        var seen  = new HashSet<(int, int)>();
        var pairs = new List<(int row, int col)>();
        for (int k = 0; k < K1; k++)
        {
            var (r, c, _) = p.GetSparseG(k);
            for (int i = 0; i < r.Length; i++)
            {
                if (seen.Add((r[i], c[i])))
                    pairs.Add((r[i], c[i]));
            }
        }
        pairs.Sort((a, b) => a.row != b.row ? a.row.CompareTo(b.row) : a.col.CompareTo(b.col));
        return (pairs.Select(p => p.row).ToArray(),
                pairs.Select(p => p.col).ToArray());
    }

    // ── Axis JSON ────────────────────────────────────────────────────────────

    private static string BuildAxesJson(DataCube cube)
    {
        // Produce: [{"name":"node","unit":"","values":[...],"labels":[...]}, ...]
        var sb = new StringBuilder();
        sb.Append('[');
        bool first = true;
        foreach (var ax in cube.Axes)
        {
            if (!first) sb.Append(',');
            first = false;

            sb.Append("{\"name\":\"");
            AppendJsonString(sb, ax.Name);
            sb.Append("\",\"unit\":\"");
            AppendJsonString(sb, ax.Unit);
            sb.Append("\",\"values\":[");
            for (int i = 0; i < ax.Values.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(ax.Values[i].ToString("G17",
                    System.Globalization.CultureInfo.InvariantCulture));
            }
            sb.Append(']');

            if (ax.Labels != null)
            {
                sb.Append(",\"labels\":[");
                for (int i = 0; i < ax.Labels.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"');
                    AppendJsonString(sb, ax.Labels[i]);
                    sb.Append('"');
                }
                sb.Append(']');
            }
            sb.Append('}');
        }
        sb.Append(']');
        return sb.ToString();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string EscapeName(string name) => name.Replace("/", "__slash__");

    private static void AppendJsonString(StringBuilder sb, string s)
    {
        foreach (char c in s)
        {
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:
                    if (c < 0x20) sb.Append($"\\u{(int)c:x4}");
                    else sb.Append(c);
                    break;
            }
        }
    }
}
