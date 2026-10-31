// Every importer a data set can name (brief-modelcomposer-3 R-mc3-3), each landing in NormalisedData:
//
//   touchstone  one file per bias point; the bias read from the file name or its comment lines by the data
//               set's BiasPattern. Point id: the source path as the .cmc states it.
//   csv         CSV or TSV with a column map. A cell may carry its own unit ("12 mA"), read by the value+unit
//               conventions every circuitRF field uses; otherwise the column's unit applies.
//               Point id: "<source>:<line>".
//   npy / mat / npz / ds   named arrays — circuitRF's own .npy through NpyReader (DataSetImporter), a .mat through
//               MatReader, a NumPy .npz through NpzReader, a binary simulation dataset (.ds) through BinaryDatasetReader
//               (arrays named Group.Variable). How the arrays may be laid out, and how the layout is
//               worked out from their shapes, is DataImport.Arrays.cs. A Y table converts to S at the import's Z0,
//               which is exact and invertible. Capacitances (MC-20) are the matrix C[point, 2, 2] or the circuit set
//               cgs, cgd, cds (and cm), which lands as the matrix. Point id: "<source>#<k>".
//
// An importer REFUSES with the source it is about and why — never a guessed bias, never a skipped row.
// Each returns one summary line: points, bias range, frequency range.

using System.Globalization;
using System.Numerics;
using CircuitRF.Design.Optimization;
using CircuitRF.Engine;
using RfCore;
using RfCore.Export;

namespace CircuitRF.Design.Modeling;

/// <summary>An import that cannot proceed. The message begins with the source it is about.</summary>
public sealed class DataImportException(string source, string sentence) : Exception($"{source}: {sentence}")
{
    public string SourceName { get; } = source;
}

/// <summary>What an import produced, and its one-line report.</summary>
public sealed record ImportResult(NormalisedData Data, string Summary)
{
    /// <summary>How the import read a source where that was not the documented layout (an array reordered, a frequency
    /// array reduced), one sentence each; empty when everything was as documented.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

public static partial class DataImport
{
    /// <summary>
    /// Imports <paramref name="set"/>, its sources resolved against <paramref name="cmcDirectory"/>. Progress
    /// is per file; cancellation is checked between files and throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public static ImportResult Import(CmcDataSet set, string cmcDirectory, RunControl? rc = null)
    {
        rc?.BeginStage($"Importing {set.Name}", set.Sources.Count, "file(s)");
        var notes = new List<string>();
        var data = set.Import.Format switch
        {
            ImportSettings.Touchstone => TouchstoneSet(set, cmcDirectory, rc),
            ImportSettings.Csv        => Csv(set, cmcDirectory, rc),
            _ when ImportSettings.IsArrayFormat(set.Import.Format) => Arrays(set, cmcDirectory, rc, notes),
            _ => throw new DataImportException(set.Name, $"'{set.Import.Format}' is not an import format."),
        };
        return new ImportResult(data, Summary(data)) { Notes = [.. notes.Distinct()] };
    }

    public static string ResolveSource(string cmcDirectory, string source) =>
        Path.GetFullPath(Path.Combine(cmcDirectory, source.Replace('/', Path.DirectorySeparatorChar)));

    // ── Touchstone sets ───────────────────────────────────────────────────────

    private static NormalisedData TouchstoneSet(CmcDataSet set, string dir, RunControl? rc)
    {
        var im = set.Import;
        var pattern = im.Pattern is null ? null : BiasPattern.Parse(im.Pattern);
        bool byName = im.PatternSource == "name";
        bool passive = set.Kind == DataSetKind.PassiveSParams;

        var points = new List<BiasPoint>();
        var s = new List<Complex>();
        double[]? freqs = null;
        double z0 = double.NaN;
        int ports = 0;

        foreach (string src in set.Sources)
        {
            rc?.ThrowIfCancellationRequested();
            string path = ResolveSource(dir, src);
            if (!File.Exists(path)) throw new DataImportException(src, "the file does not exist.");
            SNP snp;
            try { snp = TouchstoneIO.ReadFile(path, readComments: true); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { throw new DataImportException(src, $"it is not a readable Touchstone file ({ex.Message})."); }

            if (snp.IsEmpty) throw new DataImportException(src, "it holds no frequencies.");
            if (snp.Type != MatrixType.S) throw new DataImportException(src, $"it holds {snp.Type}-parameters; a data set reads S.");
            if (!passive && snp.Ports != 2) throw new DataImportException(src, $"it is a {snp.Ports}-port; a FET's data is a 2-port.");
            if (ports == 0) ports = snp.Ports;
            else if (snp.Ports != ports) throw new DataImportException(src, $"it is a {snp.Ports}-port and the set's first file a {ports}-port.");
            if (snp.Z0.Imaginary != 0 || snp.Z0PerPort is not null)
                throw new DataImportException(src, "its reference impedance is not one real value.");
            if (double.IsNaN(z0)) z0 = snp.Z0.Real;
            else if (snp.Z0.Real != z0) throw new DataImportException(src, $"its Z0 is {G(snp.Z0.Real)} Ω and the set's first file {G(z0)} Ω.");
            if (freqs is null) freqs = snp.Frequencies;
            else if (!SameAxis(freqs, snp.Frequencies))
                throw new DataImportException(src, "its frequencies differ from the set's first file; one data set shares one frequency axis.");

            IReadOnlyDictionary<string, double> f = new Dictionary<string, double>();
            if (pattern is not null)
            {
                string text = byName
                    ? Path.GetFileNameWithoutExtension(path)
                    : string.Join(" ", snp.Comments.Where(c => c.FrequencyIndex < 0).Select(c => c.Text));
                f = pattern.Match(text, whole: byName)
                    ?? throw new DataImportException(src, byName
                           ? $"the file name does not match the pattern '{im.Pattern}'."
                           : $"no comment line matches the pattern '{im.Pattern}'.");
            }
            points.Add(new BiasPoint(src, Get(f, "vgs"), Get(f, "vds"), Get(f, "ig"), Get(f, "id"))
            {
                AmbientC = Get(f, "T"), Geometry = Get(f, "geometry"),
            });
            foreach (var m in snp.Matrices)
                for (int i = 0; i < ports; i++)
                    for (int j = 0; j < ports; j++)
                        s.Add(m[i, j]);
            rc?.TickStage();
        }
        return new NormalisedData { Points = points, Ports = ports, FrequenciesHz = freqs, S = [.. s], Z0 = z0 };
    }

    private static double Get(IReadOnlyDictionary<string, double> f, string name) => f.TryGetValue(name, out var v) ? v : double.NaN;

    private static bool SameAxis(double[] a, double[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (Math.Abs(a[i] - b[i]) > 1e-9 * Math.Max(Math.Abs(a[i]), 1)) return false;
        return true;
    }

    // ── CSV / TSV ─────────────────────────────────────────────────────────────

    private static NormalisedData Csv(CmcDataSet set, string dir, RunControl? rc)
    {
        var points = new List<BiasPoint>();
        foreach (var (id, v) in CsvRows(set, dir, rc))
            points.Add(new BiasPoint(id, v["vgs"], v["vds"], Get(v, "ig"), Get(v, "id"))
            {
                AmbientC = Get(v, "T"), VgsIntrinsic = Get(v, "vgsi"), VdsIntrinsic = Get(v, "vdsi"),
            });
        return new NormalisedData { Points = points };
    }

    /// <summary>
    /// Every data row of a CSV/TSV set's sources, in order: its stable id (<c>source:line</c>) and each mapped quantity's
    /// value, scaled by its unit. The delimiter is inferred from the first row (tab, then <c>;</c> without <c>,</c>, then
    /// <c>,</c>) unless the import states one. A large-signal set (MC-11) reads its columns through here too.
    /// </summary>
    internal static List<(string Id, Dictionary<string, double> Values)> CsvRows(CmcDataSet set, string dir, RunControl? rc)
    {
        var im = set.Import;
        var rows = new List<(string, Dictionary<string, double>)>();
        foreach (string src in set.Sources)
        {
            rc?.ThrowIfCancellationRequested();
            string path = ResolveSource(dir, src);
            if (!File.Exists(path)) throw new DataImportException(src, "the file does not exist.");
            var lines = File.ReadAllLines(path);

            char delimiter = '\0';
            int[]? index = null;
            for (int ln = 0; ln < lines.Length; ln++)
            {
                string line = lines[ln].Trim();
                if (line.Length == 0 || line[0] is '#' or '!' or '%') continue;
                if (delimiter == '\0')
                    delimiter = im.Delimiter is { Length: > 0 } d ? (d == "\\t" ? '\t' : d[0])
                              : line.Contains('\t') ? '\t' : line.Contains(';') && !line.Contains(',') ? ';' : ',';
                var cells = line.Split(delimiter).Select(c => c.Trim().Trim('"')).ToArray();

                if (index is null)
                {
                    bool header = im.Header ?? true;
                    index = [.. im.Columns!.Select(c => ColumnIndex(c, header ? cells : null, src))];
                    if (header) continue;
                }

                var v = new Dictionary<string, double>(StringComparer.Ordinal);
                for (int k = 0; k < index.Length; k++)
                {
                    var col = im.Columns![k];
                    string where = $"{src}:{ln + 1}";
                    if (index[k] >= cells.Length) throw new DataImportException(where, $"the row has no column '{col.Column}'.");
                    v[col.Quantity] = Cell(cells[index[k]], col, where);
                }
                rows.Add(($"{src}:{ln + 1}", v));
            }
            rc?.TickStage();
        }
        return rows;
    }

    private static int ColumnIndex(ColumnMap c, string[]? header, string src)
    {
        if (header is null)
        {
            if (int.TryParse(c.Column, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 1) return n - 1;
            throw new DataImportException(src, $"without a header, column '{c.Column}' must be a column number from 1.");
        }
        int i = Array.FindIndex(header, h => string.Equals(h, c.Column, StringComparison.OrdinalIgnoreCase));
        return i >= 0 ? i : throw new DataImportException(src, $"the header has no column '{c.Column}'.");
    }

    private static double Cell(string text, ColumnMap col, string where)
    {
        if (!TunableValue.TryParse(text, out double number, out string unit, out double si))
            throw new DataImportException(where, $"'{text}' in column '{col.Column}' is not a number.");
        if (unit.Length > 0) return si;
        return number * (col.Unit is { } u ? CmcFile.UnitScale(u) ?? 1 : 1);
    }

    /// <summary>The spelling of a set's capacitance table: <c>table</c> for the matrix <c>C</c>, <c>circuit set</c> for
    /// <c>cgs</c>, <c>cgd</c>, <c>cds</c>; null when it has none.</summary>
    public static string? CapacitanceTable(CmcDataSet set) =>
        set.Import.Arrays is not { } map ? null : map.ContainsKey("C") ? "table" : map.ContainsKey("cgs") ? "circuit set" : null;

    /// <summary>
    /// Where a set's charges read their capacitances (R-mc20-5): <c>Y</c> when it holds S or Y (its table, if it has one, is
    /// cross-checked against <c>Im Y/ω</c>), else its <see cref="CapacitanceTable"/>; null for a set with neither.
    /// </summary>
    public static string? CapacitanceSource(CmcDataSet set)
    {
        if (set.Kind is DataSetKind.PassiveSParams or DataSetKind.LargeSignal) return null;
        bool admittance = set.Import.Format == ImportSettings.Touchstone
                       || set.Import.Arrays is { } map && (map.ContainsKey("S") || map.ContainsKey("Y") || map.ContainsKey("S11") || map.ContainsKey("Y11"));
        return admittance ? "Y" : CapacitanceTable(set);
    }

    /// <summary>S from normalised y = Z0·Y of a 2-port: S = (I − y)(I + y)⁻¹.</summary>
    private static (Complex, Complex, Complex, Complex) YToS(Complex y11, Complex y12, Complex y21, Complex y22)
    {
        Complex p11 = 1 + y11, p12 = y12, p21 = y21, p22 = 1 + y22;
        Complex det = p11 * p22 - p12 * p21;
        Complex i11 = p22 / det, i12 = -p12 / det, i21 = -p21 / det, i22 = p11 / det;
        Complex m11 = 1 - y11, m12 = -y12, m21 = -y21, m22 = 1 - y22;
        return (m11 * i11 + m12 * i21, m11 * i12 + m12 * i22, m21 * i11 + m22 * i21, m21 * i12 + m22 * i22);
    }

    // ── The one-line report ──────────────────────────────────────────────────

    public static string Summary(NormalisedData d)
    {
        var parts = new List<string> { $"{d.Count} point(s)" };
        var vgs = d.Points.Select(p => p.Vgs).Where(double.IsFinite).ToArray();
        var vds = d.Points.Select(p => p.Vds).Where(double.IsFinite).ToArray();
        if (vgs.Length > 0) parts.Add($"Vgs {G(vgs.Min())} to {G(vgs.Max())} V");
        if (vds.Length > 0) parts.Add($"Vds {G(vds.Min())} to {G(vds.Max())} V");
        if (d.FrequenciesHz is { Length: > 0 } f)
            parts.Add($"{G(f[0] / 1e9)} to {G(f[^1] / 1e9)} GHz in {f.Length} frequencies");
        return string.Join(", ", parts) + ".";
    }

    private static string G(double v) => v.ToString("G4", CultureInfo.InvariantCulture);
}
