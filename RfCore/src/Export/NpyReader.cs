// ================================================================
//  NpyReader.cs  —  NumPy .npy structured-array importer (Level 1)
//
//  Exact inverse of NpyWriter.  Reads a circuitRF .npy file and
//  reconstructs the DataSet (cubes, DataKinds, shapes, axes from
//  __meta__) plus, when present, the ImportedLinearNetwork.
//
//  Level 1: rehydrate the stored DataSet — all cubes, axes, kinds,
//           values.  Load __linnet_* data and expose it, but do NOT
//           implement the reconstruction solve (Level 2 — see docs).
//
//  Format reference: data-export.md §3, data-file-format.md §Schema.
//  Round-trip equality (bitwise for buffers) is the correctness gate.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using RfCore.Data;

namespace RfCore.Export;

/// <summary>
/// Reads a circuitRF <c>.npy</c> structured-array file and reconstructs the <see cref="DataSet"/>
/// (Level 1).  When the file contains <c>__linnet_*</c> fields, also returns an
/// <see cref="ImportedLinearNetwork"/> — everything a Level-2 consumer needs to reconstruct
/// linear-interior node voltages and branch currents without re-running the HB sweep.
/// </summary>
internal static class NpyReader
{
    // ── Magic bytes ──────────────────────────────────────────────────────────

    private static readonly byte[] Magic = { 0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y' };

    // ── Public entry point ───────────────────────────────────────────────────

    /// <summary>
    /// Read a circuitRF <c>.npy</c> file and reconstruct the <see cref="DataSet"/> and (if
    /// present) the <see cref="ImportedLinearNetwork"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// Thrown when the file is not a valid circuitRF .npy (bad magic, unsupported version,
    /// missing or mismatched <c>format_version</c>, truncated data).
    /// </exception>
    public static (DataSet DataSet, ImportedLinearNetwork? LinearNetwork) Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var r  = new BinaryReader(fs, Encoding.ASCII, leaveOpen: false);

        // ── 1. Preamble: magic, version, header length ───────────────────────

        var magic = r.ReadBytes(6);
        if (!magic.SequenceEqual(Magic))
            throw new InvalidDataException(
                $"Not a NumPy .npy file: expected magic \\x93NUMPY, got {BitConverter.ToString(magic)}.");

        byte major = r.ReadByte();
        byte minor = r.ReadByte();  // always 0

        if (major is not (1 or 2))
            throw new InvalidDataException(
                $"Unsupported .npy format version {major}.{minor}. Expected 1 or 2.");

        int headerLen = major == 1
            ? r.ReadUInt16()
            : (int)r.ReadUInt32();

        // ── 2. Header (ASCII Python dict) ────────────────────────────────────

        byte[] headerBytes = r.ReadBytes(headerLen);
        string header      = Encoding.ASCII.GetString(headerBytes).TrimEnd();

        var fields = ParseHeader(header);

        // ── 3. Read all field bytes sequentially ─────────────────────────────

        var rawFields = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var f in fields)
        {
            long byteCount = f.ByteCount;
            byte[] bytes   = r.ReadBytes(checked((int)byteCount));
            if (bytes.Length != byteCount)
                throw new InvalidDataException(
                    $"Truncated data for field '{f.Name}': expected {byteCount} bytes, got {bytes.Length}.");
            rawFields[f.Name] = bytes;
        }

        // ── 4. Parse __meta__ JSON ────────────────────────────────────────────

        if (!rawFields.TryGetValue("__meta__", out var metaBytes))
            throw new InvalidDataException("Missing '__meta__' field — not a circuitRF .npy file.");

        string metaJson = Encoding.UTF8.GetString(metaBytes);
        using var metaDoc = JsonDocument.Parse(metaJson);
        var meta = metaDoc.RootElement;

        // ── 5. Check format_version ───────────────────────────────────────────

        if (!meta.TryGetProperty("format_version", out var fvProp))
            throw new InvalidDataException(
                "format_version missing from __meta__. " +
                $"Expected version {NpyWriter.FormatVersion}. " +
                "Alpha .npy files are not backward-compatible — regenerate from the current exporter.");

        int foundVersion = fvProp.GetInt32();
        if (foundVersion != NpyWriter.FormatVersion)
            throw new InvalidDataException(
                $"format_version mismatch: file has version {foundVersion}, " +
                $"expected {NpyWriter.FormatVersion}. " +
                "Alpha .npy files are not backward-compatible — regenerate from the current exporter.");

        // ── 6. Reconstruct DataSet cubes ──────────────────────────────────────

        var ds = new DataSet();

        // Pre-register groups in the order recorded by the writer so that group order
        // is preserved even before any cubes are added.
        if (meta.TryGetProperty("groups", out var groupsArr)
            && groupsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var gEl in groupsArr.EnumerateArray())
                ds.RegisterGroup(gEl.GetString() ?? DataSet.DefaultGroup);
        }

        foreach (var f in fields)
        {
            // Skip non-cube fields
            if (f.Name == "__meta__" || f.Name.StartsWith("__linnet_", StringComparison.Ordinal))
                continue;

            // The field name is the opaque uniquified key written by NpyWriter.
            if (!meta.TryGetProperty(f.Name, out var cubeMeta))
                throw new InvalidDataException(
                    $"Field '{f.Name}' in dtype has no entry in __meta__ — file is inconsistent.");

            // Read group and cube names from meta (authoritative since format_version 2).
            if (!cubeMeta.TryGetProperty("group", out var groupProp))
                throw new InvalidDataException(
                    $"Cube field '{f.Name}' is missing 'group' in __meta__.");
            if (!cubeMeta.TryGetProperty("cube", out var cubeProp))
                throw new InvalidDataException(
                    $"Cube field '{f.Name}' is missing 'cube' in __meta__.");

            string group    = groupProp.GetString() ?? DataSet.DefaultGroup;
            string cubeName = cubeProp.GetString()
                ?? throw new InvalidDataException($"'cube' is null for field '{f.Name}'.");

            string kindStr = cubeMeta.GetProperty("kind").GetString()
                ?? throw new InvalidDataException($"'kind' is null for cube field '{f.Name}'.");
            DataKind kind  = kindStr == "Complex" ? DataKind.Complex : DataKind.Real;

            var axesJson = cubeMeta.GetProperty("axes");
            var axes     = BuildAxes(axesJson, f.Name);

            if (kind == DataKind.Complex)
            {
                var data = ReadComplex(rawFields[f.Name], f.Shape);
                ds.AddToGroup(group, cubeName, new DataCube(axes, data));
            }
            else
            {
                var data = ReadReal(rawFields[f.Name], f.Shape);
                ds.AddToGroup(group, cubeName, new DataCube(axes, data));
            }
        }

        // ── 7. Load __linnet_* fields (if present) ────────────────────────────

        ImportedLinearNetwork? linnet = null;

        bool hasLinnet = fields.Any(f => f.Name.StartsWith("__linnet_", StringComparison.Ordinal));
        if (hasLinnet)
        {
            linnet = BuildLinearNetwork(rawFields, fields, meta);
        }

        return (ds, linnet);
    }

    // ── Header parsing ────────────────────────────────────────────────────────

    private record struct FieldInfo(string Name, string Dtype, int[] Shape)
    {
        public long ByteCount
        {
            get
            {
                long elemSize = ElementSize(Dtype);
                if (Shape.Length == 0) return elemSize;
                long count = 1;
                foreach (int d in Shape) count *= d;
                return count * elemSize;
            }
        }

        private static long ElementSize(string dtype)
        {
            if (dtype == "<c16")       return 16;
            if (dtype == "<f8")        return  8;
            if (dtype == "<i4")        return  4;
            if (dtype == "<i8")        return  8;
            if (dtype.StartsWith("|S", StringComparison.Ordinal))
                return long.Parse(dtype[2..]);
            throw new InvalidDataException($"Unsupported dtype in .npy field: '{dtype}'.");
        }
    }

    private static List<FieldInfo> ParseHeader(string header)
    {
        // Find 'descr' key in the Python dict header.
        int descrIdx = header.IndexOf("'descr'", StringComparison.Ordinal);
        if (descrIdx < 0) descrIdx = header.IndexOf("\"descr\"", StringComparison.Ordinal);
        if (descrIdx < 0)
            throw new InvalidDataException(".npy header missing 'descr' key.");

        int listOpen = header.IndexOf('[', descrIdx);
        if (listOpen < 0)
            throw new InvalidDataException(".npy header 'descr' value is not a list.");

        // Find the matching ']' for the descr list.
        int depth    = 1;
        int listClose = listOpen + 1;
        while (listClose < header.Length && depth > 0)
        {
            char c = header[listClose];
            if (c == '[') depth++;
            else if (c == ']') depth--;
            if (depth > 0) listClose++;
        }

        string listContent = header[(listOpen + 1)..listClose];

        // Split into individual tuple strings at depth-0 commas.
        var tupleStrings = SplitDepthZero(listContent, ',');

        var result = new List<FieldInfo>();
        foreach (var ts in tupleStrings)
        {
            string t = ts.Trim();
            if (t.Length == 0) continue;

            // Each tuple is '(' ... ')' — strip outer parens.
            if (!t.StartsWith('(') || !t.EndsWith(')'))
                throw new InvalidDataException($"Expected tuple in dtype list, got: {t}");
            string inner = t[1..^1];

            // Split tuple contents at depth-0 commas (accounting for strings and nested parens).
            var parts = SplitDepthZero(inner, ',');
            if (parts.Count < 2)
                throw new InvalidDataException($"Dtype tuple has fewer than 2 elements: '{t}'.");

            string name  = ParsePyString(parts[0].Trim());
            string dtype = ParsePyString(parts[1].Trim());
            int[]  shape = parts.Count >= 3
                ? ParseShapeTuple(parts[2].Trim())
                : Array.Empty<int>();

            result.Add(new FieldInfo(name, dtype, shape));
        }

        return result;
    }

    /// <summary>Split <paramref name="s"/> at all <paramref name="sep"/> chars that are
    /// at depth 0 — i.e., not inside <c>()</c>, <c>[]</c>, or single-quoted strings.</summary>
    private static List<string> SplitDepthZero(string s, char sep)
    {
        var parts   = new List<string>();
        var current = new StringBuilder();
        int parenDepth = 0;
        bool inStr  = false;
        bool escape = false;

        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];

            if (escape) { current.Append(c); escape = false; continue; }
            if (inStr)
            {
                if (c == '\\')      { current.Append(c); escape = true; }
                else if (c == '\'') { current.Append(c); inStr = false; }
                else                  current.Append(c);
                continue;
            }

            if (c == '\'')              { inStr = true; current.Append(c); continue; }
            if (c == '(' || c == '[')   { parenDepth++; current.Append(c); continue; }
            if (c == ')' || c == ']')   { parenDepth--; current.Append(c); continue; }

            if (c == sep && parenDepth == 0)
            {
                parts.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
            parts.Add(current.ToString());

        return parts;
    }

    private static string ParsePyString(string s)
    {
        s = s.Trim();
        if (s.Length < 2 || s[0] != '\'' || s[^1] != '\'')
            throw new InvalidDataException($"Expected single-quoted Python string, got: '{s}'.");

        var result = new StringBuilder(s.Length - 2);
        bool esc = false;
        for (int i = 1; i < s.Length - 1; i++)
        {
            if (esc) { result.Append(s[i]); esc = false; }
            else if (s[i] == '\\') esc = true;
            else result.Append(s[i]);
        }
        return result.ToString();
    }

    private static int[] ParseShapeTuple(string s)
    {
        s = s.Trim();
        if (!s.StartsWith('(') || !s.EndsWith(')'))
            throw new InvalidDataException($"Expected shape tuple '(...)' , got: '{s}'.");

        string inner = s[1..^1];
        return inner.Split(',')
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Select(p => int.Parse(p, System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
    }

    // ── Axes reconstruction ───────────────────────────────────────────────────

    private static Axis[] BuildAxes(JsonElement axesJson, string fieldNameForError)
    {
        int n = axesJson.GetArrayLength();
        var axes = new Axis[n];
        for (int i = 0; i < n; i++)
        {
            var axEl  = axesJson[i];
            string name = axEl.GetProperty("name").GetString()
                ?? throw new InvalidDataException(
                    $"Axis {i} of cube '{fieldNameForError}' has null 'name'.");
            string unit = axEl.GetProperty("unit").GetString() ?? "";

            var valuesEl = axEl.GetProperty("values");
            double[] values = new double[valuesEl.GetArrayLength()];
            for (int j = 0; j < values.Length; j++)
                values[j] = valuesEl[j].GetDouble();

            string[]? labels = null;
            if (axEl.TryGetProperty("labels", out var labelsEl) &&
                labelsEl.ValueKind == JsonValueKind.Array)
            {
                labels = new string[labelsEl.GetArrayLength()];
                for (int j = 0; j < labels.Length; j++)
                    labels[j] = labelsEl[j].GetString()
                        ?? throw new InvalidDataException(
                            $"Null label at index {j} in axis '{name}' of cube '{fieldNameForError}'.");
            }

            axes[i] = new Axis(name, values, unit, labels);
        }
        return axes;
    }

    // ── Data deserialization ──────────────────────────────────────────────────

    private static Complex[] ReadComplex(byte[] bytes, int[] shape)
    {
        int count = ElementCount(shape);
        if (bytes.Length != count * 16)
            throw new InvalidDataException(
                $"Complex field byte count mismatch: expected {count * 16}, got {bytes.Length}.");

        var result = new Complex[count];
        using var ms = new MemoryStream(bytes, writable: false);
        using var r  = new BinaryReader(ms);
        for (int i = 0; i < count; i++)
            result[i] = new Complex(r.ReadDouble(), r.ReadDouble());
        return result;
    }

    private static double[] ReadReal(byte[] bytes, int[] shape)
    {
        int count = ElementCount(shape);
        if (bytes.Length != count * 8)
            throw new InvalidDataException(
                $"Real field byte count mismatch: expected {count * 8}, got {bytes.Length}.");

        var result = new double[count];
        using var ms = new MemoryStream(bytes, writable: false);
        using var r  = new BinaryReader(ms);
        for (int i = 0; i < count; i++)
            result[i] = r.ReadDouble();
        return result;
    }

    private static int[] ReadInt32Array(byte[] bytes, int count)
    {
        if (bytes.Length != count * 4)
            throw new InvalidDataException(
                $"Int32 field byte count mismatch: expected {count * 4}, got {bytes.Length}.");

        var result = new int[count];
        using var ms = new MemoryStream(bytes, writable: false);
        using var r  = new BinaryReader(ms);
        for (int i = 0; i < count; i++)
            result[i] = r.ReadInt32();
        return result;
    }

    private static long ReadInt64Scalar(byte[] bytes)
    {
        if (bytes.Length != 8)
            throw new InvalidDataException(
                $"Int64 scalar byte count mismatch: expected 8, got {bytes.Length}.");
        return BitConverter.ToInt64(bytes, 0);
    }

    private static int ElementCount(int[] shape)
    {
        if (shape.Length == 0) return 1;
        int count = 1;
        foreach (int d in shape) count *= d;
        return count;
    }

    // ── Linear-network reconstruction ────────────────────────────────────────

    private static ImportedLinearNetwork BuildLinearNetwork(
        Dictionary<string, byte[]> raw,
        List<FieldInfo>            fields,
        JsonElement                meta)
    {
        // Helper: find the FieldInfo for a named field.
        FieldInfo F(string name)
        {
            foreach (var f in fields) if (f.Name == name) return f;
            throw new InvalidDataException($"Expected linnet field '{name}' not found in .npy dtype.");
        }

        FieldInfo omegasF    = F("__linnet_omegas");
        FieldInfo gRowsF     = F("__linnet_G_rows");
        FieldInfo gColsF     = F("__linnet_G_cols");
        FieldInfo gDataF     = F("__linnet_G_data");
        FieldInfo bSrcF      = F("__linnet_bSrc");
        FieldInfo iNlF       = F("__linnet_iNl");
        FieldInfo ifaceF     = F("__linnet_interface_nodes");
        FieldInfo mnaF       = F("__linnet_mna_size");
        FieldInfo ngF        = F("__linnet_non_ground_count");

        long mnaSize      = ReadInt64Scalar(raw["__linnet_mna_size"]);
        long nonGndCount  = ReadInt64Scalar(raw["__linnet_non_ground_count"]);

        // omegas: float64[K+1]
        double[] omegas = ReadReal(raw["__linnet_omegas"], omegasF.Shape);
        int K1          = omegasF.Shape[0];

        // G triplets
        int nnz      = gRowsF.Shape[0];
        int[] gRows  = ReadInt32Array(raw["__linnet_G_rows"], nnz);
        int[] gCols  = ReadInt32Array(raw["__linnet_G_cols"], nnz);

        // G_data: complex128[K+1, nnz] → Complex[K+1, nnz]
        var gDataFlat = ReadComplex(raw["__linnet_G_data"], gDataF.Shape);
        var gData     = new Complex[K1, nnz];
        for (int k = 0; k < K1; k++)
            for (int nz = 0; nz < nnz; nz++)
                gData[k, nz] = gDataFlat[k * nnz + nz];

        // bSrc: complex128[S, K+1, MnaSize]
        int S = bSrcF.Shape[0];
        int M = bSrcF.Shape[2];
        var bSrcFlat = ReadComplex(raw["__linnet_bSrc"], bSrcF.Shape);
        var bSrc     = new Complex[S, K1, M];
        for (int si = 0; si < S; si++)
        for (int k  = 0; k  < K1; k++)
        for (int m  = 0; m  < M; m++)
            bSrc[si, k, m] = bSrcFlat[(si * K1 + k) * M + m];

        // iNl: complex128[S, K+1, NInterface]
        int N        = iNlF.Shape[2];
        var iNlFlat  = ReadComplex(raw["__linnet_iNl"], iNlF.Shape);
        var iNl      = new Complex[S, K1, N];
        for (int si = 0; si < S; si++)
        for (int k  = 0; k  < K1; k++)
        for (int n  = 0; n  < N; n++)
            iNl[si, k, n] = iNlFlat[(si * K1 + k) * N + n];

        // interface_nodes: int32[N]
        int[] ifaceNodes = ReadInt32Array(raw["__linnet_interface_nodes"], N);

        // node_names and branch_names from __meta__
        string[] nodeNames   = ReadStringArray(meta, "__linnet_node_names");
        string[] branchNames = ReadStringArray(meta, "__linnet_branch_names");

        return new ImportedLinearNetwork
        {
            MnaSize          = mnaSize,
            NonGroundCount   = nonGndCount,
            Omegas           = omegas,
            GRows            = gRows,
            GCols            = gCols,
            GData            = gData,
            BSrc             = bSrc,
            INl              = iNl,
            InterfaceNodes   = ifaceNodes,
            NodeNames        = nodeNames,
            BranchNames      = branchNames,
        };
    }

    private static string[] ReadStringArray(JsonElement meta, string key)
    {
        if (!meta.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var result = new string[arr.GetArrayLength()];
        for (int i = 0; i < result.Length; i++)
            result[i] = arr[i].GetString()
                ?? throw new InvalidDataException($"Null string at index {i} in __meta__['{key}'].");
        return result;
    }
}
