// Array imports — a .mat, a .npz, a binary simulation dataset (.ds), or circuitRF's own .npy — and the work of reading
// them as their writer meant.
//
// The map names which array is which quantity. What it does not have to say is how the arrays are laid out, because
// a writer can lay out the same data several ways and only one reading fits the shapes:
//
//   Bias       one value per point (vgs, vds and every per-point array the same length, or the same meshgrid shape);
//              or the axes of a grid vgs × vds, or vgs × vds × T, whose per-point arrays have one axis per bias axis
//              in ANY order, or are flattened with vgs slowest. Tried in that order; the first that fits every
//              mapped array is the one read. A T of one value applies to every point.
//   Tables     S or Y as [points…, freq, i, j] in any order of those axes, i before j — so a MATLAB Y(i, j, f, k)
//              reads as written. Or as four arrays S11…S22 / Y11…Y22, each [points…, freq]. C as [points…, i, j].
//   Singleton  axes of length 1 are dropped first, so [n, 1] and [1, n] are both n values.
//   Units      a quantity not in SI carries its unit in ArrayUnits (freq: GHz, id: mA; T: K is converted to °C).
//   Sign       an array name written with a leading '-' (-VD.i=id) is read negated: a voltage source's current is
//              recorded flowing into the source, which is out of the device.
//
// When the reading is not the one the documentation spells (a reordered or flattened array, a reduced frequency
// array), the import says so in a note: the user sees what was assumed and can tell a wrong guess from the plots.
// When several orders fit one shape equally well (vgs and vds of the same length), the one closest to the documented
// order is read and the note says another fits too. When nothing fits, the refusal lists every mapped array's shape and
// the layouts it tried — never a guessed bias, never a skipped row.

using System.Globalization;
using System.Numerics;
using CircuitRF.Engine;
using RfCore.Export;

namespace CircuitRF.Design.Modeling;

/// <summary>One array a source holds, as a listing shows it.</summary>
public sealed record ArrayEntry(string Name, int[] Shape, bool IsComplex)
{
    public string ShapeText => string.Join(" × ", Shape);
}

/// <summary>A suggested map for an array source: quantity → array name, the units its names state, and what could not be
/// decided (each a sentence).</summary>
public sealed record ArrayMapSuggestion(
    IReadOnlyDictionary<string, string> Arrays, IReadOnlyDictionary<string, string> Units, IReadOnlyList<string> Doubts)
{
    /// <summary>The map as the importer's map field spells it, <c>name=quantity[:unit]</c>, comma-separated.</summary>
    public string Text => string.Join(", ", Arrays.OrderBy(p => Order(p.Key)).Select(p =>
        Units.TryGetValue(p.Key, out var u) ? $"{p.Value}={p.Key}:{u}" : $"{p.Value}={p.Key}"));

    private static int Order(string q)
    {
        int i = CmcFile.PointQuantities.ToList().IndexOf(q);
        return i >= 0 ? i : 100 + CmcFile.TableQuantities.ToList().IndexOf(q);
    }
}

public static partial class DataImport
{
    private sealed record NamedArray(int[] Shape, double[] Re, double[]? Im)
    {
        public int Count => Re.Length;
        public Complex At(int i) => new(Re[i], Im is null ? 0 : Im[i]);
        public string ShapeText => string.Join(" × ", Shape);

        public NamedArray Squeezed()
        {
            int[] s = [.. Shape.Where(d => d != 1)];
            return this with { Shape = s.Length == 0 ? [1] : s };
        }

        public NamedArray Scaled(double k, double offset = 0) => k == 1 && offset == 0 ? this
            : new(Shape, [.. Re.Select(x => x * k + offset)], Im?.Select(x => x * k).ToArray());

        /// <summary>The array with its axes reordered: output axis k is input axis <paramref name="order"/>[k].</summary>
        public NamedArray Permuted(int[] order)
        {
            if (order.Select((o, k) => o == k).All(x => x)) return this;
            int rank = Shape.Length;
            int[] outShape = [.. order.Select(o => Shape[o])];
            var inStride = new int[rank];
            for (int d = rank - 1, s = 1; d >= 0; d--) { inStride[d] = s; s *= Shape[d]; }
            var re = new double[Count];
            var im = Im is null ? null : new double[Count];
            var idx = new int[rank];
            for (int c = 0; c < Count; c++)
            {
                int rem = c, src = 0;
                for (int d = rank - 1; d >= 0; d--) { idx[d] = rem % outShape[d]; rem /= outShape[d]; }
                for (int d = 0; d < rank; d++) src += idx[d] * inStride[order[d]];
                re[c] = Re[src];
                if (im is not null) im[c] = Im![src];
            }
            return new NamedArray(outShape, re, im);
        }
    }

    /// <summary>A role an array's axis can play, and its length.</summary>
    private readonly record struct Role(string Name, int Size);

    /// <summary>How the points are laid out: the bias axes in their canonical order (length-1 axes dropped), the point count,
    /// and how each bias quantity is read at a point.</summary>
    private sealed record BiasLayout(string Label, Role[] Axes, int N);

    private static NormalisedData Arrays(CmcDataSet set, string dir, RunControl? rc, List<string> notes)
    {
        var im = set.Import;
        var map = im.Arrays!;
        var points = new List<BiasPoint>();
        var s = new List<Complex>();
        var cTable = new List<double>();
        double[]? freqs = null;
        double z0 = im.Z0 ?? 50;

        foreach (string src in set.Sources)
        {
            rc?.ThrowIfCancellationRequested();
            string path = ResolveSource(dir, src);
            if (!File.Exists(path)) throw new DataImportException(src, "the file does not exist.");
            var arrays = Load(path, src, im.Format, [.. map.Values.Select(n => Signed(n).Name).Distinct()]);
            NamedArray? A(string q)
            {
                if (!map.TryGetValue(q, out var mapped)) return null;
                var (name, negated) = Signed(mapped);
                if (!arrays.TryGetValue(name, out var a))
                    throw new DataImportException(src, $"it holds no array '{name}' (mapped to {q}){Holds(arrays)}.");
                a = a.Squeezed();
                if (negated) a = a.Scaled(-1);
                if (im.ArrayUnits?.TryGetValue(q, out var unit) == true)
                    a = q == "T" ? (IsKelvin(unit) ? a.Scaled(1, -273.15) : a) : a.Scaled(CmcFile.UnitScale(unit) ?? 1);
                return a;
            }

            var vgs = A("vgs")!; var vds = A("vds")!; var tArr = A("T");
            string tableName = map.ContainsKey("S") || map.ContainsKey("S11") ? "S" : "Y";
            bool isY = tableName == "Y";
            bool elements = map.ContainsKey(tableName + "11");
            var table = elements ? null : A(tableName);
            var elementArrays = elements ? new[] { "11", "12", "21", "22" }.Select(e => A(tableName + e)!).ToArray() : null;
            bool hasTable = table is not null || elementArrays is not null;

            // The frequency axis, reduced to its distinct values when it was written once per point.
            double[]? f = null;
            if (A("freq") is { } fa)
            {
                f = fa.Re;
                if (fa.Shape.Length > 1 || fa.Re.Distinct().Count() < fa.Count)
                {
                    double[] distinct = [.. fa.Re.Distinct()];
                    if (fa.Count % distinct.Length != 0)
                        throw new DataImportException(src, $"'{map["freq"]}' ({fa.ShapeText}) is not one frequency axis repeated.");
                    f = distinct;
                    notes.Add($"'{map["freq"]}' ({fa.ShapeText}) read as {(distinct.Length == 1 ? "one frequency" : $"its {distinct.Length} distinct frequencies")}, repeated.");
                }
            }
            if (hasTable && f is null)
                f = im.FrequencyHz is { } fh ? [fh] : throw new DataImportException(src,
                    $"the {tableName} table has no frequency: map a freq array, or state FrequencyHz.");
            int nF = f?.Length ?? 1;

            // Every array that is laid out over the points, with the roles beyond the bias it carries.
            var perPoint = new List<(string Q, NamedArray A, Role[] Extra)>();
            foreach (string q in new[] { "id", "ig", "vgsi", "vdsi", "cgs", "cgd", "cds", "cm" })
                if (A(q) is { } a) perPoint.Add((q, a, []));
            Role[] fRole = nF > 1 ? [new Role("freq", nF)] : [];
            if (table is not null) perPoint.Add((tableName, table, [.. fRole, new Role("i", 2), new Role("j", 2)]));
            if (elementArrays is not null)
                for (int e = 0; e < 4; e++) perPoint.Add((tableName + (e switch { 0 => "11", 1 => "12", 2 => "21", _ => "22" }), elementArrays[e], fRole));
            if (A("C") is { } cArr) perPoint.Add(("C", cArr, [new Role("i", 2), new Role("j", 2)]));

            // Try each bias layout until every array fits it.
            var layouts = new List<(BiasLayout L, bool Grid, bool TAxis)>();
            bool tPerPointCandidate = tArr is not null && tArr.Count > 1;
            if (vgs.Count == vds.Count)
                layouts.Add((new BiasLayout("one value per point", Drop(Axes(vgs.Shape, "point")), vgs.Count), false, false));
            if (vgs.Shape.Length == 1 && vds.Shape.Length == 1)
            {
                layouts.Add((new BiasLayout("the grid vgs × vds", Drop([new("vgs", vgs.Count), new("vds", vds.Count)]), vgs.Count * vds.Count), true, false));
                if (tPerPointCandidate && tArr!.Shape.Length == 1)
                    layouts.Add((new BiasLayout("the grid vgs × vds × T", Drop([new("vgs", vgs.Count), new("vds", vds.Count), new("T", tArr.Count)]),
                                                vgs.Count * vds.Count * tArr.Count), true, true));
            }

            (BiasLayout L, bool Grid, bool TAxis, Dictionary<string, NamedArray> Laid, List<string> Notes)? chosen = null;
            foreach (var (l, grid, tAxis) in layouts)
            {
                var laid = new Dictionary<string, NamedArray>(StringComparer.Ordinal);
                var layoutNotes = new List<string>();
                var all = new List<(string Q, NamedArray A, Role[] Extra)>(perPoint);
                if (!grid) { all.Add(("vgs", vgs, [])); all.Add(("vds", vds, [])); }
                if (tArr is not null && !tAxis && tArr.Count > 1) all.Add(("T", tArr, []));
                bool ok = true;
                foreach (var (q, a, extra) in all)
                {
                    if (Lay(a, l, extra, map.TryGetValue(q, out var n) ? n : q) is not { } r) { ok = false; break; }
                    laid[q] = r.Array;
                    if (r.Note is { } note) layoutNotes.Add(note);
                }
                if (ok) { chosen = (l, grid, tAxis, laid, layoutNotes); break; }
            }
            if (chosen is not { } c)
                throw new DataImportException(src, ShapeRefusal(map, vgs, vds, tArr, perPoint, nF));
            notes.AddRange(c.Notes);

            int nPts = c.L.N;
            int nG = vgs.Count, nD = vds.Count, nT = c.TAxis ? tArr!.Count : 1;
            double Vgs(int k) => c.Grid ? vgs.Re[k / (nD * nT)] : c.Laid["vgs"].Re[k];
            double Vds(int k) => c.Grid ? vds.Re[k / nT % nD] : c.Laid["vds"].Re[k];
            double T(int k) => tArr is null ? double.NaN : c.TAxis ? tArr.Re[k % nT] : tArr.Count == 1 ? tArr.Re[0] : c.Laid["T"].Re[k];
            double[] Per(string q) => c.Laid.TryGetValue(q, out var a) ? a.Re : [.. Enumerable.Repeat(double.NaN, nPts)];
            double[] ig = Per("ig"), id = Per("id"), vgsi = Per("vgsi"), vdsi = Per("vdsi");

            if (hasTable)
            {
                if (freqs is null) freqs = f;
                else if (!SameAxis(freqs, f!)) throw new DataImportException(src, "its frequencies differ from the set's first source.");
                for (int k = 0; k < nPts * nF; k++)
                {
                    Complex a, b, cc, d;
                    if (table is not null)
                    {
                        var t = c.Laid[tableName];
                        (a, b, cc, d) = (t.At(4 * k), t.At(4 * k + 1), t.At(4 * k + 2), t.At(4 * k + 3));
                    }
                    else
                        (a, b, cc, d) = (c.Laid[tableName + "11"].At(k), c.Laid[tableName + "12"].At(k),
                                         c.Laid[tableName + "21"].At(k), c.Laid[tableName + "22"].At(k));
                    if (isY) (a, b, cc, d) = YToS(a * z0, b * z0, cc * z0, d * z0);
                    s.Add(a); s.Add(b); s.Add(cc); s.Add(d);
                }
            }
            if (c.Laid.TryGetValue("C", out var cm4)) cTable.AddRange(cm4.Re);
            else if (c.Laid.ContainsKey("cgs"))
            {
                // The circuit set as the matrix, IntrinsicQuantities' relations inverted (R-mc20-1); an absent cm is 0.
                double[] cgs = Per("cgs"), cgd = Per("cgd"), cds = Per("cds");
                double[] cmv = c.Laid.ContainsKey("cm") ? Per("cm") : new double[nPts];
                for (int k = 0; k < nPts; k++)
                {
                    cTable.Add(cgs[k] + cgd[k]); cTable.Add(-cgd[k]);
                    cTable.Add(cmv[k] - cgd[k]); cTable.Add(cds[k] + cgd[k]);
                }
            }
            for (int k = 0; k < nPts; k++)
                points.Add(new BiasPoint($"{src}#{k}", Vgs(k), Vds(k), ig[k], id[k])
                {
                    AmbientC = T(k), VgsIntrinsic = vgsi[k], VdsIntrinsic = vdsi[k],
                });
            rc?.TickStage();
        }
        return new NormalisedData
        {
            Points = points,
            FrequenciesHz = freqs,
            S = s.Count > 0 ? [.. s] : null,
            Z0 = z0,
            C = cTable.Count > 0 ? [.. cTable] : null,
        };
    }

    private static bool IsKelvin(string unit) => unit.Trim() is "K" or "kelvin" or "Kelvin";

    private static Role[] Axes(int[] shape, string name) =>
        shape.Length == 1 ? [new Role(name, shape[0])] : [.. shape.Select((d, k) => new Role($"{name} axis {k + 1}", d))];

    private static Role[] Drop(Role[] roles) => [.. roles.Where(r => r.Size != 1)];

    /// <summary>
    /// <paramref name="a"/> in the canonical C order over <paramref name="l"/>'s points then <paramref name="extra"/>, or null
    /// when its shape fits neither the bias axes one per axis (in any order), nor flattened into one point axis.
    /// </summary>
    private static (NamedArray Array, string? Note)? Lay(NamedArray a, BiasLayout l, Role[] extra, string name)
    {
        int need = l.N * extra.Aggregate(1, (p, r) => p * r.Size);
        if (a.Count != need) return null;
        Role[] separate = [.. l.Axes, .. extra];
        int iRole = Array.FindIndex(separate, r => r.Name == "i");
        int[] shape = separate.Length == 0 && a.Count == 1 ? [] : a.Shape;     // one bias point, one value: no axes at all
        if (Fit(shape, separate, iRole, out int alternatives) is { } perm)
            return (a.Permuted(perm), Note(name, a, separate, perm, alternatives));
        if (l.Axes.Length != 1)
        {
            Role[] merged = [new Role("point", l.N), .. extra];
            if (Fit(a.Shape, merged, iRole < 0 ? -1 : iRole - l.Axes.Length + 1, out alternatives) is { } p2)
                return (a.Permuted(p2), Note(name, a, merged, p2, alternatives));
        }
        // Leading axes that together make the points, in C order, with the extra roles after them.
        int prod = 1;
        for (int k = 0; k < a.Shape.Length; k++)
        {
            prod *= a.Shape[k];
            if (prod == l.N)
            {
                int[] rest = a.Shape[(k + 1)..];
                if (rest.Length != extra.Length || rest.Where((d, i) => d != extra[i].Size).Any()) break;
                return (a with { Shape = [l.N, .. rest] }, l.Axes.Length == 1 && k == 0 ? null
                    : $"'{name}' ({a.ShapeText}) read as [{string.Join(", ", l.Axes.Select(r => r.Name))}{string.Concat(extra.Select(r => ", " + r.Name))}], flattened.");
            }
            if (prod > l.N) break;
        }
        return null;
    }

    private static string? Note(string name, NamedArray a, Role[] roles, int[] perm, int alternatives)
    {
        bool identity = perm.Select((p, k) => p == k).All(x => x);
        if (identity && alternatives == 0) return null;
        string read = $"'{name}' ({a.ShapeText}) read as [{string.Join(", ", perm.Select((p, k) => (p, k)).OrderBy(x => x.p).Select(x => roles[x.k].Name))}]";
        return alternatives > 0 ? $"{read}; another order of its axes fits too — check the plots." : $"{read}.";
    }

    /// <summary>
    /// The axis of <paramref name="shape"/> that plays each role (perm[k] for role k), with role <paramref name="iRole"/> (i)
    /// before the next (j) in the source; null when none fits. When several fit — axes of equal length — the one read is the
    /// closest to a layout a writer would choose: the documented [bias…, freq, i, j], the matrix first [i, j, freq, bias…],
    /// or either with the bias axes reversed, as a column-major writer lays them out. <paramref name="alternatives"/> counts
    /// the other fits.
    /// </summary>
    private static int[]? Fit(int[] shape, Role[] roles, int iRole, out int alternatives)
    {
        alternatives = 0;
        if (shape.Length != roles.Length) return null;
        var refs = WriterOrders(roles);
        int[]? best = null; int bestScore = int.MaxValue, found = 0;
        var perm = new int[roles.Length];
        var used = new bool[shape.Length];
        void Go(int k)
        {
            if (k == roles.Length)
            {
                if (iRole >= 0 && perm[iRole] > perm[iRole + 1]) return;
                found++;
                int score = refs.Min(pos =>
                {
                    int disagree = 0;
                    for (int x = 0; x < perm.Length; x++)
                        for (int y = x + 1; y < perm.Length; y++)
                            if (perm[x] < perm[y] != pos[x] < pos[y]) disagree++;
                    return disagree;
                });
                if (score < bestScore) { bestScore = score; best = (int[])perm.Clone(); }
                return;
            }
            for (int axis = 0; axis < shape.Length; axis++)
            {
                if (used[axis] || shape[axis] != roles[k].Size) continue;
                used[axis] = true; perm[k] = axis;
                Go(k + 1);
                used[axis] = false;
            }
        }
        Go(0);
        alternatives = best is null ? 0 : found - 1;
        return best;
    }

    /// <summary>Each layout a writer would choose, as the position of every role in it.</summary>
    private static List<int[]> WriterOrders(Role[] roles)
    {
        var bias = Enumerable.Range(0, roles.Length).Where(k => roles[k].Name is not ("freq" or "i" or "j")).ToList();
        var tail = Enumerable.Range(0, roles.Length).Where(k => roles[k].Name == "freq")
                             .Concat(Enumerable.Range(0, roles.Length).Where(k => roles[k].Name == "i"))
                             .Concat(Enumerable.Range(0, roles.Length).Where(k => roles[k].Name == "j")).ToList();
        var reversed = Enumerable.Reverse(bias).ToList();
        var head = tail.Count > 0 && roles[tail[0]].Name == "freq" ? tail.Skip(1).Append(tail[0]).ToList() : tail;   // i, j, freq
        int[] Pos(IEnumerable<int> order)
        {
            var pos = new int[roles.Length];
            int n = 0;
            foreach (int k in order) pos[k] = n++;
            return pos;
        }
        return [Pos(bias.Concat(tail)), Pos(head.Concat(bias)), Pos(head.Concat(reversed)), Pos(reversed.Concat(tail))];
    }

    private static string ShapeRefusal(IReadOnlyDictionary<string, string> map, NamedArray vgs, NamedArray vds, NamedArray? t,
                                       List<(string Q, NamedArray A, Role[] Extra)> perPoint, int nF)
    {
        var shapes = new List<string> { $"{map["vgs"]} ({vgs.ShapeText})", $"{map["vds"]} ({vds.ShapeText})" };
        if (t is not null) shapes.Add($"{map["T"]} ({t.ShapeText})");
        shapes.AddRange(perPoint.Select(p => $"{map[p.Q]} ({p.A.ShapeText})"));
        string grid = t is { Count: > 1 } ? $"{vgs.Count} × {vds.Count} (× {t.Count} for T)" : $"{vgs.Count} × {vds.Count}";
        string tables = perPoint.Any(p => p.Extra.Length > 0)
            ? $", and a table those points × {(nF > 1 ? $"{nF} frequencies × " : "")}2 × 2 in any order" : "";
        return $"the arrays' shapes do not fit together: {string.Join(", ", shapes)}. Each per-point array needs one value per point " +
               $"({(vgs.Count == vds.Count ? $"{vgs.Count}, as vgs and vds have" : "vgs and vds differ in length, so not per point")}) " +
               $"or one per grid point ({grid}, its axes in any order){tables}.";
    }

    private static string Holds(Dictionary<string, NamedArray> arrays) =>
        arrays.Count == 0 ? "" : $"; it holds {string.Join(", ", arrays.Take(12).Select(p => $"{p.Key} ({p.Value.ShapeText})"))}{(arrays.Count > 12 ? ", …" : "")}";

    /// <summary>The requested arrays (and, for a message about a missing one, every other array the file holds).</summary>
    private static Dictionary<string, NamedArray> Load(string path, string src, string format, string[] names)
    {
        var result = new Dictionary<string, NamedArray>(StringComparer.Ordinal);
        try
        {
            IReadOnlyDictionary<string, MatArray>? raw = format switch
            {
                ImportSettings.Mat => MatReader.Read(path, names),
                ImportSettings.Npz => NpzReader.Read(path, names),
                ImportSettings.Ds => BinaryDatasetReader.ReadArrays(path, names),
                _ => null,
            };
            if (raw is not null)
            {
                foreach (var (name, a) in raw) result[name] = new NamedArray(a.Shape, a.Real, a.Imag);
                if (names.Any(n => !result.ContainsKey(n)))
                    foreach (var e in ListArrays(path, format).Arrays)
                        result.TryAdd(e.Name, new NamedArray(e.Shape, [], null));
                return result;
            }
            PlainNpyRefusal(path, src);
            var ds = DataSetImporter.Import(path).DataSet;
            foreach (var (name, cube) in NpyCubes(ds))
            {
                int[] shape = [.. cube.Axes.Select(x => x.Length)];
                result[name] = !names.Contains(name) ? new NamedArray(shape, [], null)
                    : cube.DataKind == RfCore.Data.DataKind.Complex
                        ? new NamedArray(shape, [.. cube.ComplexValues.Select(c => c.Real)], [.. cube.ComplexValues.Select(c => c.Imaginary)])
                        : new NamedArray(shape, cube.RealValues, null);
            }
            return result;
        }
        catch (MatFormatException ex) { throw new DataImportException(src, ex.Message); }
        catch (InvalidDataException ex) { throw new DataImportException(src, ex.Message); }
    }

    /// <summary>A mapped array name and whether it is read negated (written <c>-name</c>).</summary>
    private static (string Name, bool Negated) Signed(string mapped) =>
        mapped.Length > 1 && mapped[0] == '-' ? (mapped[1..], true) : (mapped, false);

    /// <summary>A plain one-array <c>.npy</c> (what <c>numpy.save</c> writes) is refused with what to write instead.</summary>
    private static void PlainNpyRefusal(string path, string src)
    {
        if (NpzReader.IsPlainNpy(path, out string descr, out int[] shape))
            throw new DataImportException(src,
                $"it is one unnamed array ({descr}, {string.Join(" × ", shape)}), as numpy.save writes. Save the arrays together with their " +
                "names instead: numpy.savez(\"tables.npz\", vgs=vgs, vds=vds, id=id, …), or scipy.io.savemat(\"tables.mat\", {…}).");
    }

    /// <summary>
    /// Every array an array source holds, with its shape, and what it holds that cannot be read (a cell array, text) with the
    /// reason — for the importer's preview and for suggesting a map. Refuses only a file that is not of its format at all.
    /// </summary>
    public static (IReadOnlyList<ArrayEntry> Arrays, IReadOnlyList<(string Name, string Why)> Skipped) ListArrays(string path, string format)
    {
        try
        {
            switch (format)
            {
                case ImportSettings.Mat:
                {
                    var (arrays, skipped) = MatReader.List(path);
                    return ([.. arrays.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(a => new ArrayEntry(a.Name, a.Shape, a.IsComplex))], skipped);
                }
                case ImportSettings.Npz:
                {
                    var (arrays, skipped) = NpzReader.List(path);
                    return ([.. arrays.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(a => new ArrayEntry(a.Name, a.Shape, a.IsComplex))], skipped);
                }
                case ImportSettings.Ds:
                {
                    var (arrays, skipped) = BinaryDatasetReader.List(path);
                    return ([.. arrays.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(a => new ArrayEntry(a.Name, a.Shape, a.IsComplex))], skipped);
                }
                default:
                {
                    PlainNpyRefusal(path, Path.GetFileName(path));
                    var ds = DataSetImporter.Import(path).DataSet;
                    return ([.. NpyCubes(ds).Select(c => new ArrayEntry(c.Name, [.. c.Cube.Axes.Select(x => x.Length)], c.Cube.DataKind == RfCore.Data.DataKind.Complex))], []);
                }
            }
        }
        catch (MatFormatException ex) { throw new DataImportException(Path.GetFileName(path), ex.Message); }
        catch (InvalidDataException ex) { throw new DataImportException(Path.GetFileName(path), ex.Message); }
    }

    /// <summary>Every cube of a circuitRF <c>.npy</c> by the name a map gives it: bare in the default group, else <c>Group.Cube</c>.</summary>
    private static IEnumerable<(string Name, RfCore.Data.DataCube Cube)> NpyCubes(RfCore.Data.DataSet ds)
    {
        foreach (string g in ds.Groups)
            foreach (var (name, cube) in ds.CubesIn(g))
                yield return (g == RfCore.Data.DataSet.DefaultGroup ? name : $"{g}.{name}", cube);
    }

    // ── Suggesting a map from the names ──────────────────────────────────────

    /// <summary>The spellings each quantity is recognised by, compared lower-case with <c>_ - . space</c> removed.</summary>
    private static readonly (string Quantity, string[] Names)[] Aliases =
    [
        ("vgs", ["vgs", "vg", "vgate", "vgg", "vgsext"]),
        ("vds", ["vds", "vd", "vdrain", "vdd", "vdsext"]),
        ("id", ["id", "ids", "idrain", "idd", "idc"]),
        ("ig", ["ig", "igs", "igate", "igg"]),
        ("T", ["t", "temp", "temperature", "tamb", "tambient", "ambient", "tchuck"]),
        ("vgsi", ["vgsi", "vgsint", "vgsintrinsic", "vgi"]),
        ("vdsi", ["vdsi", "vdsint", "vdsintrinsic", "vdi"]),
        ("freq", ["freq", "f", "frequency", "freqs", "frequencies"]),
        ("Y", ["y", "yparams", "yparam", "ymatrix", "ymat"]),
        ("S", ["s", "sparams", "sparam", "smatrix", "smat"]),
        ("C", ["c", "cmatrix", "cmat"]),
        ("cgs", ["cgs"]), ("cgd", ["cgd"]), ("cds", ["cds"]), ("cm", ["cm"]),
        .. CmcFile.ElementQuantities.Select(e => (e, new[] { e.ToLowerInvariant() })),
    ];

    /// <summary>A name's unit suffix (<c>Id_mA</c>, <c>freq_GHz</c>) → the unit it states; SI suffixes state none.</summary>
    private static readonly (string Suffix, string? Unit)[] UnitSuffixes =
    [
        ("ghz", "GHz"), ("mhz", "MHz"), ("khz", "kHz"), ("hz", null),
        ("ma", "mA"), ("ua", "uA"), ("na", "nA"), ("a", null),
        ("mv", "mV"), ("v", null),
        ("pf", "pF"), ("ff", "fF"), ("nf", "nF"), ("f", null),
        ("ms", "mS"), ("degc", null), ("c", null), ("k", "K"),
    ];

    /// <summary>
    /// A map for <paramref name="arrays"/> from their names alone: each recognised spelling of a quantity, its unit from a
    /// suffix such as <c>_mA</c>. Two arrays that both look like one quantity are left for the user, and said so. A whole
    /// S or Y table is preferred to its elements, and Y to S when both are there.
    /// </summary>
    public static ArrayMapSuggestion SuggestArrayMap(IEnumerable<ArrayEntry> arrays)
    {
        var candidates = new Dictionary<string, List<(string Name, string? Unit, bool Exact)>>(StringComparer.Ordinal);
        foreach (var a in arrays)
        {
            string leaf = a.Name.Split('.', '/')[^1];
            string norm = Normalise(leaf);
            if (Match(norm) is { } q) { Add(q, a.Name, null, norm == q.ToLowerInvariant()); continue; }
            // A unit suffix after a separator: Id_mA, freq_GHz, T_K.
            int sep = leaf.LastIndexOfAny(['_', '-', ' ', '(', '[']);
            if (sep > 0)
            {
                string stem = Normalise(leaf[..sep]), suffix = Normalise(leaf[(sep + 1)..]).TrimEnd(')', ']');
                if (Match(stem) is { } q2 && UnitSuffixes.FirstOrDefault(u => u.Suffix == suffix) is { Suffix: not null } us && Plausible(q2, us.Unit, suffix))
                    Add(q2, a.Name, us.Unit, stem == q2.ToLowerInvariant());
            }
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var units = new Dictionary<string, string>(StringComparer.Ordinal);
        var doubts = new List<string>();
        foreach (var (q, list) in candidates)
        {
            var pick = list.Count == 1 ? list[0] : list.Count(x => x.Exact) == 1 ? list.First(x => x.Exact) : default;
            if (pick.Name is null)
            {
                doubts.Add($"{string.Join(" and ", list.Select(x => $"'{x.Name}'"))} all look like {q}; map the one that is.");
                continue;
            }
            map[q] = pick.Name;
            if (pick.Unit is { } u) units[q] = u;
        }
        foreach (string t in new[] { "S", "Y" })
        {
            string[] el = [$"{t}11", $"{t}12", $"{t}21", $"{t}22"];
            int given = el.Count(map.ContainsKey);
            if (given > 0 && (map.ContainsKey(t) || given < 4))
                foreach (string e in el) { map.Remove(e); units.Remove(e); }
        }
        bool sAny = map.ContainsKey("S") || map.ContainsKey("S11"), yAny = map.ContainsKey("Y") || map.ContainsKey("Y11");
        if (sAny && yAny)
        {
            foreach (string k in new[] { "S", "S11", "S12", "S21", "S22" }) map.Remove(k);
            doubts.Add("the file holds both S and Y; Y is mapped.");
        }
        foreach (string k in units.Keys.Where(k => !map.ContainsKey(k)).ToList()) units.Remove(k);
        return new ArrayMapSuggestion(map, units, doubts);

        void Add(string q, string name, string? unit, bool exact)
        {
            if (!candidates.TryGetValue(q, out var l)) candidates[q] = l = [];
            l.Add((name, unit, exact));
        }
    }

    private static string Normalise(string s) => new([.. s.ToLowerInvariant().Where(ch => ch is not ('_' or '-' or '.' or ' '))]);

    private static string? Match(string norm) => Aliases.FirstOrDefault(a => a.Names.Contains(norm)).Quantity;

    /// <summary>A unit suffix that fits its quantity: a current's in amperes, a frequency's in hertz, and so on.</summary>
    private static bool Plausible(string q, string? unit, string suffix) => q switch
    {
        "id" or "ig" => suffix.EndsWith('a'),
        "vgs" or "vds" or "vgsi" or "vdsi" => suffix.EndsWith('v'),
        "freq" => suffix.EndsWith("hz", StringComparison.Ordinal),
        "T" => suffix is "c" or "degc" or "k",
        "C" or "cgs" or "cgd" or "cds" or "cm" => suffix.EndsWith('f'),
        "Y" or "Y11" or "Y12" or "Y21" or "Y22" => suffix is "ms",
        _ => false,
    };
}
