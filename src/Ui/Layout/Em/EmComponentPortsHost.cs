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
    /// <summary>brief-icp-7 R-icp7-1 — every placed cell instance that is not a footprint part (devices and copper cells), each
    /// parent before its children; on a 3D view placing several layouts, under its layout instance.</summary>
    public IReadOnlyList<EmComponentInstanceItem> Instances { get; init; } = [];

    public static EmComponentPartListing Unread(string why) => new([], why, Read: false);
}

/// <summary>brief-icp-7 — one placed cell instance the tree offers: its path (as an entry names it), its cell, what it is, its
/// pins, and why no port can be built on it (or null).</summary>
public sealed record EmComponentInstanceItem(string Path, string Cell, string CellLabel, EmInstanceKind Kind, IReadOnlyList<string> Pins, string? Why)
{
    /// <summary>How deep it is: 0 at the top.</summary>
    public int Depth => Path.Count(c => c == '/');

    /// <summary>Its parent's path, or null at the top.</summary>
    public string? Parent => Path.LastIndexOf('/') is var at and > 0 ? Path[..at] : null;

    /// <summary>Every cell instance of <paramref name="set"/> that is not a footprint part, with <paramref name="prefix"/> on its path.</summary>
    internal static IEnumerable<EmComponentInstanceItem> Of(EmCellInstanceSet set, string prefix = "")
        => set.Instances.Where(i => i.Kind != EmInstanceKind.Footprint)
                        .Select(i => new EmComponentInstanceItem(prefix + i.Path, i.Cell, i.CellLabel, i.Kind, [.. i.Pins.Select(p => p.Name)], i.Why));
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
    /// <param name="geometry">brief-icp-7 R-icp7-6 — also return what the run hands its extractor, for the layout's solver-geometry
    /// preview.</param>
    EmComponentPortMarksJob? PrepareMarks(EmSetup setup, bool geometry = false);
}

/// <summary>brief-icp-7 — one marks build: the component ports (a footprint part's gap, a cut instance's face port), the cut
/// instances (R-icp7-3), and when asked the geometry the run hands its extractor (R-icp7-6).</summary>
public sealed record EmComponentPortMarkSet(IReadOnlyList<LayoutComponentPortMark> Ports, IReadOnlyList<LayoutCutMark> Cuts,
                                            IReadOnlyList<LayoutShape>? SolverGeometry = null)
{
    public static readonly EmComponentPortMarkSet Empty = new([], []);
}

/// <summary>brief-lcp-4 — one build of the marks a layout draws for a setup's component ports.</summary>
/// <param name="View">The layout the marks are about: the open editor's own model when it is open.</param>
/// <param name="EditCount">That layout's <see cref="LayoutView.EditCount"/> when it was copied: marks built from an older copy
/// are dropped.</param>
/// <param name="Build">Builds the ports on the copy, as a run would, and returns the built ones; refused parts are not drawn
/// (R-lcp4-5).</param>
public sealed record EmComponentPortMarksJob(LayoutView View, long EditCount, Func<EmComponentPortMarkSet> Build);

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
            // brief-icp-7 R-icp7-4 — the cell instances of each placed layout, under the layout instance when there are several
            // (U1/X1, as an entry names them on such a view).
            var layouts = e.Instances.Where(f => f.Layout && File.Exists(f.DocumentPath)).ToList();
            var instances = new List<EmComponentInstanceItem>();
            foreach (var frame in layouts)
            {
                try
                {
                    var view = LayoutPersistence.LoadFromFile(frame.DocumentPath);
                    instances.AddRange(EmComponentInstanceItem.Of(EmCellInstances.Project(view, frame.DocumentPath, null, C3dTransform.Identity),
                                                                  layouts.Count > 1 ? frame.Path + "/" : ""));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
            }
            return new(parts, parts.Count == 0 && instances.Count == 0 ? "No part with a reference designator is placed on this 3D view's layout." : null)
            {
                Instances = instances,
            };
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
    public EmComponentPortMarksJob? PrepareMarks(EmSetup setup, bool geometry = false) => null;
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
            // brief-icp-7 R-icp7-1 — and every cell instance, at any depth (ICP-1's projection, the one the cut reads).
            var instances = EmComponentInstanceItem.Of(EmCellInstances.Project(view, s.AbsolutePath, s.Technology, C3dTransform.Identity)).ToList();
            return new(parts, parts.Count == 0 && instances.Count == 0 ? "No part with a reference designator is placed on this layout." : null)
            {
                Instances = instances,
            };
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
    public EmComponentPortMarksJob? PrepareMarks(EmSetup setup, bool geometry = false)
    {
        if (source() is not { } s) return null;
        long at = s.View.EditCount;
        bool ports = setup.ComponentPorts.Count > 0 && EmComponentPorts.Inapplicable(setup) is null;
        if (!ports && !geometry) return new(s.View, at, () => EmComponentPortMarkSet.Empty);
        string text = LayoutPersistence.Serialize(s.View);
        var copy = setup.Clone();
        return new(s.View, at, () =>
        {
            var copied = s with { View = LayoutPersistence.Deserialize(text) };
            // brief-icp-7 R-icp7-6 — exactly what the run hands its extractor, through the one door every caller takes (and the
            // cached build it reads).
            var solver = geometry ? EmGeometry.ForSetup(copy, copied).Shapes : null;
            if (!ports) return new EmComponentPortMarkSet([], [], solver);
            // brief-icp-3 — Of: a Planar setup's build, or a 3D setup's cut alone; both carry the cut and its face ports.
            var cutBuild = EmPlanarComponentPorts.Of(copy, copied);
            var marks = new List<LayoutComponentPortMark>();
            if (setup.Is3D)
            {
                if (copied.Technology is { } tech && Em3dSetupProblem.Of(copy, copied, tech).ComponentPorts is { } built)
                    marks.AddRange(SheetMarks(built, copied));
            }
            else if (cutBuild is not null)
                marks.AddRange(cutBuild.Ports.Where(p => p.Ok && !p.Cutout).Select(p => new LayoutComponentPortMark(p.Number, p.Name, p.Refdes, p.Gap!, p.Strip!)));
            // brief-icp-7 R-icp7-3 — a cut instance's face port: the edge port at its face (a pin referenced to another pin is a gap
            // across a strip, drawn as a footprint part's is).
            if (cutBuild is not null)
                marks.AddRange(cutBuild.Ports.Where(p => p.Ok && p.Cutout).Select(p => new LayoutComponentPortMark(p.Number, p.Name, p.Refdes, p.Gap!,
                                                                                                                   p.Strip ?? FaceRegion(p.Gap!)) { Cut = p.Strip is null }));
            return new EmComponentPortMarkSet(marks, CutMarks(cutBuild, copied), solver);
        });
    }

    /// <summary>A cut face port's region: the face, and as deep again as its half-width on the routed conductor's side (the side
    /// its current flows toward), so the canvas finds the port's conductor there and not the instance the layout still draws.</summary>
    private static RectShape FaceRegion(LabelShape gap)
    {
        long h = Math.Max(1, gap.Height), d = 2 * h;
        var (x1, y1, x2, y2) = gap.PortDirection switch
        {
            LayoutRotation.R0 => (gap.X, gap.Y - h, gap.X + d, gap.Y + h),
            LayoutRotation.R180 => (gap.X - d, gap.Y - h, gap.X, gap.Y + h),
            LayoutRotation.R90 => (gap.X - h, gap.Y, gap.X + h, gap.Y + d),
            _ => (gap.X - h, gap.Y - d, gap.X + h, gap.Y),
        };
        return new RectShape { Layer = gap.PortLayer ?? gap.Layer, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };
    }

    /// <summary>
    /// brief-icp-7 R-icp7-3 — each cut instance as the layout draws it, read from the build: its left-out region
    /// (<see cref="EmCellCutoutResult.Regions"/>), each pin's class at its face (ICP-1's projection), and as each tooltip the line
    /// <c>explain --component-ports</c> prints for it (<see cref="EmCutReport.Lines"/>). An instance left out whole has no region
    /// and one cross at its box, its tooltip the reason.
    /// </summary>
    internal static IReadOnlyList<LayoutCutMark> CutMarks(EmPlanarComponentPortBuild? build, EmLayoutSource source)
    {
        if (build?.Cut is not { } cut) return [];
        double per = C3dLowering.Metres(1, source.DbuPerMicron > 0 ? source.DbuPerMicron : source.View.DbuPerMicron);
        long D(double m) => (long)Math.Round(m / per, MidpointRounding.AwayFromZero);
        var reportPorts = EmCutReport.PortsOf(build);
        string Layer(LayerKey k) => source.Technology?.Layers.FirstOrDefault(l => l.Key == k)?.Name is { Length: > 0 } n ? n : $"{k.Layer}/{k.Datatype}";
        var marks = new List<LayoutCutMark>();
        foreach (string path in cut.Pins.Select(p => p.Path).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var inst = build.Instances.At(path);
            var part = build.Ports.FirstOrDefault(p => p.Cutout && p.Refdes.Equals(path, StringComparison.OrdinalIgnoreCase) && p.CutPart is not null)?.CutPart;
            var lines = part is null ? null : EmCutReport.Lines(part, "", reportPorts, Layer);
            var pins = new List<LayoutCutPinMark>();
            if (cut.Pins.FirstOrDefault(p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.Pin.Length == 0 && p.Why is not null) is { } whole)
            {
                // Its own box, else the box of what it places (a cell of sub-cells draws nothing of its own).
                var under = build.Instances.Instances.Where(i => i.Path.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase) && i.Box is not null)
                                                     .Select(i => i.Box!.Value).ToList();
                (double X0, double Y0, double X1, double Y1)? box = inst?.Box ?? (under.Count == 0 ? null
                    : (under.Min(b => b.X0), under.Min(b => b.Y0), under.Max(b => b.X1), under.Max(b => b.Y1)));
                if (box is { } b) pins.Add(new LayoutCutPinMark(D((b.X0 + b.X1) / 2), D((b.Y0 + b.Y1) / 2), LayoutCutPinKind.LeftOut, whole.Why!));
                else if (inst?.Placement is { } placed
                         && CellHierarchy.InstanceBbox(placed, Path.GetDirectoryName(Path.GetFullPath(source.AbsolutePath)) ?? "") is { } pb && pb != Bbox.Empty)
                    pins.Add(new LayoutCutPinMark((pb.MinX + pb.MaxX) / 2, (pb.MinY + pb.MaxY) / 2, LayoutCutPinKind.LeftOut, whole.Why!));
                marks.Add(new LayoutCutMark(path, [], false, pins, whole.Why!));
                continue;
            }
            for (int k = 0; part is not null && k < part.Pins.Count; k++)
            {
                var pin = part.Pins[k];
                if (pin.Face is not { } f) continue;
                var kind = pin.Why is not null ? LayoutCutPinKind.LeftOut : pin.Class switch
                {
                    EmPinClass.Grounded => LayoutCutPinKind.Grounded,
                    EmPinClass.Abutting => LayoutCutPinKind.Abutting,
                    EmPinClass.Floating => LayoutCutPinKind.Floating,
                    EmPinClass.Return => LayoutCutPinKind.Return,
                    _ => LayoutCutPinKind.Routed,
                };
                long? tx = null, ty = null;
                if (kind == LayoutCutPinKind.Abutting && pin.Abuts is { } abuts && abuts.LastIndexOf('.') is var dot and > 0
                    && build.Instances.At(abuts[..dot])?.Pins.FirstOrDefault(q => q.Name.Equals(abuts[(dot + 1)..], StringComparison.OrdinalIgnoreCase)) is { } other)
                    (tx, ty) = (D(other.X), D(other.Y));
                pins.Add(new LayoutCutPinMark(D(f.X), D(f.Y), kind, lines?[k + 1].Text ?? $"{path}.{pin.Name}", tx, ty));
            }
            var region = cut.Regions.GetValueOrDefault(path);
            marks.Add(new LayoutCutMark(path, region?.Outline ?? [], region?.Box ?? false, pins, lines?[0].Text ?? $"{path} is cut out of the problem."));
        }
        return marks;
    }

    /// <summary>brief-lcp-5 R-lcp5-7 — one mark per built 3D sheet: the gap at the sheet's centre in the layout's frame (the view
    /// places the layout at its origin, at the layout's DBU), across the sheet's width, pointing at the positive pad as a Planar
    /// gap does, and the sheet's rectangle as what the marker measures and a hover covers.</summary>
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
            var direction = p.AlongX ? (pos.Cx < neg.Cx ? LayoutRotation.R180 : LayoutRotation.R0)
                                     : (pos.Cy < neg.Cy ? LayoutRotation.R270 : LayoutRotation.R90);
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
        // brief-icp-7 — a cut instance's port has no pads: it stands at the face where the instance is cut out.
        if (p.Cutout)
            return $"{p.Name}: component port {p.Number}, at the face where {p.Refdes}{(p.Cell is { } c ? $" ({c})" : "")} is cut out" +
                   (p.ReferencePin is { } rp ? $", referenced to pin {rp}" : "") + $", {Um(p.WidthM)} µm wide; verified. The instance is left out " +
                   "of the solved problem only, not the layout.";
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
