// ================================================================
//  NpyWriter.cs  —  NumPy .npy structured-array exporter
//
//  Writes a DataSet as a single .npy file containing a (1,)-shaped
//  NumPy structured array.  Each DataCube becomes a named sub-array
//  field.  Axis metadata is stored in a '__meta__' bytes field (JSON).
//  When IncludeLinearNetwork is true, '__linnet_*' fields carry the
//  per-harmonic linear MNA system (G, bSrc, iNl, index maps).
//
//  Format reference: NumPy NDArray format spec v1.0 / v2.0
//  (https://numpy.org/doc/stable/reference/generated/numpy.lib.format.html)
//
//  Consumer access pattern:
//      arr    = np.load('result.npy', allow_pickle=False)
//      meta   = json.loads(arr['__meta__'][0])
//      V      = arr['V'][0]         # shape (nNodes, nHarm, nPin)
//      omegas = arr['__linnet_omegas'][0]
//
//  See docs/design/data-export.md §3.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using RfCore.Data;

namespace RfCore.Export;

/// <summary>
/// Writes a <see cref="DataSet"/> to a NumPy <c>.npy</c> packed structured array.
/// Internal — call via <see cref="DataSetExporter.Export"/>.
/// </summary>
internal static class NpyWriter
{
    /// <summary>
    /// Format version written into the <c>__meta__</c> JSON and checked by <see cref="NpyReader"/>.
    /// Increment when the file layout changes.  The importer rejects any mismatch.
    /// </summary>
    internal const int FormatVersion = 2;

    // ── NumPy magic & version constants ─────────────────────────────────────

    private static readonly byte[] Magic = { 0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y' };

    // ── Cube field mapping ───────────────────────────────────────────────────

    // Maps each cube to its unique NumPy field name (opaque key — do not parse for group/cube).
    private readonly record struct CubeMapping(string FieldName, string Group, string CubeName);

    // ── Public entry point ───────────────────────────────────────────────────

    public static void Write(
        string                path,
        DataSet               ds,
        ExportOptions         opts,
        ILinearNetworkPayload? payload)
    {
        // 0. Build cube → field-name mappings once; shared by meta + data passes.
        var cubeMappings = BuildCubeMappings(ds);

        // 1. Build JSON metadata first — need byte count for |S<N> dtype.
        string metaJson  = BuildMetaJson(ds, opts, payload, cubeMappings);
        byte[] metaBytes = Encoding.UTF8.GetBytes(metaJson);

        // 2. Collect field descriptors (name, numpy-dtype, int[] shape).
        var fields = CollectFields(ds, metaBytes.Length, opts, payload, cubeMappings);

        // 3. Build the Python dict header string (without padding — needed first to
        //    compute length then pad).
        string rawHeader = BuildHeaderString(fields);
        byte[] rawHeaderBytes = Encoding.ASCII.GetBytes(rawHeader);

        // 4. Choose format version: v1 uses uint16 HEADER_LEN, v2 uses uint32.
        //    v1 preamble = 10, v2 preamble = 12.  Header must be padded so that
        //    (preambleLen + headerLen) % 64 == 0.
        bool useV2    = rawHeaderBytes.Length + 10 + 1 > 65535; // pessimistic
        int  preamble = useV2 ? 12 : 10;

        // Padding is spaces inserted before the final \n.
        int need   = (preamble + rawHeaderBytes.Length) % 64;
        int spaces = (need == 0) ? 0 : (64 - need);

        string paddedHeader      = rawHeader.TrimEnd('\n') + new string(' ', spaces) + '\n';
        byte[] paddedHeaderBytes = Encoding.ASCII.GetBytes(paddedHeader);

        // Re-check version with padded length (could push over uint16 limit, extremely unlikely).
        useV2    = paddedHeaderBytes.Length + 10 > 65535;
        preamble = useV2 ? 12 : 10;

        // 5. Write the file.
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var w  = new BinaryWriter(fs, Encoding.ASCII, leaveOpen: false);

        w.Write(Magic);                     // 6 bytes: magic
        w.Write((byte)(useV2 ? 2 : 1));    // major version
        w.Write((byte)0);                   // minor version
        if (useV2)
            w.Write((uint)paddedHeaderBytes.Length);
        else
            w.Write((ushort)paddedHeaderBytes.Length);
        w.Write(paddedHeaderBytes);

        // 6. Write data for each field in declaration order.
        WriteFieldData(w, ds, metaBytes, opts, payload, fields, cubeMappings);
    }

    // ── Field descriptor ────────────────────────────────────────────────────

    private sealed record FieldDesc(
        string   Name,    // numpy field name (unique, opaque — do not parse for group/cube)
        string   Dtype,   // numpy dtype string: '<c16', '<f8', '<i4', '<i8', '|S<N>'
        int[]    Shape);  // sub-array shape; empty = scalar field

    private static string EscapeName(string name) => name.Replace("/", "__slash__");

    // ── Cube → field-name mapping ─────────────────────────────────────────────

    /// <summary>
    /// Assign a unique NumPy field name to every cube in the DataSet.
    /// The field name is an opaque key; the authoritative (group, cube) pair lives in __meta__.
    /// Uniquification: base = EscapeName(cube); on collision: EscapeName(group)+"."+EscapeName(cube);
    /// on further collision: append "~N".
    /// </summary>
    private static List<CubeMapping> BuildCubeMappings(DataSet ds)
    {
        var mappings  = new List<CubeMapping>();
        var usedNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var group in ds.Groups)
        {
            foreach (var kvp in ds.CubesIn(group))
            {
                string cubeName = kvp.Key;
                string fieldName = EscapeName(cubeName);

                if (usedNames.Contains(fieldName))
                    fieldName = EscapeName(group) + "." + EscapeName(cubeName);

                if (usedNames.Contains(fieldName))
                {
                    int n = 2;
                    string candidate;
                    do { candidate = EscapeName(group) + "." + EscapeName(cubeName) + "~" + n++; }
                    while (usedNames.Contains(candidate));
                    fieldName = candidate;
                }

                usedNames.Add(fieldName);
                mappings.Add(new CubeMapping(fieldName, group, cubeName));
            }
        }
        return mappings;
    }

    // ── Field collection ─────────────────────────────────────────────────────

    private static List<FieldDesc> CollectFields(
        DataSet               ds,
        int                   metaByteCount,
        ExportOptions         opts,
        ILinearNetworkPayload? payload,
        List<CubeMapping>     cubeMappings)
    {
        var list = new List<FieldDesc>();

        // Cube fields — in group order as recorded by BuildCubeMappings.
        foreach (var m in cubeMappings)
        {
            var    cube  = ds.CubesIn(m.Group)[m.CubeName];
            string dtype = cube.DataKind == DataKind.Complex ? "<c16" : "<f8";
            var    shape = cube.Axes.Select(a => a.Length).ToArray();
            list.Add(new FieldDesc(m.FieldName, dtype, shape));
        }

        // Metadata field — fixed-length byte string sized to exact JSON length.
        list.Add(new FieldDesc("__meta__", $"|S{metaByteCount}", Array.Empty<int>()));

        // Linear-network fields
        if (opts.IncludeLinearNetwork && payload != null)
        {
            // nnz = union of all harmonics' sparsity patterns.
            var (canonRows, _) = BuildCanonicalPattern(payload, payload.HarmonicCount);
            int nnz = canonRows.Length;
            int K1  = payload.HarmonicCount;
            int S   = payload.SweepCount;
            int M   = payload.MnaSize;
            int N   = payload.InterfaceCount;

            list.Add(new FieldDesc("__linnet_omegas",          "<f8", new[] { K1 }));
            list.Add(new FieldDesc("__linnet_G_rows",          "<i4", new[] { nnz }));
            list.Add(new FieldDesc("__linnet_G_cols",          "<i4", new[] { nnz }));
            list.Add(new FieldDesc("__linnet_G_data",          "<c16", new[] { K1, nnz }));
            list.Add(new FieldDesc("__linnet_bSrc",            "<c16", new[] { S, K1, M }));
            list.Add(new FieldDesc("__linnet_iNl",             "<c16", new[] { S, K1, N }));
            list.Add(new FieldDesc("__linnet_interface_nodes", "<i4",  new[] { N }));
            list.Add(new FieldDesc("__linnet_mna_size",        "<i8",  Array.Empty<int>()));
            list.Add(new FieldDesc("__linnet_non_ground_count","<i8",  Array.Empty<int>()));
        }

        return list;
    }

    // ── Header string ────────────────────────────────────────────────────────

    private static string BuildHeaderString(List<FieldDesc> fields)
    {
        // Produce: {'descr': [...], 'fortran_order': False, 'shape': (1,), }
        var sb = new StringBuilder();
        sb.Append("{'descr': [");

        for (int i = 0; i < fields.Count; i++)
        {
            var f = fields[i];
            if (i > 0) sb.Append(", ");
            if (f.Shape.Length == 0)
            {
                // Scalar field: ('name', 'dtype')
                sb.Append($"('{PyEscape(f.Name)}', '{f.Dtype}')");
            }
            else if (f.Shape.Length == 1)
            {
                // 1-D field: ('name', 'dtype', (N,))   trailing comma required
                sb.Append($"('{PyEscape(f.Name)}', '{f.Dtype}', ({f.Shape[0]},))");
            }
            else
            {
                // Multi-D field: ('name', 'dtype', (d0, d1, ...))
                string shapeTuple = "(" + string.Join(", ", f.Shape) + ")";
                sb.Append($"('{PyEscape(f.Name)}', '{f.Dtype}', {shapeTuple})");
            }
        }

        sb.Append("], 'fortran_order': False, 'shape': (1,), }");
        sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>Escape a field name for use inside a Python single-quoted string.</summary>
    private static string PyEscape(string s) =>
        s.Replace("\\", "\\\\").Replace("'", "\\'");

    // ── Data writing ─────────────────────────────────────────────────────────

    private static void WriteFieldData(
        BinaryWriter          w,
        DataSet               ds,
        byte[]                metaBytes,
        ExportOptions         opts,
        ILinearNetworkPayload? payload,
        List<FieldDesc>       fields,
        List<CubeMapping>     cubeMappings)
    {
        // Build a field-name → mapping lookup for O(1) access.
        var cubeByField = cubeMappings.ToDictionary(m => m.FieldName, m => m);

        // The structured array has shape (1,) — write exactly ONE element of the struct.
        // Fields are laid out sequentially, each field as C-order row-major bytes.

        foreach (var f in fields)
        {
            if (f.Name == "__meta__")
            {
                // Fixed-length byte string — write exact bytes (length was sized to match)
                w.Write(metaBytes);
                continue;
            }

            if (f.Name.StartsWith("__linnet_", StringComparison.Ordinal))
            {
                WriteLinnetField(w, f, payload!);
                continue;
            }

            // Regular DataCube field — look up via the cube mapping.
            var m    = cubeByField[f.Name];
            var cube = ds.CubesIn(m.Group)[m.CubeName];
            WriteCubeData(w, cube);
        }
    }

    private static void WriteCubeData(BinaryWriter w, DataCube cube)
    {
        if (cube.DataKind == DataKind.Complex)
        {
            // complex128 == two float64s (real, imag) in System.Numerics.Complex layout
            // which on little-endian matches NumPy '<c16' directly.
            var data = cube.ComplexValues;  // cloned copy
            foreach (var c in data)
            {
                w.Write(c.Real);
                w.Write(c.Imaginary);
            }
        }
        else
        {
            var data = cube.RealValues;
            foreach (var v in data) w.Write(v);
        }
    }

    private static void WriteLinnetField(
        BinaryWriter          w,
        FieldDesc             f,
        ILinearNetworkPayload p)
    {
        switch (f.Name)
        {
            case "__linnet_omegas":
            {
                foreach (var v in p.Omegas) w.Write(v);
                break;
            }

            case "__linnet_G_rows":
            {
                var (rows, _) = BuildCanonicalPattern(p, p.HarmonicCount);
                foreach (var v in rows) w.Write(v);
                break;
            }

            case "__linnet_G_cols":
            {
                var (_, cols) = BuildCanonicalPattern(p, p.HarmonicCount);
                foreach (var v in cols) w.Write(v);
                break;
            }

            case "__linnet_G_data":
            {
                // Shape [K+1, nnz], row-major.  Use the canonical union sparsity pattern so
                // every harmonic's slice is the same nnz.  Positions absent from harmonic k
                // (e.g. capacitors at DC where ω₀=0) are zero-padded — physically correct.
                int K1 = p.HarmonicCount;
                var (canonRows, canonCols) = BuildCanonicalPattern(p, K1);
                int nnz = canonRows.Length;
                for (int k = 0; k < K1; k++)
                {
                    var (rk, ck, dk) = p.GetSparseG(k);
                    var lookup = new Dictionary<(int, int), Complex>(rk.Length);
                    for (int i = 0; i < rk.Length; i++) lookup[(rk[i], ck[i])] = dk[i];
                    for (int nz = 0; nz < nnz; nz++)
                    {
                        var val = lookup.TryGetValue((canonRows[nz], canonCols[nz]), out var c)
                            ? c : Complex.Zero;
                        w.Write(val.Real);
                        w.Write(val.Imaginary);
                    }
                }
                break;
            }

            case "__linnet_bSrc":
            {
                // Shape [S, K+1, mnaSize], row-major
                int S = p.SweepCount, K1 = p.HarmonicCount, M = p.MnaSize;
                for (int si = 0; si < S; si++)
                for (int k  = 0; k  < K1; k++)
                for (int m  = 0; m  < M; m++)
                {
                    var c = p.GetBSrc(si, k, m);
                    w.Write(c.Real);
                    w.Write(c.Imaginary);
                }
                break;
            }

            case "__linnet_iNl":
            {
                // Shape [S, K+1, N_interface], row-major
                int S = p.SweepCount, K1 = p.HarmonicCount, N = p.InterfaceCount;
                for (int si = 0; si < S; si++)
                for (int k  = 0; k  < K1; k++)
                for (int n  = 0; n  < N; n++)
                {
                    var c = p.GetINl(si, n, k);
                    w.Write(c.Real);
                    w.Write(c.Imaginary);
                }
                break;
            }

            case "__linnet_interface_nodes":
            {
                foreach (var v in p.InterfaceNodes) w.Write(v);
                break;
            }

            case "__linnet_mna_size":
                w.Write((long)p.MnaSize);
                break;

            case "__linnet_non_ground_count":
                w.Write((long)p.NonGroundCount);
                break;
        }
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

    // ── Metadata JSON ─────────────────────────────────────────────────────────

    private static string BuildMetaJson(
        DataSet               ds,
        ExportOptions         opts,
        ILinearNetworkPayload? payload,
        List<CubeMapping>     cubeMappings)
    {
        // Build:
        //   { "format_version":2,
        //     "groups":["HB1","SP1","measurements"],
        //     "<fieldName>": { "group":"HB1", "cube":"V", "kind":"Complex", "axes":[...] },
        //     ...
        //     "__linnet_node_names":[...], "__linnet_branch_names":[...] }
        //
        // The field name (key) is the opaque uniquified numpy dtype name.
        // The authoritative group and cube names live inside the value object.
        var sb = new StringBuilder();
        sb.Append("{\"format_version\":");
        sb.Append(FormatVersion);

        // Top-level groups array (ordered).
        sb.Append(",\"groups\":[");
        var groups = ds.Groups;
        for (int gi = 0; gi < groups.Count; gi++)
        {
            if (gi > 0) sb.Append(',');
            sb.Append('"');
            AppendJsonString(sb, groups[gi]);
            sb.Append('"');
        }
        sb.Append(']');

        // Per-cube entries keyed by unique field name.
        foreach (var m in cubeMappings)
        {
            var cube = ds.CubesIn(m.Group)[m.CubeName];

            sb.Append(',');
            sb.Append('"');
            AppendJsonString(sb, m.FieldName);
            sb.Append("\":{\"group\":\"");
            AppendJsonString(sb, m.Group);
            sb.Append("\",\"cube\":\"");
            AppendJsonString(sb, m.CubeName);
            sb.Append("\",\"kind\":\"");
            sb.Append(cube.DataKind == DataKind.Complex ? "Complex" : "Real");
            sb.Append("\",\"axes\":[");

            bool firstAxis = true;
            foreach (var ax in cube.Axes)
            {
                if (!firstAxis) sb.Append(',');
                firstAxis = false;

                sb.Append("{\"name\":\"");
                AppendJsonString(sb, ax.Name);
                sb.Append("\",\"unit\":\"");
                AppendJsonString(sb, ax.Unit);
                sb.Append("\",\"values\":[");
                for (int i = 0; i < ax.Values.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    // G17 round-trip precision
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
            sb.Append("]}");
        }

        // Linnet name maps: include when IncludeLinearNetwork is on.
        if (opts.IncludeLinearNetwork && payload != null)
        {
            sb.Append(",\"__linnet_node_names\":[");
            for (int i = 0; i < payload.NodeNames.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"');
                AppendJsonString(sb, payload.NodeNames[i]);
                sb.Append('"');
            }
            sb.Append("],\"__linnet_branch_names\":[");
            for (int i = 0; i < payload.BranchNames.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"');
                AppendJsonString(sb, payload.BranchNames[i]);
                sb.Append('"');
            }
            sb.Append(']');
        }

        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>
    /// Append <paramref name="s"/> to <paramref name="sb"/> with JSON string escaping
    /// (escapes <c>"</c> and <c>\</c> and control characters).
    /// </summary>
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
                    if (c < 0x20)
                        sb.Append($"\\u{(int)c:x4}");
                    else
                        sb.Append(c);
                    break;
            }
        }
    }
}
