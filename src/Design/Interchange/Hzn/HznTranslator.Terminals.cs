// brief-hzn-14 R-hzn14-3 — Hertzon's Terminal solution: each wave port becomes a .c3d wave port with Terminals.
//
// WHAT HERTZON DOES, ESTABLISHED BLACK-BOX (three one-frequency runs of a coupled pair, about 4 s each; never its source):
//   - one terminal per conductor reaching the port; a conductor that stops short gives that port none;
//   - numbered port by port in port-number order, then by the conductor's position across the face — x ascending on a y-facing
//     port. Swapping the two traces' order in the file kept the numbering, and swapping their positions changed it;
//   - the Touchstone states R 50, the port's RenormImpedance, although the ports say Renormalize False.
// A port facing another axis is ordered by the same world order (x, then y, then z), which is assumed, not established.
//
// The reference is left to the run's own inference (C3dPorts): a closed design's PEC box faces, which is what Hertzon's empty or
// "Outer" ReferenceConductor means; any other ReferenceConductor names an object, which becomes the port's Reference.

using CircuitRF.Design.ThreeD;

namespace CircuitRF.Design.Interchange.Hzn;

internal sealed partial class HznTranslator
{
    /// <summary>The design's solution type, as Hertzon writes it (Modal when unstated).</summary>
    private string SolutionType => _design.Json.GetObject("settings")?.GetString("SolutionType") ?? "Modal";

    /// <summary>True for a Terminal design.</summary>
    private bool Terminal => SolutionType == "Terminal";

    /// <summary>The Terminal design's wave ports, with the ReferenceConductor each states.</summary>
    private readonly List<(C3dPort Port, string Reference)> _terminalPorts = [];

    private void Terminals()
    {
        if (_terminalPorts.Count == 0) return;
        C3dResolver.Resolve(_doc, C3dCell.None);      // the ports' rectangles were bound after the first resolution; Check reports errors
        var hznOf = _c3dName.GroupBy(kv => kv.Value, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.Ordinal);
        int next = 1;
        bool assumed = false;
        foreach (var (port, referenceText) in _terminalPorts.OrderBy(t => t.Port.Number))
        {
            string name = port.Name;
            string? reference = null;
            string stated = referenceText.Trim();
            if (stated.Length > 0 && stated != "Outer")
            {
                reference = _c3dName.GetValueOrDefault(stated, stated);
                if (!_top.TryGetValue(reference, out var refObj) || !IsConductorObject(refObj))
                {
                    _r.Add(HznRefusalKind.Excitation, $"ReferenceConductor '{stated}'", "it names no conductor of the design", name);
                    continue;
                }
            }
            var (u, v, n) = port.Plane switch { C3dPlane.YZ => (1, 2, 0), C3dPlane.XZ => (0, 2, 1), _ => (0, 1, 2) };
            assumed |= n != 1;
            long h = port.Offset, u0 = port.Rect.Min.U, v0 = port.Rect.Min.V, u1 = u0 + port.Rect.Size.U, v1 = v0 + port.Rect.Size.V;
            var reached = new List<(string Name, long[] Foot)>();
            foreach (var (c3d, obj) in _top)
            {
                if (c3d == reference || !obj.Model || !IsConductorObject(obj) || Bounds(obj) is not { } b) continue;
                if (b.Min[n] > h + 1 || b.Max[n] < h - 1) continue;
                if (!Overlaps(b.Min[u], b.Max[u], u0, u1) || !Overlaps(b.Min[v], b.Max[v], v0, v1)) continue;
                var foot = new long[3];
                foot[n] = h;
                foot[u] = (Math.Max(b.Min[u], u0) + Math.Min(b.Max[u], u1)) / 2;
                foot[v] = (Math.Max(b.Min[v], v0) + Math.Min(b.Max[v], v1)) / 2;
                reached.Add((c3d, foot));
            }
            if (reached.Count == 0)
            {
                _r.Add(HznRefusalKind.Excitation, $"Wave Port '{name}' in a Terminal design", "no conductor reaches the port, so it has no terminal", name);
                continue;
            }
            var ordered = reached.OrderBy(r => r.Foot[0]).ThenBy(r => r.Foot[1]).ThenBy(r => r.Foot[2]).ToList();
            port.Reference = reference;
            if (ordered.Count == 1)
            {
                port.Number = next++;
                continue;
            }
            port.Terminals = [.. ordered.Select(r => new C3dTerminal
            {
                Number = next++, Name = $"{name}_{hznOf.GetValueOrDefault(r.Name, r.Name)}", Conductor = r.Name, Z0 = port.Z0,
            })];
            port.Number = 0;
        }
        if (assumed)
            _notes.Add("A port faces x or z: its terminals are numbered by the conductors' world position (x, then y, then z), which " +
                       "Hertzon was seen to use on y-facing ports only.");

        // A strictly positive overlap where the conductor has extent; touching is enough where it has none (a sheet's thickness).
        static bool Overlaps(long a0, long a1, long b0, long b1) => a1 > a0 ? a0 < b1 && a1 > b0 : a0 >= b0 - 1 && a0 <= b1 + 1;
    }

    /// <summary>A conductor: a sheet (it always conducts), or an object whose role or material says so.</summary>
    private bool IsConductorObject(C3dObject o)
        => C3dSheetBooleans.IsSheet(o) || o.Role == CircuitRF.Engine.Em3d.Em3dRole.Conductor || IsConductorMaterial(o.Material);
}
