using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Text;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Systems;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.VendorLibraries;
using CircuitRF.Engine;

namespace CircuitRF.Design.Filters;

/// <summary>
/// A ladder as the circuit it is BUILT as: an element with a vendor library part is that part's own
/// SPICE model (parasitics, self-resonance, loss), as a placed ladder and a simulated Filter block
/// are, not an ideal L or C at its stated value. The Designer's ladder curve, its mask check and the
/// search for passing values all judge this circuit, so what passes in the window passes when placed.
/// </summary>
public static class FilterLadderCircuit
{
    /// <summary>
    /// The ladder as a <c>.cnl</c>: <see cref="FilterLadder.ToCnl"/>, with each element in
    /// <paramref name="parts"/> written as a <c>SpiceModel</c> line naming its library part. A part that
    /// is a model folder rather than a SPICE subcircuit keeps its stated value.
    /// </summary>
    public static string ToCnl(IReadOnlyList<FilterElement> elements, IReadOnlyDictionary<string, VendorPartRef>? parts,
                               double rs, double rl)
    {
        if (parts is null || parts.Count == 0) return FilterLadder.ToCnl(elements, rs, rl);
        var sb = new StringBuilder();
        sb.Append("Term:T1  p1 0  Num=1 Z=").AppendLine(Num(rs));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in elements)
        {
            string name = e.Name;
            if (!seen.Add(name)) name += "_" + seen.Count;
            if (parts.TryGetValue(e.Name, out var part) && !part.IsModel)
            {
                sb.Append(NetExtractor.NetlistSpiceModelToken).Append(':').Append(name).Append("  ")
                  .Append(e.NodeA).Append(' ').Append(e.NodeB).Append("  ")
                  .Append("File=\"").Append(part.FileRef).Append("\" Name=\"").Append(part.Part).AppendLine("\"");
                continue;
            }
            sb.Append(e.Component).Append(':').Append(name).Append("  ")
              .Append(e.NodeA).Append(' ').Append(e.NodeB).Append("  ")
              .Append(e.Component).Append('=').AppendLine(Num(e.Value));
        }
        sb.Append("Term:T2  ").Append(FilterLadder.LoadNode(elements)).Append(" 0  Num=2 Z=").AppendLine(Num(rl));
        return sb.ToString();
    }

    /// <summary>
    /// Reads a ladder netlist, resolving its library parts through the workspace at
    /// <paramref name="workspaceRoot"/> exactly as a netlist placing them is resolved. Throws when a
    /// part cannot be found, with the reason.
    /// </summary>
    public static (Library Library, TestBench TestBench) Read(string cnl, string? workspaceRoot)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        if (cnl.Contains(NetExtractor.NetlistSpiceModelToken + ":", StringComparison.Ordinal))
            NetExtractor.ResolveNetlistSpiceModels(lib, tb, DirectoryOf(workspaceRoot));
        return (lib, tb);
    }

    /// <summary>S21 of a ladder netlist at each of <paramref name="hz"/>, its library parts as their models.</summary>
    public static Complex[] S21(string cnl, IReadOnlyList<double> hz, string? workspaceRoot)
    {
        var (lib, tb) = Read(cnl, workspaceRoot);
        var ds = SParameterEngine.Run(new Elaborator(lib).Elaborate(tb), hz.ToArray());
        var cube = ds["S"];
        int np = cube.Axes[1].Length;
        var raw = cube.ComplexValues;
        var s21 = new Complex[hz.Count];
        for (int f = 0; f < hz.Count; f++) s21[f] = raw[f * np * np + 1 * np + 0];
        return s21;
    }

    private static readonly ConcurrentDictionary<string, Complex[]?> ImpedanceCache = new(StringComparer.Ordinal);

    /// <summary>
    /// A library part's impedance between its two terminals at each of <paramref name="hz"/>, from its
    /// own model (measured in series between two 50 ohm ports). Null for a model folder, or a part
    /// that does not resolve. Cached per part and frequency set, so a search pays for each part once.
    /// </summary>
    public static Complex[]? PartImpedance(VendorPartRef part, IReadOnlyList<double> hz, string? workspaceRoot)
    {
        if (part.IsModel || hz.Count == 0) return null;
        string key = string.Join("\u0001", part.Library, part.File, part.Part, DirectoryOf(workspaceRoot) ?? "",
                                 hz.Count, hz[0].ToString("R", CultureInfo.InvariantCulture),
                                 hz[^1].ToString("R", CultureInfo.InvariantCulture),
                                 hz[hz.Count / 2].ToString("R", CultureInfo.InvariantCulture));
        return ImpedanceCache.GetOrAdd(key, _ =>
        {
            const double z0 = 50;
            string cnl = $"Term:T1  p1 0  Num=1 Z={z0}\n"
                       + $"{NetExtractor.NetlistSpiceModelToken}:X1  p1 p2  File=\"{part.FileRef}\" Name=\"{part.Part}\"\n"
                       + $"Term:T2  p2 0  Num=2 Z={z0}\n";
            try
            {
                var s21 = S21(cnl, hz, workspaceRoot);
                // In series between two z0 ports: S21 = 2 z0 / (2 z0 + Z).
                return s21.Select(s => s == Complex.Zero ? new Complex(1e15, 0) : 2 * z0 * (1 / s - 1)).ToArray();
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or FormatException
                                           or IOException or KeyNotFoundException)
            {
                return null;
            }
        });
    }

    private static string? DirectoryOf(string? workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot)) return null;
        return Directory.Exists(workspaceRoot) ? workspaceRoot : Path.GetDirectoryName(workspaceRoot);
    }

    private static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}
