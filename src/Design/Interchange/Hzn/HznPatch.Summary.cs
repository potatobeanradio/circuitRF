// HZN-5 R-hzn5-4 — the edits a document holds, by name, for the dialog that offers to discard them.

using CircuitRF.Design.ThreeD;

namespace CircuitRF.Design.Interchange.Hzn;

internal sealed partial class HznPatch
{
    internal static IReadOnlyList<string> Summary(C3dDocument opened, C3dDocument edited)
    {
        var e = Clone(edited);
        var lines = new List<string>();
        void Add(string what, Delta d, bool showAdded = true)
        {
            if (d.Changed.Count > 0) lines.Add($"{what} changed: {string.Join(", ", d.Changed)}");
            if (showAdded && d.Added.Count > 0) lines.Add($"{what} added: {string.Join(", ", d.Added)}");
            if (d.Removed.Count > 0) lines.Add($"{what} deleted: {string.Join(", ", d.Removed)}");
        }
        Add("Objects", Compare(Read(opened, "Objects", x => Str(x, "Name")), Read(e, "Objects", x => Str(x, "Name"))));
        Add("Variables", Compare(Read(opened, "Variables", x => Str(x, "Name")), Read(e, "Variables", x => Str(x, "Name"))));
        var ports = Compare(Read(opened, "Ports", x => Str(x, "Number")), Read(e, "Ports", x => Str(x, "Number")));
        Add("Ports", new Delta([.. ports.Removed.Select(n => "port " + n)], [.. ports.Added.Select(n => "port " + n)], [.. ports.Changed.Select(n => "port " + n)]));
        var faces = Compare(Read(opened, "FaceBoundaries", x => Str(x, "Object") + "." + Str(x, "Face")),
                            Read(e, "FaceBoundaries", x => Str(x, "Object") + "." + Str(x, "Face")));
        Add("Face boundaries", faces);
        Add("Setups", Compare(Read(opened, "Setups", x => Str(x, "Name")), Read(e, "Setups", x => Str(x, "Name"))));
        return lines;
    }
}
