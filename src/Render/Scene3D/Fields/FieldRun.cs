// brief-em3d-29 R-em3d29-2a / 6 — what a Palace (or openEMS) run directory holds in the way of fields, and
// one step of it opened. openEMS's dump is in VtrReader.cs; Palace's is below. The layout is the pinned Palace's, read off real runs (src/Render/RESOLVED.md §brief-em3d-29):
//
//   postpro/paraview/<problem>/<problem>.pvd                       one excitation (or eigen / static)
//   postpro/paraview/<problem>/excitation_<k>/excitation_<k>.pvd   a driven run exciting several ports
//   postpro/paraview/<problem>_boundary/…                          the same, on the boundary faces
//
// <problem> is driven, eigenmode, electrostatic or magnetostatic. A step's "timestep" is the frequency in
// GHz for a driven run, the 0-based mode for an eigenmode run and the 0-based terminal for a static one;
// Palace also writes one ERROR-INDICATOR step (timestep 99 or 999), recognised by its content — it has no
// point data, only per-cell "Indicator" and "Rank" — rather than by its number.

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CircuitRF.Render.Scene3D.Fields;

/// <summary>Which Palace problem wrote the fields.</summary>
public enum FieldProblemKind { Driven, Eigenmode, Electrostatic, Magnetostatic }

/// <summary>
/// One solution the view can show: a driven run's field at one saved frequency with one port excited, an
/// eigenmode's mode, or a static run's terminal. The volume and boundary steps of the same solution are
/// paired by their timestep.
/// </summary>
public sealed record FieldSolution(FieldProblemKind Kind, int Excitation, double Timestep, string? VolumePvtu, string? BoundaryPvtu,
                                   string Solver = "Palace")
{
    /// <summary>The saved frequency, Hz, for a driven solution.</summary>
    public double FrequencyHz => Kind == FieldProblemKind.Driven ? Timestep * 1e9 : double.NaN;
    /// <summary>The 0-based mode or terminal of an eigenmode or static solution.</summary>
    public int Index => (int)Math.Round(Timestep);
}

/// <summary>A run's fields: every solution, and (Palace) the error indicator.</summary>
public sealed class FieldRun
{
    /// <summary>Where the field files are: Palace's paraview folder, or openEMS's run directory.</summary>
    public required string Directory { get; init; }
    /// <summary>"Palace" or "openEMS".</summary>
    public string Solver { get; init; } = "Palace";
    public required FieldProblemKind Kind { get; init; }
    public required IReadOnlyList<FieldSolution> Solutions { get; init; }
    /// <summary>The error indicator's volume step, if Palace wrote one.</summary>
    public string? IndicatorPvtu { get; init; }
    /// <summary>Metres per mesh unit (the configuration's Model.L0).</summary>
    public required double ToMetres { get; init; }

    private static readonly (string Dir, FieldProblemKind Kind)[] Problems =
    [
        ("driven", FieldProblemKind.Driven), ("eigenmode", FieldProblemKind.Eigenmode),
        ("electrostatic", FieldProblemKind.Electrostatic), ("magnetostatic", FieldProblemKind.Magnetostatic),
    ];

    /// <summary>
    /// The fields in run directory <paramref name="runDir"/> (it holds <c>config.json</c> and
    /// <c>postpro/</c>), or null when Palace saved none there.
    /// </summary>
    public static FieldRun? OpenPalace(string runDir, double defaultToMetres = 1e-6)
    {
        string paraview = Path.Combine(runDir, "postpro", "paraview");
        if (!System.IO.Directory.Exists(paraview)) return null;
        foreach (var (dir, kind) in Problems)
        {
            string vol = Path.Combine(paraview, dir);
            if (!System.IO.Directory.Exists(vol)) continue;
            var volume = Collections(vol, dir);
            var boundary = Collections(Path.Combine(paraview, dir + "_boundary"), dir + "_boundary");
            var solutions = new List<FieldSolution>();
            string? indicator = null;
            foreach (var (excitation, steps) in volume.OrderBy(v => v.Key))
            {
                boundary.TryGetValue(excitation, out var bsteps);
                foreach (var (t, pvtu) in steps)
                {
                    if (IsIndicator(pvtu)) { indicator ??= pvtu; continue; }
                    string? b = bsteps?.FirstOrDefault(s => s.Timestep == t).Pvtu;
                    solutions.Add(new FieldSolution(kind, excitation, t, pvtu, b));
                }
            }
            if (solutions.Count == 0 && indicator is null) continue;
            return new FieldRun
            {
                Directory = paraview, Kind = kind, IndicatorPvtu = indicator,
                Solutions = [.. solutions.OrderBy(s => s.Timestep).ThenBy(s => s.Excitation)],
                ToMetres = LengthUnit(runDir) ?? defaultToMetres,
            };
        }
        return null;
    }

    /// <summary>The collections under one problem directory, by excitation (0 when there is only one).</summary>
    private static Dictionary<int, List<(double Timestep, string Pvtu)>> Collections(string dir, string name)
    {
        var map = new Dictionary<int, List<(double, string)>>();
        if (!System.IO.Directory.Exists(dir)) return map;
        string single = Path.Combine(dir, name + ".pvd");
        if (File.Exists(single)) map[0] = Steps(single);
        foreach (string sub in System.IO.Directory.EnumerateDirectories(dir, "excitation_*"))
        {
            string ex = Path.GetFileName(sub);
            if (!int.TryParse(ex["excitation_".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int k)) continue;
            string pvd = Path.Combine(sub, ex + ".pvd");
            if (File.Exists(pvd)) map[k] = Steps(pvd);
        }
        return map;
    }

    private static readonly Regex OpenEmsDump = new(@"^efield(\d+)_f=([0-9.eE+-]+)_abs\.vtr$", RegexOptions.CultureInvariant);

    /// <summary>
    /// brief-em3d-29 R-em3d29-6 — the fields an openEMS run dumped: one solution per excited port (the run's
    /// <c>p&lt;k&gt;</c> folders) per dump frequency, each an <c>_abs.vtr</c>/<c>_arg.vtr</c> pair. Null when
    /// it dumped none.
    /// </summary>
    public static FieldRun? OpenOpenEms(string runDir)
    {
        if (!System.IO.Directory.Exists(runDir)) return null;
        var solutions = new List<FieldSolution>();
        foreach (string portDir in System.IO.Directory.EnumerateDirectories(runDir, "p*"))
        {
            if (!int.TryParse(Path.GetFileName(portDir)[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int port)) continue;
            foreach (string file in System.IO.Directory.EnumerateFiles(portDir, "efield*_abs.vtr"))
            {
                var m = OpenEmsDump.Match(Path.GetFileName(file));
                if (!m.Success || !double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double hz)) continue;
                if (!File.Exists(ArgFile(file))) continue;
                solutions.Add(new FieldSolution(FieldProblemKind.Driven, port, hz / 1e9, file, null, "openEMS"));
            }
        }
        if (solutions.Count == 0) return null;
        return new FieldRun
        {
            Directory = runDir, Solver = "openEMS", Kind = FieldProblemKind.Driven, ToMetres = 1,
            Solutions = [.. solutions.OrderBy(s => s.Timestep).ThenBy(s => s.Excitation)],
        };
    }

    /// <summary>The phase file beside an openEMS <c>_abs.vtr</c>.</summary>
    public static string ArgFile(string absFile) => absFile[..^"_abs.vtr".Length] + "_arg.vtr";

    private static readonly Regex DataSet = new(@"<DataSet\b[^>]*?\btimestep=""([^""]+)""[^>]*?\bfile=""([^""]+)""", RegexOptions.CultureInvariant);
    private static readonly Regex Piece = new(@"<Piece\b[^>]*?\bSource=""([^""]+)""", RegexOptions.CultureInvariant);

    /// <summary>A .pvd's steps: each timestep and the .pvtu it names.</summary>
    public static List<(double Timestep, string Pvtu)> Steps(string pvd)
    {
        string dir = Path.GetDirectoryName(pvd)!;
        return [.. DataSet.Matches(File.ReadAllText(pvd)).Select(m =>
            (double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), Path.Combine(dir, m.Groups[2].Value)))];
    }

    /// <summary>A .pvtu's pieces, in the order it lists them (one per MPI rank).</summary>
    public static List<string> Pieces(string pvtu)
    {
        string dir = Path.GetDirectoryName(pvtu)!;
        return [.. Piece.Matches(File.ReadAllText(pvtu)).Select(m => Path.Combine(dir, m.Groups[1].Value))];
    }

    /// <summary>The error-indicator step: per-cell arrays only, no point data.</summary>
    private static bool IsIndicator(string pvtu)
    {
        string text = File.ReadAllText(pvtu);
        int a = text.IndexOf("<PPointData", StringComparison.Ordinal), b = text.IndexOf("</PPointData>", StringComparison.Ordinal);
        return a >= 0 && b > a && !text.AsSpan(a, b - a).Contains("<PDataArray", StringComparison.Ordinal);
    }

    /// <summary>The configuration's Model.L0 — the mesh's length unit in metres.</summary>
    private static double? LengthUnit(string runDir)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(runDir, "config.json")),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return doc.RootElement.TryGetProperty("Model", out var m) && m.TryGetProperty("L0", out var l) ? l.GetDouble() : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>
/// One step of a collection, opened: every piece's header read, the pieces' geometry MERGED into one
/// <see cref="FieldMesh"/>, and the arrays listed. An array is read only when <see cref="Load"/> asks.
/// </summary>
public sealed class FieldStep
{
    public required string Pvtu { get; init; }
    public required IReadOnlyList<VtuPiece> Pieces { get; init; }
    public required FieldMesh Mesh { get; init; }
    public required IReadOnlyList<FieldArrayInfo> Arrays { get; init; }

    private readonly Dictionary<string, FieldArray> _loaded = new(StringComparer.Ordinal);

    public FieldArrayInfo? Info(string name) => Arrays.FirstOrDefault(a => a.Name == name);

    /// <summary>Bytes resident: the mesh and every array loaded so far.</summary>
    public long ResidentBytes { get { lock (_loaded) return Mesh.ResidentBytes + _loaded.Values.Sum(a => a.ResidentBytes); } }

    /// <summary>
    /// Opens <paramref name="pvtu"/>: every piece, merged. A cell of another type, or of an order other
    /// than 1 or 2, is refused by name.
    /// </summary>
    public static FieldStep Open(string pvtu, double toMetres)
    {
        if (pvtu.EndsWith("_abs.vtr", StringComparison.Ordinal)) return OpenOpenEms(pvtu, toMetres);
        var pieces = FieldRun.Pieces(pvtu).Select(VtuReader.ReadPiece).ToList();
        if (pieces.Count == 0) throw new FieldReadException($"'{pvtu}' names no pieces.");
        int nodes = 0, cells = 0, npc = -1, order = 0;
        FieldCellShape shape = FieldCellShape.Tetrahedron;
        byte type = 0;
        foreach (var p in pieces)
        {
            nodes += p.NumberOfPoints;
            cells += p.NumberOfCells;
        }
        var points = new float[3 * nodes];
        var attribute = new int[cells];
        int[]? conn = null;
        int nodeAt = 0, cellAt = 0;
        foreach (var p in pieces)
        {
            if (p.NumberOfCells == 0) continue;
            var types = new int[p.NumberOfCells];
            VtuReader.ReadInts(p, Need(p, "types"), types);
            var offsets = new int[p.NumberOfCells];
            VtuReader.ReadInts(p, Need(p, "offsets"), offsets);
            int n = offsets[0];
            for (int c = 0; c < offsets.Length; c++)
            {
                int count = offsets[c] - (c == 0 ? 0 : offsets[c - 1]);
                if (count != n || types[c] != types[0])
                    throw new FieldReadException($"'{p.Path}' mixes cell types or orders; the 3D view reads Palace's single-type meshes.");
            }
            if (npc < 0)
            {
                type = (byte)types[0];
                npc = n;
                (shape, order) = (type, n) switch
                {
                    (VtuReader.LagrangeTetrahedron, 4) => (FieldCellShape.Tetrahedron, 1),
                    (VtuReader.LagrangeTetrahedron, 10) => (FieldCellShape.Tetrahedron, 2),
                    (VtuReader.LagrangeTriangle, 3) => (FieldCellShape.Triangle, 1),
                    (VtuReader.LagrangeTriangle, 6) => (FieldCellShape.Triangle, 2),
                    _ => throw new FieldReadException(
                        $"'{p.Path}' holds VTK cell type {types[0]} with {n} nodes. The 3D view draws Palace's element orders 1 " +
                        "and 2 (tetrahedra of 4 or 10 nodes, triangles of 3 or 6); a higher order is not drawn rather than drawn " +
                        "through a subset of its nodes."),
                };
                conn = new int[cells * npc];
            }
            else if (types[0] != type || n != npc)
                throw new FieldReadException($"'{pvtu}': its pieces disagree on the cell type or order.");

            VtuReader.ReadFloats(p, Need(p, "Points"), points.AsSpan(3 * nodeAt, 3 * p.NumberOfPoints));
            var c2 = conn!.AsSpan(cellAt * npc, p.NumberOfCells * npc);
            VtuReader.ReadInts(p, Need(p, "connectivity"), c2);
            for (int i = 0; i < c2.Length; i++)
            {
                if ((uint)c2[i] >= (uint)p.NumberOfPoints)
                    throw new FieldReadException($"'{p.Path}': a cell names node {c2[i]} of {p.NumberOfPoints}.");
                c2[i] += nodeAt;          // the merge: each piece's nodes follow the previous pieces'
            }
            if (p.Array("attribute") is { } attr) VtuReader.ReadInts(p, attr, attribute.AsSpan(cellAt, p.NumberOfCells));
            nodeAt += p.NumberOfPoints;
            cellAt += p.NumberOfCells;
        }

        var mesh = new FieldMesh
        {
            Shape = shape, Order = order, NodesPerCell = Math.Max(npc, 0), Points = points, Cells = conn ?? [],
            Attribute = attribute, ToMetres = toMetres,
        };
        return new FieldStep { Pvtu = pvtu, Pieces = pieces, Mesh = mesh, Arrays = List(pieces[0]) };
    }

    /// <summary>
    /// brief-em3d-29 R-em3d29-6 — an openEMS dump: the magnitude and phase files read, the complex field
    /// rebuilt from them (Re = |E|·cos∠E, Im = |E|·sin∠E per component — exactly the solver's complex value,
    /// to Float32), on the grid as tetrahedra. Named E, like Palace's, so the same quantities are offered.
    /// </summary>
    private static FieldStep OpenOpenEms(string absFile, double toMetres)
    {
        var mag = VtrReader.Read(absFile);
        var arg = VtrReader.Read(FieldRun.ArgFile(absFile));
        if (arg.Values.Length != mag.Values.Length || arg.X.Length != mag.X.Length)
            throw new FieldReadException($"'{absFile}' and its phase file describe different grids.");
        var re = new float[mag.Values.Length];
        var im = new float[mag.Values.Length];
        for (int i = 0; i < re.Length; i++)
        {
            double a = mag.Values[i], t = arg.Values[i];
            re[i] = (float)(a * Math.Cos(t));
            im[i] = (float)(a * Math.Sin(t));
        }
        var info = new FieldArrayInfo("E", mag.Components, true, false);
        var step = new FieldStep { Pvtu = absFile, Pieces = [], Mesh = VtrReader.Mesh(mag, toMetres), Arrays = [info] };
        step._loaded["E"] = new FieldArray { Info = info, Re = re, Im = im };
        return step;
    }

    private static VtuArray Need(VtuPiece p, string name)
        => p.Array(name) ?? throw new FieldReadException($"'{p.Path}' has no '{name}' array.");

    /// <summary>The arrays a piece holds, a _real/_imag pair as one complex array.</summary>
    private static List<FieldArrayInfo> List(VtuPiece p)
    {
        var list = new List<FieldArrayInfo>();
        var names = p.Arrays.Where(a => a.Name is not ("Points" or "connectivity" or "offsets" or "types"))
                            .ToDictionary(a => a.Name, StringComparer.Ordinal);
        foreach (var a in names.Values)
        {
            if (FieldNames.IsBookkeeping(a.Name) || a.Name.EndsWith("_imag", StringComparison.Ordinal)) continue;
            if (a.Name.EndsWith("_real", StringComparison.Ordinal))
            {
                string b = a.Name[..^5];
                bool pair = names.TryGetValue(b + "_imag", out var im) && im.Components == a.Components && im.PerCell == a.PerCell;
                list.Add(new FieldArrayInfo(pair ? b : a.Name, a.Components, pair, a.PerCell));
            }
            else list.Add(new FieldArrayInfo(a.Name, a.Components, false, a.PerCell));
        }
        return list;
    }

    /// <summary>Array <paramref name="name"/>, read from every piece now (and kept), or null when the
    /// step does not hold it.</summary>
    public FieldArray? Load(string name)
    {
        lock (_loaded) if (_loaded.TryGetValue(name, out var have)) return have;
        if (Info(name) is not { } info) return null;
        string reName = info.IsComplex ? name + "_real" : name, imName = name + "_imag";
        int per = info.Components, count = info.PerCell ? Mesh.CellCount : Mesh.NodeCount;
        var re = new float[per * count];
        var im = info.IsComplex ? new float[per * count] : null;
        int at = 0;
        foreach (var p in Pieces)
        {
            int n = info.PerCell ? p.NumberOfCells : p.NumberOfPoints;
            if (n == 0) continue;
            VtuReader.ReadFloats(p, Need(p, reName), re.AsSpan(at * per, n * per));
            if (im is not null) VtuReader.ReadFloats(p, Need(p, imName), im.AsSpan(at * per, n * per));
            at += n;
        }
        var array = new FieldArray { Info = info, Re = re, Im = im };
        lock (_loaded) _loaded[name] = array;
        return array;
    }
}

/// <summary>A mesh attribute's object, from the <c>groups.json</c> GmshGeoWriter writes beside the mesh.</summary>
public sealed record FieldGroup(string Name, int Attribute, int Dimension, string Kind);

public static class FieldGroups
{
    /// <summary>The groups of run directory <paramref name="runDir"/>, or empty when it has no groups.json.</summary>
    public static IReadOnlyList<FieldGroup> Read(string runDir)
    {
        string path = Path.Combine(runDir, CircuitRF.Design.Em3d.GmshGeoWriter.GroupsFile);
        if (!File.Exists(path)) return [];
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return [.. doc.RootElement.GetProperty("Groups").EnumerateArray().Select(g => new FieldGroup(
                g.GetProperty("Name").GetString() ?? "", g.GetProperty("Attribute").GetInt32(),
                g.GetProperty("Dimension").GetInt32(), g.GetProperty("Kind").GetString() ?? ""))];
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException) { return []; }
    }
}
