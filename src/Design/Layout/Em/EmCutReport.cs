// brief-icp-6 R-icp6-2/3 — the pin check before meshing: each cut instance, how it was chosen and what left the problem with it,
// and each of its pins' verdicts (routed with its port, abutting, grounded, a return, floating, or left out with why). Read from
// the build (ICP-2's cut and its ports), never recomputed, so `explain`, `check` and a dry run say exactly what a run would solve.

using System.Globalization;
using CircuitRF.Design.ThreeD;

namespace CircuitRF.Design.Layout.Em;

/// <summary>A cut port as the report reads it: its name (<c>path.pin</c>), number, reference pin, and refusal.</summary>
public sealed record EmCutReportPort(string Name, int Number, string? ReferencePin, string? Refusal);

public static class EmCutReport
{
    /// <summary>The step an instance's line carries in <c>explain</c>'s walk.</summary>
    public const string InstanceStep = "cell instance";

    /// <summary>The step a pin's line carries.</summary>
    public const string PinStep = "cut pin";

    /// <summary>A Planar build's cut ports, for <see cref="Lines"/>.</summary>
    public static IReadOnlyList<EmCutReportPort> PortsOf(EmPlanarComponentPortBuild build)
        => [.. build.Ports.Where(p => p.Cutout && p.Pin is not null).Select(p => new EmCutReportPort(p.Name, p.Number, p.ReferencePin, p.Refusal))];

    /// <summary>A 3D build's cut ports, for <see cref="Lines"/>; their names carry the view's layout prefix.</summary>
    public static IReadOnlyList<EmCutReportPort> PortsOf(C3dComponentPortBuild build)
        => [.. build.Ports.Where(p => p.Cutout && p.Pin is not null)
                          .Select(p => new EmCutReportPort($"{p.Refdes}.{p.Pin}", p.Port?.Number ?? 0, p.ReferencePin, p.Refusal))];

    /// <summary>The cut instances a build holds, each once, in the order their ports are numbered, with the prefix its ports' names
    /// carry (a 3D view placing several layouts names them <c>U1/X1</c>).</summary>
    public static IReadOnlyList<(EmCoSimCut Cut, string Prefix)> Cuts(IEnumerable<(string Refdes, EmCoSimCut? Cut)> ports)
    {
        var list = new List<(EmCoSimCut, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (refdes, cut) in ports)
        {
            if (cut is null || !seen.Add(refdes)) continue;
            string prefix = refdes.Length > cut.Path.Length && refdes.EndsWith(cut.Path, StringComparison.OrdinalIgnoreCase) ? refdes[..^cut.Path.Length] : "";
            list.Add((cut, prefix));
        }
        return list;
    }

    /// <summary>
    /// One line for the instance and one per pin, each with its step. <paramref name="ports"/> are the build's cut ports;
    /// <paramref name="layer"/> names a drawing layer as the technology does.
    /// </summary>
    public static IReadOnlyList<(string Step, string Text)> Lines(EmCoSimCut cut, string prefix, IReadOnlyList<EmCutReportPort> ports,
                                                                 Func<LayerKey, string> layer)
    {
        string at = prefix + cut.Path;
        var lines = new List<(string, string)>
        {
            (InstanceStep, $"{at} ({cut.CellLabel}), selected {cut.Selected}: left out of the solved problem with {cut.LeftOut}; " +
                           $"{cut.Pins.Count} pin(s), {ports.Count(p => p.Refusal is null && Mine(p.Name))} port(s) built."),
        };
        foreach (var pin in cut.Pins)
        {
            string name = $"{at}.{pin.Name}";
            string face = pin.Face is { } f
                ? $"face at ({Mm(f.X)}, {Mm(f.Y)}) mm, {Um(f.WidthM)} µm wide, facing {Deg(f.OutwardDeg)}°, on '{layer(f.Layer)}'"
                : "no face found";
            var port = ports.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            string text = pin.Why is { } why ? $"{name}: left out — {why}"
                : pin.Class switch
                {
                    EmPinClass.Routed when port is { Refusal: null } built =>
                        $"{name}: routed, {face}; port {built.Number} '{built.Name}', " +
                        (built.ReferencePin is { } rp ? $"referenced to pin {rp}" : "returning through the run's ground"),
                    EmPinClass.Routed when port is { Refusal: { } refused } => $"{name}: routed, {face}; its port was not built — {refused}",
                    EmPinClass.Routed => $"{name}: routed, {face}",
                    EmPinClass.Abutting => $"{name}: abutting {pin.Abuts}, {face}; no port: the two models' terminals join directly",
                    EmPinClass.Grounded => $"{name}: grounded, {face}; no port: tied to the run's ground at its face",
                    EmPinClass.Return => $"{name}: a return, {face}; no port of its own: " +
                                         $"{string.Join(" and ", ports.Where(p => p.Refusal is null && p.ReferencePin is { } r && r.Equals(pin.Name, StringComparison.OrdinalIgnoreCase) && Mine(p.Name)).Select(p => p.Name).DefaultIfEmpty("no built port"))} return through it",
                    EmPinClass.Floating => $"{name}: floating, {face}; no port: nothing meets it, so the model sees an open there",
                    _ => $"{name}: {face}",
                };
            lines.Add((PinStep, text));
        }
        return lines;

        bool Mine(string portName) => portName.StartsWith(at + ".", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// R-icp6-2 — the copper instances <c>"*"</c> passed over: placed metal the solver solves, which only a name or a cell entry cuts
    /// out. Each once, the top-most of a branch; none when the setup names no <c>"*"</c>.
    /// </summary>
    public static IReadOnlyList<string> PassedOver(EmPlanarComponentPortBuild build, IReadOnlyList<EmComponentPort> entries)
    {
        if (!entries.Any(e => e.Cell is null && e.Refdes.Trim() == "*")) return [];
        var cut = build.Cut?.Excluded ?? new HashSet<string>();
        bool UnderCut(string path) => cut.Any(c => path.Equals(c, StringComparison.OrdinalIgnoreCase) || path.StartsWith(c + "/", StringComparison.OrdinalIgnoreCase)
                                                   || c.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase));
        var copper = build.Instances.Instances.Where(i => i.Kind == EmInstanceKind.Copper && !UnderCut(i.Path)).ToList();
        var paths = copper.Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. copper.Where(i => i.Parent is null || !paths.Contains(i.Parent))
                         .Select(i => $"{i.Path} ({i.CellLabel}): copper the solver can solve, passed over by \"*\": name it, or select its cell, to cut it out.")];
    }

    private static string Mm(double m) => (m * 1e3).ToString("0.####", CultureInfo.InvariantCulture);
    private static string Um(double m) => C3dComponentPorts.Um(m);
    private static string Deg(double d) => d.ToString("0.#", CultureInfo.InvariantCulture);
}
