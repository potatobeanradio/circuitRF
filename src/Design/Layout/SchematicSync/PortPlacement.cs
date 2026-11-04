using System.Globalization;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Schematic;
using static CircuitRF.Design.Layout.SchematicSync.SchematicLayoutPlanner;

namespace CircuitRF.Design.Layout.SchematicSync;

/// <summary>
/// brief-sdl-5 R-sdl5-1: a schematic's Port components become EM port labels on the layout. Runs inside the planner
/// once every part is where the run leaves it (chains, rows, side-on parts), so a port lands on its pin's final place.
///
/// <para><b>D1 — where.</b> A port goes where its signal net meets exactly one layout pin: on the pin's layer, at the
/// pin point (a line's end midpoint) or, for a footprint pad, at SDL-2's outer-edge joint, facing into the metal (the
/// pin's outward direction + 180°, <see cref="LayoutPortDirection.FromPinOutward"/>). Its text is the Port's
/// <c>Num</c>; its kind is left null, so it is inferred from the artwork exactly as a hand-placed port's is.</para>
///
/// <para><b>D2 — what is reported instead.</b> A Port on a net with two or more layout pins (a junction), with none,
/// on the ground net, or between two non-ground nets (differential): one line each, asking for it to be placed by
/// hand. Nothing is guessed.</para>
///
/// <para><b>D3 — a placed label is a linked part.</b> It carries the Port's schematic id
/// (<see cref="LabelShape.SchematicId"/>) and a <see cref="GeneratedPlacement"/> whose <see cref="GeneratedPlacement.Text"/>
/// is the number this command last wrote. A label still on its pin's old position moves with the pin, however the
/// pin moved (a propagated length change, a re-snap); one the user pulled off it stays put; one the user deleted is
/// not put back; a renumbering in the schematic changes its text unless the text was edited in the layout
/// (D-conflict).</para>
/// </summary>
internal static class PortPlacement
{
    /// <summary>One placed part as the run leaves it: <paramref name="After"/>, and <paramref name="Before"/> — the
    /// same part as the layout held it before the run, or null for a part this run adds.</summary>
    internal sealed record PlacedPart(EditableComponent Component, LayoutInstance After, LayoutInstance? Before);

    internal sealed record Input(
        SchematicEditModel Model, NetExtractor.ExtractionResult Extraction, LayoutView Target,
        IReadOnlyList<PlacedPart> Parts, string SchematicDir, string LayoutBaseDir, Technology? Technology,
        IReadOnlySet<string> Resnap, bool RestoreRemoved);

    /// <summary>The labels to add, the labels to replace (index into the target's shapes), the placement records to
    /// write, and the report lines.</summary>
    internal sealed record Outcome(
        IReadOnlyList<LabelShape> Added,
        IReadOnlyList<(int Index, LabelShape Before, LabelShape After)> Replaced,
        IReadOnlyList<(string Id, GeneratedPlacement Record)> Records,
        IReadOnlyList<ReportLine> Lines);

    /// <summary>Whether a schematic component is a port: what the circuit is driven through, not a part to place.</summary>
    public static bool IsPort(EditableComponent comp) => comp.Symbol is SymbolKind.Term or SymbolKind.ZPort;

    /// <summary>A port's number as the schematic states it — the text of its <c>Num</c>, trimmed.</summary>
    public static string NumberText(EditableComponent comp) =>
        comp.Parameters.FirstOrDefault(p => p.Name == "Num")?.Expression?.Trim() ?? "";

    /// <summary>One layout pin on a net: the part, the terminal (1-based), and the pin before and after the run.</summary>
    private sealed record PinEnd(PlacedPart Part, int Terminal, LayoutPin After, LayoutPin? Before);

    public static Outcome Plan(Input input)
    {
        var (model, extraction, target) = (input.Model, input.Extraction, input.Target);
        var bindings = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var inst in extraction.TestBench.Instances) bindings.TryAdd(inst.InstanceName, TerminalNets.Of(inst));

        // Every layout pin on every net, as the run leaves the parts (and as they were before it, for a part that was
        // already placed) — the pins the chain joins by, so a pad's is its outer-edge joint (SDL-2 D2).
        var pinsOn = new Dictionary<string, List<PinEnd>>(StringComparer.Ordinal);
        foreach (var part in input.Parts)
        {
            if (part.After.SchematicId is not { Length: > 0 } sid || !bindings.TryGetValue(sid, out var nets)) continue;
            var after = SchematicLayoutPlanner.PartTerminals(part.Component, part.After, input.SchematicDir, input.LayoutBaseDir,
                                                            input.Technology, out _, out _);
            if (after is null) continue;
            var before = part.Before is null ? null
                : SchematicLayoutPlanner.PartTerminals(part.Component, part.Before, input.SchematicDir, input.LayoutBaseDir,
                                                      input.Technology, out _, out _);
            for (int k = 1; k <= Math.Min(after.Count, nets.Count); k++)
            {
                if (after[k - 1] is not { } pin || nets[k - 1] is not { Length: > 0 } net) continue;
                if (!pinsOn.TryGetValue(net, out var list)) pinsOn[net] = list = [];
                list.Add(new PinEnd(part, k, pin, before is not null && k - 1 < before.Count ? before[k - 1] : null));
            }
        }

        // The labels this command placed, by the Port they belong to.
        var labelOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < target.Shapes.Count; i++)
            if (target.Shapes[i] is LabelShape { IsPort: true, SchematicId: { Length: > 0 } sid }) labelOf.TryAdd(sid, i);

        var added = new List<LabelShape>();
        var replaced = new List<(int, LabelShape, LabelShape)>();
        var records = new List<(string, GeneratedPlacement)>();
        var lines = new List<ReportLine>();
        var ports = model.Components.Where(c => IsPort(c) && c.InstanceName is { Length: > 0 }
                                                && c.Disable is not (DisableState.Open or DisableState.Short))
                         .OrderBy(c => c.InstanceName, Comparer<string>.Create(JointGraph.CompareIds)).ToList();
        var portIds = new HashSet<string>(ports.Select(p => p.InstanceName), StringComparer.Ordinal);

        foreach (var port in ports)
        {
            string id = port.InstanceName;
            string num = NumberText(port);
            void ByHand(string why) => lines.Add(new ReportLine(id, $"{id} — {why}; place port {Shown(num)} by hand with the Port tool",
                ReportSeverity.Warning) { Kind = ReportKind.PortByHand, Ids = [id] });

            if (!bindings.TryGetValue(id, out var nets) || nets.Count == 0 || nets[0] is not { Length: > 0 } signal)
            {
                ByHand("it is connected to nothing");
                continue;
            }
            var others = nets.Skip(1).Where(n => n is { Length: > 0 } && n != "0").Distinct(StringComparer.Ordinal).ToList();
            if (signal != "0" && others.Count > 0)
            {
                ByHand($"it is driven between two nets ({signal} and {others[0]}), not against ground");
                continue;
            }
            if (signal == "0")
            {
                ByHand("its signal terminal is on the ground net");
                continue;
            }
            var ends = pinsOn.GetValueOrDefault(signal) ?? [];
            if (ends.Count == 0)
            {
                ByHand($"no part on net {signal} has a layout pin");
                continue;
            }
            if (ends.Count > 1)
            {
                ByHand($"net {signal} meets {ends.Count} layout pins ({string.Join(", ", ends.Select(EndName))}), so the port is at a junction");
                continue;
            }

            var end = ends[0];
            var (x, y, deg) = ChainPose.Of(end.Part.After).Apply(end.After);
            long px = (long)Math.Round(x), py = (long)Math.Round(y);
            var direction = LayoutPortDirection.FromPinOutward(deg);
            // A port faces along an axis (LabelShape.PortDirection is four-way, and the EM extractor reads a port by the
            // side of its conductor), so a line ending at another angle has no port the placer could state truthfully.
            double offAxis = Math.Abs(LayoutAngle.Normalize(deg + 45.0) % 90.0 - 45.0);
            if (offAxis > 0.01)
            {
                ByHand($"{EndName(end)} faces {deg.ToString("0.#", CultureInfo.InvariantCulture)}°, and a port faces along an axis");
                continue;
            }
            var record = target.GeneratedPlacements.GetValueOrDefault(id);

            if (!labelOf.TryGetValue(id, out int index))
            {
                // A label this command placed and the user has since deleted is not put back (SDL-4's rule for a part).
                if (record is not null && !input.RestoreRemoved)
                {
                    lines.Add(new ReportLine(id, $"{id} — port label removed from the layout by hand, so not put back " +
                        "(Layout ▸ Restore Removed Parts puts it back)", ReportSeverity.Info) { Ids = [id] });
                    if (!record.Removed) records.Add((id, record with { Removed = true }));
                    continue;
                }
                var label = new LabelShape
                {
                    Layer = end.After.Layer, PortLayer = end.After.Layer,
                    X = px, Y = py, Text = num,
                    Height = Math.Max(end.After.WidthDbu / 2, target.DbuPerMicron),
                    IsPort = true, PortDirection = direction, SchematicId = id,
                };
                added.Add(label);
                records.Add((id, RecordOf(label)));
                lines.Add(new ReportLine(id, $"{id} — port {Shown(num)} placed at {EndName(end)}", ReportSeverity.Info) { Ids = [id] });
                continue;
            }

            var existing = (LabelShape)target.Shapes[index];
            var next = (LabelShape)LayoutGeometry.Clone(existing);

            // Position (D3): a label still on its pin's old place follows the pin; Re-snap puts it back on it; one the
            // user pulled off it stays where it was put.
            var old = end.Before is { } b && end.Part.Before is { } beforeInst ? ChainPose.Of(beforeInst).Apply(b) : ((double, double, double)?)null;
            bool onPin = old is { } o && Math.Abs(existing.X - o.Item1) <= 1 && Math.Abs(existing.Y - o.Item2) <= 1;
            bool resnap = input.Resnap.Contains(id);
            if (onPin || resnap)
            {
                (next.X, next.Y) = (px, py);
                // The arrow turns with the pin unless the user turned it by hand; Re-snap restores it.
                if (resnap || existing.PortDirection is null
                    || old is { } od && existing.PortDirection == LayoutPortDirection.FromPinOutward(od.Item3))
                    next.PortDirection = direction;
                next.Layer = end.After.Layer;
                next.PortLayer = end.After.Layer;
            }
            else if (existing.X != px || existing.Y != py)
                lines.Add(new ReportLine(id, $"{id} — port label is off its pin ({EndName(end)}), where it was moved by hand; " +
                    "left there (Re-snap puts it back)", ReportSeverity.Info) { Ids = [id] });

            // Text (D3 + D-conflict): the record's text is what this command last wrote.
            string? baseline = record?.Text;
            bool layoutChanged = baseline is not null && existing.Text != baseline;
            bool schematicChanged = baseline is null ? existing.Text != num : num != baseline;
            string recordText = baseline ?? num;
            if (resnap || (!layoutChanged && schematicChanged))
            {
                if (existing.Text != num)
                    lines.Add(new ReportLine(id, $"{id} — port {Shown(existing.Text)} renumbered to {Shown(num)}", ReportSeverity.Info) { Ids = [id] });
                next.Text = num;
                recordText = num;
            }
            else if (layoutChanged && !schematicChanged)
                lines.Add(new ReportLine(id, $"{id} — port label reads {Shown(existing.Text)} in the layout; the schematic says " +
                    $"{Shown(num)}. The layout's value is kept", ReportSeverity.Info) { Ids = [id] });
            else if (layoutChanged && schematicChanged && existing.Text != num)
                lines.Add(new ReportLine(id, $"{id} — port number changed on both sides: the layout's label reads {Shown(existing.Text)}, " +
                    $"the schematic changed it from {Shown(baseline!)} to {Shown(num)}. The layout's value is kept",
                    ReportSeverity.Warning) { Ids = [id] });

            if (!SameLabel(existing, next)) replaced.Add((index, existing, next));
            var newRecord = RecordOf(next) with { Text = recordText };
            if (record != newRecord) records.Add((id, newRecord));
        }

        // A label whose Port is gone from the schematic: reported, never deleted (R-L5-4's rule for a part).
        foreach (var (sid, index) in labelOf.OrderBy(kv => kv.Key, Comparer<string>.Create(JointGraph.CompareIds)))
            if (!portIds.Contains(sid))
                lines.Add(new ReportLine(sid, $"{sid} — port label {Shown(((LabelShape)target.Shapes[index]).Text)} has no Port in the " +
                    "schematic any more; left in place", ReportSeverity.Warning) { Ids = [sid] });

        return new Outcome(added, replaced, records, lines);
    }

    /// <summary>D4: the port numbers are checked where they already are — <see cref="EmComponentPorts"/> for a top-level
    /// label against a component's port, <see cref="EmPortExtraction"/> for two labels. Null when the layout as the run
    /// leaves it numbers every port once; otherwise that check's own refusal.</summary>
    /// <summary>D4: the port numbers are checked where they already are — <see cref="EmComponentPorts"/> for a top-level
    /// label against a component's port, <see cref="EmPortExtraction.DuplicateNumberRefusal"/> for two labels. Null when
    /// the layout as the run leaves it numbers every port once; otherwise that check's own refusal.</summary>
    public static string? DuplicateRefusal(LayoutView future, string clayPath, Technology? technology)
    {
        var collected = EmComponentPorts.Collect(new EmLayoutSource(clayPath, future, technology, future.DbuPerMicron));
        return collected.Refusal
               ?? EmPortExtraction.DuplicateNumberRefusal(collected.Shapes, future.DbuPerMicron, future.DisplayUnit);
    }

    private static GeneratedPlacement RecordOf(LabelShape label) => new()
    {
        X = label.X, Y = label.Y,
        RotationDeg = label.PortDirection is { } d ? LayoutAngle.OfCardinal(d) : 0,
        Text = label.Text,
    };

    private static bool SameLabel(LabelShape a, LabelShape b) =>
        a.X == b.X && a.Y == b.Y && a.Text == b.Text && a.PortDirection == b.PortDirection && a.Layer == b.Layer
        && a.PortLayer == b.PortLayer;

    private static string EndName(PinEnd end)
    {
        string pin = end.After.Name is { Length: > 0 } n ? n : end.Terminal.ToString(CultureInfo.InvariantCulture);
        return $"{end.Part.After.SchematicId} pin {pin}";
    }

    private static string Shown(string text) => text.Length > 0 ? text : "(no number)";
}
