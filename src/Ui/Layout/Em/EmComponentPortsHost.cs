// brief-lcp-3 R-lcp3-1 — the Component ports list, on either container. What the list needs of where it is shown is three
// things: the placed parts, a build-and-verify for Check, and somewhere to post the verdicts. A 3D view's setup answers them
// from its document and the elaboration it already holds (brief-emb-1's behaviour, unchanged); a layout's .cem answers them
// from the layout the panel has already resolved, through the builder the run itself calls (EmPlanarComponentPorts.Build), so
// a verdict in the panel is the verdict the run reaches. No second construction.
//
// Both reads run off the UI thread: a board's projection and every part's port is seconds of work. The UI-thread half of each
// (Prepare…) captures what it reads as it stands — a serialized copy of the document or the layout — so the pool half touches
// nothing the editor can change under it.

using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;

namespace CircuitRF.Ui.Layout.Em;

/// <summary>One placed part the list offers: its refdes, and its pins (the reference picker's rows).</summary>
public sealed record EmComponentPart(string Refdes, IReadOnlyList<string> Pins)
{
    public int Pads => Pins.Count;
}

/// <summary>The placed parts, or why there are none to list. <paramref name="Read"/>: the parts were read, so a part the setup
/// names and this list lacks is not placed; false when nothing could be read (no layout, a 3D view not built yet).</summary>
public sealed record EmComponentPartListing(IReadOnlyList<EmComponentPart> Parts, string? Status, bool Read = true)
{
    public static EmComponentPartListing Unread(string why) => new([], why, Read: false);
}

/// <summary>One built or refused component port, and the line Messages shows for it.</summary>
public sealed record EmComponentPortVerdict(string Refdes, bool Ok, string Line);

/// <summary>What Check found: each port's verdict, the build's other warnings, why the list as a whole was not built, and
/// whether nothing was.</summary>
public sealed record EmComponentPortCheck(IReadOnlyList<EmComponentPortVerdict> Ports, IReadOnlyList<string> Warnings,
                                          string? Refusal, bool NotBuilt)
{
    public static EmComponentPortCheck Refused(string why) => new([], [], why, true);
}

/// <summary>What the Component ports list reads of the container it is shown in.</summary>
public interface IEmComponentPortsHost
{
    /// <summary>Changes when the placed parts may have: the list is read again only when it moves.</summary>
    string PartsKey();

    /// <summary>On the UI thread: captures what the listing reads. The function returned runs off it.</summary>
    Func<EmComponentPartListing> PrepareList();

    /// <summary>On the UI thread: captures <paramref name="setup"/> and its geometry as they stand. The function returned runs off it.</summary>
    Func<EmComponentPortCheck> PrepareCheck(EmSetup setup);

    /// <summary>Posts one line to Messages; <paramref name="warning"/> marks it as one.</summary>
    void Post(string text, bool warning);

    /// <summary>brief-lcp-4 — on the UI thread: captures what the layout's component-port marks are built from; the job's
    /// <see cref="EmComponentPortMarksJob.Build"/> runs off it. Null when this container draws its ports itself (a 3D view
    /// does, in 3D) or the layout has not resolved.</summary>
    EmComponentPortMarksJob? PrepareMarks(EmSetup setup);
}

/// <summary>brief-lcp-4 — one build of the marks a layout draws for a setup's component ports.</summary>
/// <param name="View">The layout the marks are about: the open editor's own model when it is open.</param>
/// <param name="EditCount">That layout's <see cref="LayoutView.EditCount"/> when it was copied: marks built from an older copy
/// are dropped.</param>
/// <param name="Build">Builds the ports on the copy, as a run would, and returns the built ones; refused parts are not drawn
/// (R-lcp4-5).</param>
public sealed record EmComponentPortMarksJob(LayoutView View, long EditCount, Func<IReadOnlyList<LayoutComponentPortMark>> Build);

/// <summary>brief-emb-1 — a 3D view's embedded setup: its document, path and workspace, and the elaboration the view holds.</summary>
public sealed class C3dComponentPortsHost(Func<C3dDocument> document, Func<string> path, Func<string?> workspace,
                                          Func<C3dElaboration?> elaboration, Action<string, bool>? post = null) : IEmComponentPortsHost
{
    public string PartsKey()
        => elaboration() is { } e ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e).ToString(System.Globalization.CultureInfo.InvariantCulture) : "";

    public Func<EmComponentPartListing> PrepareList()
    {
        if (elaboration() is not { } e) return () => EmComponentPartListing.Unread("The 3D view has not been built yet.");
        return () =>
        {
            var parts = C3dComponentPorts.PlacedParts(e).Select(p => new EmComponentPart(p.Refdes, PinsOf(p.Pads))).ToList();
            return new(parts, parts.Count == 0 ? "No part with a reference designator is placed on this 3D view's layout." : null);
        };
    }

    /// <summary>The 3D lister counts pads without naming them; the per-part settings of a 3D view are its Properties'.</summary>
    private static IReadOnlyList<string> PinsOf(int pads) => [.. Enumerable.Range(1, pads).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture))];

    public Func<EmComponentPortCheck> PrepareCheck(EmSetup setup)
    {
        string where = path(), documentText = C3dPersistence.Serialize(document());
        string? ws = workspace();
        var forRun = C3dSetups.ForRun(setup, where);
        return () =>
        {
            var g = C3dProblemAssembly.Assemble(forRun, C3dPersistence.Deserialize(documentText), where, ws);
            if (g.ComponentPorts is not { } build)
                return EmComponentPortCheck.Refused($"Component ports: {g.Refusal ?? "the ports were not built."}");
            var verdicts = build.Ports.Select(p => new EmComponentPortVerdict(p.Refdes, p.Ok, p.Ok ? C3dComponentPorts.Describe(p) : p.Refusal!)).ToList();
            // The left-out parts carry their reasons; the build's other warnings (no model on the schematic) follow.
            var refusals = build.Ports.Where(p => p.Refusal is not null).Select(p => p.Refusal!).ToList();
            var warnings = build.Warnings.Where(w => !refusals.Any(r => w.StartsWith(r, StringComparison.Ordinal))).ToList();
            return new(verdicts, warnings, build.Refusal, build.Refusal is not null || g.Refusal is not null);
        };
    }

    public void Post(string text, bool warning) => post?.Invoke(text, warning);

    /// <summary>A 3D view draws its component ports in its own scene.</summary>
    public EmComponentPortMarksJob? PrepareMarks(EmSetup setup) => null;
}

/// <summary>brief-lcp-3 — a layout's <c>.cem</c>: the layout the panel resolved at its last refresh.</summary>
public sealed class LayoutComponentPortsHost(Func<EmLayoutSource?> source, Func<Action<string, bool>?> post) : IEmComponentPortsHost
{
    /// <summary>The layout, its technology and what is placed on it — not where: moving a part does not change the list.</summary>
    public string PartsKey()
        => source() is not { } s ? ""
         : s.AbsolutePath + "|" + s.Technology?.Name + "|" + string.Join(";", s.View.Instances.Select(i => $"{i.DisplayRefDes}\u001f{i.CellRef}\u001f{i.Rows}x{i.Cols}"));

    public Func<EmComponentPartListing> PrepareList()
    {
        if (source() is not { } s) return () => EmComponentPartListing.Unread("The layout has not resolved, so no part can be listed.");
        string text = LayoutPersistence.Serialize(s.View);
        return () =>
        {
            var view = LayoutPersistence.Deserialize(text);
            var placed = EmPartPads.Project(view, s.AbsolutePath, s.Technology, C3dTransform.Identity);
            if (placed.Refusal is { } why) return EmComponentPartListing.Unread($"The layout's parts cannot be read: {why}");
            var parts = placed.Parts.DistinctBy(p => p.Refdes, StringComparer.OrdinalIgnoreCase)
                                    .OrderBy(p => p.Refdes, Comparer<string>.Create(NaturalCompare))
                                    .Select(p => new EmComponentPart(p.Refdes, [.. p.Pads.Select(d => d.Pin)])).ToList();
            return new(parts, parts.Count == 0 ? "No part with a reference designator is placed on this layout." : null);
        };
    }

    public Func<EmComponentPortCheck> PrepareCheck(EmSetup setup)
    {
        // A thermal or electrostatic setup has no ports: the one rule em, explain and check read.
        if (EmComponentPorts.Inapplicable(setup) is { } inapplicable)
            return () => EmComponentPortCheck.Refused(inapplicable);
        if (source() is not { } s) return () => EmComponentPortCheck.Refused("Component ports: the layout has not resolved, so no port can be built.");
        string text = LayoutPersistence.Serialize(s.View);
        var copy = setup.Clone();
        // brief-lcp-5 — a 3D setup builds them in a 3D view of the layout, by the run's own door, and says so as a 3D view's does.
        if (setup.Is3D)
            return () =>
            {
                if (s.Technology is not { } tech) return EmComponentPortCheck.Refused("Component ports: the layout's technology does not resolve.");
                var g = Em3dSetupProblem.Of(copy, s with { View = LayoutPersistence.Deserialize(text) }, tech);
                if (g.ComponentPorts is not { } built)
                    return EmComponentPortCheck.Refused($"Component ports: {g.Refusal ?? "the ports were not built."}");
                var lines = built.Ports.Select(p => new EmComponentPortVerdict(p.Refdes, p.Ok, p.Ok ? C3dComponentPorts.Describe(p) : p.Refusal!)).ToList();
                var refused = built.Ports.Where(p => p.Refusal is not null).Select(p => p.Refusal!).ToList();
                var others = built.Warnings.Where(w => !refused.Any(r => w.StartsWith(r, StringComparison.Ordinal))).ToList();
                return new(lines, others, built.Refusal, built.Refusal is not null || g.Refusal is not null);
            };
        return () =>
        {
            var at = s with { View = LayoutPersistence.Deserialize(text) };
            if (EmPlanarComponentPorts.Build(copy, at) is not { } build)
                return EmComponentPortCheck.Refused("Component ports: the setup names no part.");
            var verdicts = build.Ports.Select(p => new EmComponentPortVerdict(p.Refdes, p.Ok, p.Ok ? Describe(p) : p.Refusal!)).ToList();
            var refusals = build.Ports.Where(p => p.Refusal is not null).Select(p => p.Refusal!).ToList();
            var warnings = build.Warnings.Where(w => !refusals.Any(r => w.Contains(r, StringComparison.Ordinal))).ToList();
            return new(verdicts, warnings, build.Refusal, build.Refusal is not null);
        };
    }

    public void Post(string text, bool warning) => post()?.Invoke(text, warning);

    /// <summary>brief-lcp-4 — the marks: <see cref="EmPlanarComponentPorts.Build"/> on a serialized copy, the builder a run
    /// calls, so a mark sits where the run's port is. A setup that cannot take component ports (thermal, electrostatic) builds
    /// none. brief-lcp-5 R-lcp5-7 — a 3D setup's are the sheets of the 3D view its run builds (<see cref="Em3dSetupProblem"/>),
    /// each drawn as the same marker at the sheet's centre, pointing from its positive pad to its negative one.</summary>
    public EmComponentPortMarksJob? PrepareMarks(EmSetup setup)
    {
        if (source() is not { } s) return null;
        if (setup.ComponentPorts.Count == 0 || EmComponentPorts.Inapplicable(setup) is not null)
            return new(s.View, s.View.EditCount, () => []);
        long at = s.View.EditCount;
        string text = LayoutPersistence.Serialize(s.View);
        var copy = setup.Clone();
        if (setup.Is3D)
            return new(s.View, at, () =>
            {
                if (s.Technology is not { } tech) return [];
                var at3 = s with { View = LayoutPersistence.Deserialize(text) };
                return Em3dSetupProblem.Of(copy, at3, tech).ComponentPorts is { } built ? SheetMarks(built, at3) : [];
            });
        return new(s.View, at, () =>
        {
            var build = EmPlanarComponentPorts.Build(copy, s with { View = LayoutPersistence.Deserialize(text) });
            return build is null ? []
                : [.. build.Ports.Where(p => p.Ok).Select(p => new LayoutComponentPortMark(p.Number, p.Name, p.Refdes, p.Gap!, p.Strip!))];
        });
    }

    /// <summary>brief-lcp-5 R-lcp5-7 — one mark per built 3D sheet: the gap at the sheet's centre in the layout's frame (the view
    /// places the layout at its origin, at the layout's DBU), across the sheet's width, pointing from the positive pad to the
    /// negative one, and the sheet's rectangle as what the marker measures and a hover covers.</summary>
    internal static IReadOnlyList<LayoutComponentPortMark> SheetMarks(C3dComponentPortBuild built, EmLayoutSource source)
    {
        var parts = EmPartPads.Project(source.View, source.AbsolutePath, source.Technology, C3dTransform.Identity).Parts
                              .GroupBy(p => p.Refdes, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var marks = new List<LayoutComponentPortMark>();
        foreach (var p in built.Ports)
        {
            if (p is not { Ok: true, Port: { } port } || !parts.TryGetValue(p.Refdes, out var part)) continue;
            var pos = part.Pads.FirstOrDefault(d => d.Pin == p.Pin);
            var neg = part.Pads.FirstOrDefault(d => d.Pin == p.ReferencePin);
            if (pos?.Layer is not { } layer || neg is null) continue;
            long x0 = port.Rect.Min.U, y0 = port.Rect.Min.V, x1 = x0 + port.Rect.Size.U, y1 = y0 + port.Rect.Size.V;
            var strip = new RectShape { Layer = layer, X1 = Math.Min(x0, x1), Y1 = Math.Min(y0, y1), X2 = Math.Max(x0, x1), Y2 = Math.Max(y0, y1) };
            long across = p.AlongX ? strip.Y2 - strip.Y1 : strip.X2 - strip.X1;
            var direction = p.AlongX ? (pos.Cx < neg.Cx ? LayoutRotation.R0 : LayoutRotation.R180)
                                     : (pos.Cy < neg.Cy ? LayoutRotation.R90 : LayoutRotation.R270);
            var gap = new LabelShape
            {
                Layer = layer, PortLayer = layer, X = (strip.X1 + strip.X2) / 2, Y = (strip.Y1 + strip.Y2) / 2, Text = port.Name,
                Height = Math.Max(1, across / 2), IsPort = true, PortKind = CircuitRF.Engine.Mom.PlanarPortKind.InternalDeltaGap, PortDirection = direction,
            };
            marks.Add(new LayoutComponentPortMark(port.Number, port.Name, p.Refdes, gap, strip));
        }
        return marks;
    }

    /// <summary>A built port's line in Messages. LCP-0 D5: the panel says "component port"; only explain and the docs name the
    /// construction, so this is not <see cref="EmPlanarComponentPorts.Describe"/>.</summary>
    internal static string Describe(EmPlanarComponentPort p)
    {
        static string Um(double m) => (m * 1e6).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        static string Pad(string? pin, string? net) => net is { Length: > 0 } n ? $"pad {pin} (net {n})" : $"pad {pin}";
        return $"{p.Name}: component port {p.Number}, {Pad(p.Pin, p.Nets.Positive)} to {Pad(p.ReferencePin, p.Nets.Negative)}, " +
               $"across {Um(p.GapM)} µm of air, {Um(p.WidthM)} µm wide; verified. It is added to the solved problem only, not to the layout.";
    }

    /// <summary>C2 before C10: digits compare as numbers.</summary>
    private static int NaturalCompare(string? a, string? b)
    {
        a ??= ""; b ??= "";
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                string na = a[si..i].TrimStart('0'), nb = b[sj..j].TrimStart('0');
                int c = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : string.CompareOrdinal(na, nb);
                if (c != 0) return c;
                continue;
            }
            int d = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
            if (d != 0) return d;
            i++; j++;
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}
