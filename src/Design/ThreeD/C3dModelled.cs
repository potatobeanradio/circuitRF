// brief-em3d-93 R-em3d93-2 — "Model": an object kept in the drawing and out of the solve.
//
// THE ELABORATION KEEPS IT; A SOLVE SEES THE FILTER. The editor builds its scene from the elaboration, and a not-modelled
// object must stay drawn, pickable and editable, so the elaborator lowers it as it lowers anything else and records its
// name in C3dElaboration.NotModelled (instance content by the instance's flag). Filter is the ONE place anything is taken
// out: the EM problem assembly (Palace and openEMS alike, air box included — C3dElaboration.Extent reads the filtered
// solids, so a not-modelled fixture far away does not grow the box) and the thermal run each call it, and nothing else
// filters. `em` and the thermal run from the CLI go through the same two callers.
//
// A REFERENCE TO A NOT-MODELLED OBJECT IS A REFUSAL, NEVER A SILENT DROP. A result that ran without something the user did
// not realise was missing is the outcome this feature must not create. Each refusal names both parties: a modelled wire on
// a pad that is not (here, in Filter); a modelled port on a conductor that is not (C3dPorts.Resolve, so the editor's port
// rows, check and the run all say it); a heat source, a probe, a contact resistance or a thermal boundary on an object that is
// not (C3dThermal's own validator, which check, the editor and the thermal run already share). A field plot's face on one is
// a warning: a plot is a reading and can simply show nothing.
//
// THE REFUSALS NOT-MODELLED CONTENT RAISED ITSELF DO NOT STOP A RUN. A lid that does not build, or an instance whose cell is
// missing, is exactly what a user turns off to run without; the elaborator records those (NotModelledRefusals) and Filter
// takes them out of the refusals a solve sees. The editor still shows them.
//
// PORTS ARE RENUMBERED, NOT GAPPED (owner decision D2b). A Touchstone file's ports are 1…N, so the modelled ports take 1…N
// in their Number order and the mapping is recorded in the notes, on each Em3dPort (SourceNumber) and so in the .sNp header
// and the .npy's diagnostics group. The document's numbers never change: turning P2 back on restores the four-port result.
// A port that is off is ABSENT — no excitation, no sheet, no lumped element (D2a): the gap is open, not terminated.

using CircuitRF.Diagnostics;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.ThreeD;

public static class C3dModelled
{
    /// <summary>What the tree's and the view's tooltips say of a not-modelled object.</summary>
    public const string Tip = "Not modelled: drawn and editable, left out of every simulation run";

    /// <summary>The suffix a not-modelled row's or object's hover carries.</summary>
    public const string Suffix = " (not modelled)";

    /// <summary>A driven run with every port off.</summary>
    public const string AllPortsOff = "Every port is turned off (Model): there is nothing to excite. Turn one on.";

    /// <summary>The document's own objects (not polylines, which are never modelled) and instances whose Model is off, by name.</summary>
    public static IReadOnlyList<string> OffNames(C3dDocument doc)
        => [.. doc.Objects.Where(o => o is not C3dPolyline && !o.Model).Select(o => o.Name),
            .. doc.Instances.Where(i => !i.Model).Select(i => i.Name)];

    /// <summary>Whether <paramref name="name"/> — an elaborated object, a wire or one element of a wire array (<c>w1[3]</c>) — is
    /// not modelled in <paramref name="e"/>. A drawn wire array's elements are named <c>w1</c>, <c>w1[1]</c>…, so a
    /// reference to the whole array is off when its elements are.</summary>
    public static bool IsOff(C3dElaboration e, string name)
    {
        if (e.NotModelled.Count == 0) return false;
        if (e.NotModelled.Contains(name)) return true;
        int bracket = name.IndexOf('[');
        return bracket > 0 && e.NotModelled.Contains(name[..bracket]);
    }

    /// <summary>The object half of a face spelled <c>object/face</c> (split at the last <c>/</c>, so <c>U1/die/zmax</c> is on
    /// <c>U1/die</c>).</summary>
    public static string ObjectOfFace(string spelled)
    {
        int slash = spelled.LastIndexOf('/');
        return slash <= 0 ? spelled : spelled[..slash];
    }

    /// <summary>A reference to a not-modelled object, naming both parties: <paramref name="who"/> is the referring record's
    /// clause (<c>Heat source 'q' is spread through</c>), <paramref name="remedy"/> the other way out.</summary>
    public static string Reference(string who, string target, string remedy)
        => $"{who} '{target}', which is not modelled (its Model is off, so no run has it). Turn '{target}' back on, or {remedy}.";

    /// <summary>
    /// The elaboration a SOLVE sees: every solid, sheet and wire that is not modelled taken out, with what refers to them
    /// (their nets, origins, material records, ground-band membership), and the refusals only they raised; a modelled wire
    /// on a pad that is not modelled added as a refusal; and a note naming what was left out, so a result carries its own
    /// record. The same elaboration back when nothing is off.
    /// </summary>
    public static C3dElaboration Filter(C3dDocument doc, C3dElaboration e)
    {
        if (e.NotModelled.Count == 0 && e.NotModelledRefusals.Count == 0) return e;
        var off = e.NotModelled;
        bool Keep(string name) => !off.Contains(name);

        var refusals = e.Refusals.ToList();
        foreach (string r in e.NotModelledRefusals) refusals.Remove(r);
        refusals.AddRange(WireRefusals(e));

        var notes = e.Notes.ToList();
        if (LeftOut(doc, e) is { } said) notes.Add(said);

        return e with
        {
            Solids = [.. e.Solids.Where(s => Keep(s.Name))],
            Sheets = [.. e.Sheets.Where(s => Keep(s.Name))],
            CylinderSheets = [.. e.CylinderSheets.Where(s => Keep(s.Name))],
            Refusals = refusals,
            Notes = notes,
            Wires = [.. e.Wires.Where(w => Keep(w.Name))],
            DrawnWires = e.DrawnWires.Where(kv => Keep(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            ObjectNets = e.ObjectNets.Where(kv => Keep(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            ViaNetConflicts = e.ViaNetConflicts.Where(kv => Keep(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            ViaNetAdoptions = e.ViaNetAdoptions.Where(kv => Keep(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            GroundBandObjects = [.. e.GroundBandObjects.Where(Keep)],
            Origins = e.Origins.Where(kv => Keep(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            SolidMaterials = e.SolidMaterials.Where(kv => Keep(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            NotModelled = new HashSet<string>(StringComparer.Ordinal),
            NotModelledRefusals = [],
        };
    }

    /// <summary>Each modelled wire (drawn, or a placed layout's) whose pad is not modelled, naming both.</summary>
    public static IReadOnlyList<string> WireRefusals(C3dElaboration e)
    {
        var list = new List<string>();
        if (e.NotModelled.Count == 0) return list;
        foreach (var w in e.Wires)
        {
            if (e.NotModelled.Contains(w.Name)) continue;
            foreach (string pad in new[] { w.Start.Pad, w.End.Pad }.Distinct(StringComparer.Ordinal))
                if (e.NotModelled.Contains(pad))
                    list.Add($"Wire '{w.Name}' is bonded to '{pad}', which is not modelled (its Model is off, so no run has it): a " +
                             $"wire needs its pads. Turn '{pad}' back on, or turn '{w.Name}' off too.");
        }
        return [.. list.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The note a run carries naming what it left out, or null when nothing is.</summary>
    public static string? LeftOut(C3dDocument doc, C3dElaboration e)
    {
        var names = OffNames(doc).ToList();
        // Content turned off inside a placed 3D view (its own objects' Model), under an instance that is itself modelled.
        int inner = e.NotModelled.Count(n => n.Contains('/') && !names.Any(top => n.StartsWith(top + "/", StringComparison.Ordinal) ||
                                                                                   n.StartsWith(top + "[", StringComparison.Ordinal)));
        if (names.Count == 0 && inner == 0) return null;
        string list = string.Join(", ", names.Select(n => $"'{n}'"));
        string parts = inner == 0 ? "" : $"{(names.Count > 0 ? ", and " : "")}{inner} part(s) inside placed cells that are turned off there";
        return $"Not modelled, and so left out of this run: {list}{parts}. Each is still drawn and editable; its Model switch puts it back.";
    }

    // ── ports ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A modelled port whose conductor is not modelled, naming both.</summary>
    public static string PortConductorRefusal(C3dPort port, string conductor, bool positive)
        => $"{C3dPorts.Label(port)} is modelled, but its {(positive ? "positive" : "negative")} conductor '{conductor}' is not (its Model " +
           $"is off, so no run has it): a port needs both its conductors. Turn '{conductor}' back on, or turn {C3dPorts.Label(port)} off too.";

    /// <summary>
    /// D2b — the modelled ports, renumbered 1…N in their document <see cref="C3dPort.Number"/> order, each carrying its document
    /// number and label; and the notes saying so. Nothing is renumbered when no port is off (and no note is written).
    /// </summary>
    public static IReadOnlyList<Em3dPort> Renumber(IReadOnlyList<(C3dPort Port, Em3dPort Resolved)> modelled, IReadOnlyList<C3dPort> off,
                                                  List<string> notes)
    {
        // brief-em3d-114 — by the RESULT port's document number, which is a terminal's own: each terminal is one port here.
        var ordered = modelled.OrderBy(m => m.Resolved.Number).ToList();
        // Every port carries the name it has in the 3D view (a component port's is its refdes), so the Touchstone file can
        // say which numbered port is which (Em3dPortMap.NameLines); a terminal keeps its own, set when it was made.
        if (off.Count == 0)
            return [.. ordered.Select(m => m.Resolved.FaceGroup is null && m.Resolved.SourceLabel is null
                                           ? m.Resolved with { SourceLabel = C3dPorts.Label(m.Port) } : m.Resolved)];
        var ports = new List<Em3dPort>(ordered.Count);
        for (int k = 0; k < ordered.Count; k++)
        {
            var (doc, r) = ordered[k];
            ports.Add(r with { Number = k + 1, Name = C3dPorts.ProblemName(k + 1), SourceNumber = r.Number,
                               SourceLabel = r.FaceGroup is null ? C3dPorts.Label(doc) : r.SourceLabel });
        }
        string map = string.Join(", ", ordered.Select((m, k) =>
            $"{(m.Resolved.FaceGroup is null ? C3dPorts.Label(m.Port) : $"{C3dPorts.Label(m.Port)} {m.Resolved.SourceLabel ?? m.Resolved.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)}")}→{k + 1}"));
        foreach (var p in off.OrderBy(p => C3dPorts.Numbers(p).DefaultIfEmpty(0).Min()))
            notes.Add($"{C3dPorts.Label(p)} not modelled: it is left out of the solve entirely — no excitation, no port sheet and no " +
                      "lumped element — so the gap it spans is whatever the geometry and the background are there: open, not " +
                      "shorted and not terminated in its Z0.");
        notes.Add($"The result's ports are the modelled ones, renumbered: {map}. The document's port numbers are unchanged.");
        int declared = ordered.Count + off.Sum(p => C3dPorts.Numbers(p).Count);
        notes.Add($"The result has {ordered.Count} port{(ordered.Count == 1 ? "" : "s")}; this 3D view declares {declared} — a " +
                  $"schematic placing it expects {declared}.");
        return ports;
    }

    // ── what check and explain say ────────────────────────────────────────────────────────────────

    /// <summary>
    /// R-em3d93-5 — what <c>check</c> reports of the switch beyond what the validators it already runs say: the objects and heat
    /// sources that are off (info; a port that is off, check's port rows say), each modelled wire on a pad that is not (error, Filter's own sentence), every port
    /// off under a driven setup (error, the assembly's), a field plot's face on a not-modelled object (warning), and each
    /// refusal the not-modelled content raised itself (a warning: no run refuses for it).
    /// </summary>
    public static IReadOnlyList<Diagnostic> Findings(C3dDocument doc, C3dElaboration e)
    {
        var found = new List<Diagnostic>();
        if (OffNames(doc) is { Count: > 0 } names)
            found.Add(Diagnostic.Create("c3d.model.off", DiagnosticSeverity.Info,
                "{count} object(s) are not modelled: {names}. Each is drawn and left out of every simulation run.",
                ("count", names.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("names", string.Join(", ", names))));
        // (a port that is off is said by check's own port rows, C3dPortReport.Text)
        if (doc.HeatSources.Where(h => !h.Model).Select(h => h.Name).ToList() is { Count: > 0 } sources)
            found.Add(Diagnostic.Create("c3d.model.source-off", DiagnosticSeverity.Info,
                "Heat source(s) not modelled: {sources}. No thermal run puts power in them.", ("sources", string.Join(", ", sources))));
        foreach (string why in WireRefusals(e))
            found.Add(Diagnostic.Create("c3d.model.wire-pad", DiagnosticSeverity.Error, "{why}", ("why", why)));
        if (doc.Ports.Count > 0 && doc.Ports.All(p => !p.Model) &&
            C3dSetups.Read(doc).Any(s => s.Setup is { Is3D: true, IsThermal: false, Problem3D: Em3dProblemType.Driven }))
            found.Add(Diagnostic.Create("c3d.model.all-ports-off", DiagnosticSeverity.Error, "{why}", ("why", AllPortsOff)));
        foreach (string why in FieldPlotWarnings(doc, e))
            found.Add(Diagnostic.Create("c3d.model.field-plot", DiagnosticSeverity.Warning, "{why}", ("why", why)));
        foreach (string r in e.NotModelledRefusals.Distinct(StringComparer.Ordinal))
            found.Add(Diagnostic.Create("c3d.model.refused-off", DiagnosticSeverity.Warning,
                "{why} It is not modelled, so no run refuses for it.", ("why", r)));
        return found;
    }

    /// <summary>A field plot's face on a not-modelled object: it will show nothing, and says so.</summary>
    public static IReadOnlyList<string> FieldPlotWarnings(C3dDocument doc, C3dElaboration e)
    {
        var list = new List<string>();
        int modelled = doc.Ports.Count(p => p.Model);
        foreach (var plot in doc.FieldPlots)
        {
            // A plot's excited port is the RESULT's number, and the result's ports are the modelled ones renumbered 1…N.
            if (modelled < doc.Ports.Count && plot.Solution?.Port is { } excited && excited > modelled)
                list.Add($"Field plot '{plot.Name}' shows the excitation of port {excited}, and with {doc.Ports.Count - modelled} port(s) " +
                         $"turned off (Model) a run has only {modelled}: no run excites it, so the plot shows nothing.");
            foreach (var f in plot.Faces)
                if (IsOff(e, f.Parts.Object))
                    list.Add($"Field plot '{plot.Name}' paints the face '{f.Face}', which is on '{f.Parts.Object}', not modelled: no run " +
                             "solves a field there, so the plot shows nothing on it.");
        }
        return list;
    }
}
