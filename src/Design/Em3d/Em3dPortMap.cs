// brief-em3d-93 R-em3d93-1a — the record a result carries of which document port each of its ports is, when a port turned
// off (Model) left the rest renumbered 1…N. The notes say it too, but a Touchstone file is read by a netlist months later
// where the notes are gone (MIM-10's reason for the port lines), so the FILE carries it: one header line per port, and the
// .npy's diagnostics group a cube of the document numbers. Both writers — Palace's and openEMS's — call this, and both write
// nothing extra when no port was renumbered, so every result written before this is byte-identical.

using CircuitRF.Engine.Em3d;
using RfCore.Data;

namespace CircuitRF.Design.Em3d;

public static class Em3dPortMap
{
    /// <summary>The header line's prefix.</summary>
    public const string Prefix = "circuitRF-EM 3D port map: ";

    /// <summary>The diagnostics cube's name: each result port's number in the document.</summary>
    public const string DocumentPortCube = "DocumentPort";

    /// <summary>True when the problem's ports were renumbered from the document's.</summary>
    public static bool Renumbered(IReadOnlyList<Em3dPort> ports) => ports.Any(p => p.SourceNumber is not null);

    /// <summary>brief-em3d-114 R-em3d114-2d — true when any result port is a terminal of a multi-terminal wave port.</summary>
    public static bool HasTerminals(IReadOnlyList<Em3dPort> ports) => ports.Any(p => p.FaceGroup is not null);

    /// <summary>One line per result port — <c>result port 2 is the 3D view's port 3 'P3'</c>, or for a terminal <c>result port 2
    /// is the 3D view's port 'Left', terminal 'P2' (strip_b)</c> — and, when a port was turned off, one saying what the gap
    /// means; none when nothing was renumbered and no port has terminals. ASCII, as every header line is.</summary>
    public static IReadOnlyList<string> Lines(IReadOnlyList<Em3dPort> ports)
    {
        if (!Renumbered(ports) && !HasTerminals(ports)) return [];
        var lines = ports.OrderBy(p => p.Number)
            .Select(p => p.FaceGroup is null
                ? $"{Prefix}result port {p.Number} is the 3D view's port {p.SourceNumber ?? p.Number} '{Ascii(p.SourceLabel ?? p.Name)}'"
                : $"{Prefix}result port {p.Number} is the 3D view's port '{Ascii(p.FaceGroupLabel ?? "")}', terminal " +
                  $"'{Ascii(p.SourceLabel ?? (p.SourceNumber ?? p.Number).ToString(System.Globalization.CultureInfo.InvariantCulture))}' ({Ascii(p.PositiveObject)})")
            .ToList();
        if (Renumbered(ports))
            lines.Add(Prefix + "a port of the 3D view missing above was turned off (Model) for this run: it was left out entirely, " +
                      "open, not terminated.");
        return lines;
    }

    /// <summary>The document numbers as a real cube over the result's ports, in <paramref name="group"/>; nothing when nothing
    /// was renumbered and no port has terminals.</summary>
    public static void AddTo(DataSet ds, string group, IReadOnlyList<Em3dPort> ports)
    {
        if (!Renumbered(ports) && !HasTerminals(ports)) return;
        var ordered = ports.OrderBy(p => p.Number).ToList();
        ds.AddToGroup(group, DocumentPortCube, new DataCube(
            [new Axis("port", [.. ordered.Select(p => (double)p.Number)], "port")],
            [.. ordered.Select(p => (double)(p.SourceNumber ?? p.Number))]));
    }

    /// <summary>The name a result port has in the 3D view: its label (a component port's refdes), or for a terminal of a
    /// multi-terminal port the port's name and the terminal's.</summary>
    public static string Label(Em3dPort p) => p.FaceGroup is null
        ? p.SourceLabel ?? p.Name
        : $"{p.FaceGroupLabel ?? p.Name} {p.SourceLabel ?? (p.SourceNumber ?? p.Number).ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>The <c>Port[N] = name</c> header lines (<see cref="RfCore.TouchstonePortNames"/>): which numbered port of the
    /// file is which named port, for every result port. Every 3D writer calls this, so every 3D result carries it.</summary>
    public static IReadOnlyList<string> NameLines(IReadOnlyList<Em3dPort> ports)
        => RfCore.TouchstonePortNames.Lines(ports.Select(p => (p.Number, (string?)Label(p))));

    private static string Ascii(string s) => new([.. s.Select(c => c < 128 ? c : '?')]);
}
