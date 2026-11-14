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
            // brief-icp-10 — the one builder, below the firewall, which `render --component-ports` calls too.
            var built = ComponentPortMarkBuilder.Build(copy, copied, ports, geometry);
            return new EmComponentPortMarkSet(built.Ports, built.Cuts, built.SolverGeometry);
        });
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
