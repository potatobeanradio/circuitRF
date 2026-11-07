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
}

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
        // A thermal or electrostatic setup has no ports, and a 3D setup on a layout does not build them yet: the one rule
        // em, explain and check read.
        if (EmComponentPorts.Inapplicable(setup, onThreeDView: false) is { } inapplicable)
            return () => EmComponentPortCheck.Refused(inapplicable);
        if (source() is not { } s) return () => EmComponentPortCheck.Refused("Component ports: the layout has not resolved, so no port can be built.");
        string text = LayoutPersistence.Serialize(s.View);
        var copy = setup.Clone();
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
