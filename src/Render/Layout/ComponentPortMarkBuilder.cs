// brief-icp-10 — the marks a layout draws for a setup's component ports and cut instances, and on request the geometry the run
// hands its extractor: built here, below the firewall, so the EM panel's canvas and `circuitrf render --component-ports` draw
// from one builder (moved from src/Ui's EmComponentPortsHost, whose brief-lcp-4 / icp-7 notes below still hold).

using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;

namespace CircuitRF.Render;

/// <summary>brief-icp-7 — one marks build: the component ports (a footprint part's gap, a cut instance's face port), the cut
/// instances (R-icp7-3), and when asked the geometry the run hands its extractor (R-icp7-6).</summary>
public sealed record ComponentPortMarkSet(IReadOnlyList<LayoutComponentPortMark> Ports, IReadOnlyList<LayoutCutMark> Cuts,
                                          IReadOnlyList<LayoutShape>? SolverGeometry = null)
{
    public static readonly ComponentPortMarkSet Empty = new([], []);
}

public static class ComponentPortMarkBuilder
{
    /// <summary>
    /// brief-lcp-4 — the marks: <see cref="EmPlanarComponentPorts.Build"/>, the builder a run calls, so a mark sits where the run's
    /// port is. A 3D setup's are the sheets of the 3D view its run builds. <paramref name="copied"/> is the layout to build on (the
    /// canvas hands a serialized copy); <paramref name="ports"/> false builds no ports (a setup that cannot take them).
    /// </summary>
    public static ComponentPortMarkSet Build(EmSetup copy, EmLayoutSource copied, bool ports, bool geometry)
    {
        // brief-icp-7 R-icp7-6 — exactly what the run hands its extractor, through the one door every caller takes (and the
        // cached build it reads).
        var solver = geometry ? EmGeometry.ForSetup(copy, copied).Shapes : null;
        if (!ports) return new ComponentPortMarkSet([], [], solver);
        // brief-icp-3 — Of: a Planar setup's build, or a 3D setup's cut alone; both carry the cut and its face ports.
        var cutBuild = EmPlanarComponentPorts.Of(copy, copied);
        var marks = new List<LayoutComponentPortMark>();
        if (copy.Is3D)
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
        return new ComponentPortMarkSet(marks, CutMarks(cutBuild, copied), solver);
    }

    /// <summary>A cut face port's region: the face, and as deep again as its half-width on the routed conductor's side (the side
    /// its current flows toward), so the canvas finds the port's conductor there and not the instance the layout still draws.</summary>
    internal static RectShape FaceRegion(LabelShape gap)
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

}
