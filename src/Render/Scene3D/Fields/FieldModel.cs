// brief-em3d-29 R-em3d29-2b / 2c — the field model, GENERIC (overview §4): a mesh plus named arrays, each a
// scalar or a 3-vector, real or complex, per node or per cell. Nothing here says "electric" — the friendly
// names are a lookup the UI consults, and an array with no friendly name is still listed under its own —
// so F3's temperature is one more array, drawn by the same code.
//
// MEMORY (R-em3d29-2c): flat float arrays exactly as the files store them (Float32), never boxed and never
// widened to double on load. A step's GEOMETRY is read when it is opened; an ARRAY only when something
// asks to draw it, so listing a million-unknown run's quantities reads no field values at all.

using System.Text.RegularExpressions;

namespace CircuitRF.Render.Scene3D.Fields;

/// <summary>The cells of a field mesh: the volume's tetrahedra or a boundary's triangles.</summary>
public enum FieldCellShape { Tetrahedron, Triangle }

/// <summary>
/// One step's mesh, every rank's piece merged. Cell <c>c</c>'s nodes are <c>Cells[c·NodesPerCell ..]</c>
/// in VTK's Lagrange order: the corners, then one node per edge — (0,1),(1,2),(0,2),(0,3),(1,3),(2,3) for a
/// tetrahedron, (0,1),(1,2),(2,0) for a triangle. Palace gives each cell its OWN nodes (a Nédélec field is
/// not continuous across element faces, so it shares none); openEMS's grid, split into tetrahedra, shares
/// its grid nodes. Nothing here depends on which.
/// </summary>
public sealed class FieldMesh
{
    public required FieldCellShape Shape { get; init; }
    /// <summary>The element order the nodes describe: 1 (corners only) or 2 (corners and edge midpoints).</summary>
    public required int Order { get; init; }
    public required int NodesPerCell { get; init; }
    /// <summary>x, y, z per node, in the mesh's length unit (Palace's L0).</summary>
    public required float[] Points { get; init; }
    public required int[] Cells { get; init; }
    /// <summary>Each cell's mesh attribute — the Gmsh physical group, so the object it belongs to.</summary>
    public required int[] Attribute { get; init; }
    /// <summary>Metres per mesh unit.</summary>
    public required double ToMetres { get; init; }

    public int CellCount => Attribute.Length;
    public int NodeCount => Points.Length / 3;

    /// <summary>Bytes this mesh keeps resident.</summary>
    public long ResidentBytes => 4L * (Points.Length + Cells.Length + Attribute.Length);
}

/// <summary>What an array holds, before any of its values are read.</summary>
public sealed record FieldArrayInfo(string Name, int Components, bool IsComplex, bool PerCell)
{
    /// <summary>Values per node (or cell): the components, twice over when complex (real, then imaginary).</summary>
    public int Channels => Components * (IsComplex ? 2 : 1);
}

/// <summary>A loaded array. <see cref="Im"/> is null for a real one.</summary>
public sealed class FieldArray
{
    public required FieldArrayInfo Info { get; init; }
    public required float[] Re { get; init; }
    public float[]? Im { get; init; }
    public string Name => Info.Name;
    public int Components => Info.Components;
    public long ResidentBytes => 4L * (Re.Length + (Im?.Length ?? 0));
}

/// <summary>
/// The known arrays' friendly names and units (R-em3d29-2b): the UI lists every array a file holds, and
/// these are the ones it can say more about. Keyed by the name Palace writes.
/// </summary>
public static class FieldNames
{
    private static readonly Dictionary<string, (string Friendly, string Unit)> Known = new(StringComparer.Ordinal)
    {
        ["E"]         = ("Electric field E", "V/m"),
        ["B"]         = ("Magnetic flux density B", "T"),
        ["H"]         = ("Magnetic field H", "A/m"),
        ["J_s"]       = ("Surface current J_s", "A/m"),
        ["Q_s"]       = ("Surface charge Q_s", "C/m²"),
        ["V"]         = ("Potential V", "V"),
        ["A"]         = ("Vector potential A", "Wb/m"),
        ["S"]         = ("Poynting vector S", "W/m²"),
        ["U_e"]       = ("Electric energy density", "J/m³"),
        ["U_m"]       = ("Magnetic energy density", "J/m³"),
        ["Indicator"] = ("Error indicator", ""),
        ["Rank"]      = ("MPI rank", ""),
    };

    private static readonly Regex PortMode = new(@"^E0_(\d+)$", RegexOptions.CultureInvariant);

    /// <summary>A display name for array <paramref name="name"/>: the friendly one, or the name itself.</summary>
    public static string Friendly(string name)
        => Known.TryGetValue(name, out var k) ? k.Friendly
         : PortMode.Match(name) is { Success: true } m ? $"Port {m.Groups[1].Value} mode field" : name;

    /// <summary>The unit of array <paramref name="name"/>, or "" when it is not a known one.</summary>
    public static string Unit(string name)
        => Known.TryGetValue(name, out var k) ? k.Unit : PortMode.IsMatch(name) ? "V/m" : "";

    /// <summary>Geometry and bookkeeping, never a quantity to draw.</summary>
    public static bool IsBookkeeping(string name) => name is "attribute" or "Rank";
}
