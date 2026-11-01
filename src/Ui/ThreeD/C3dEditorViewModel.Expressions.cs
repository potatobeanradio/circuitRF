// brief-em3d-51 — dimensions as expressions in the 3D editor: the live document's resolution, name edits as undo entries,
// and the DRAG RULE (R-em3d51-6).
//
// THE LIVE DOCUMENT IS ALWAYS RESOLVED. Every change (an entry applied forward or back, a gesture step) re-resolves it, so
// every number a tool, the kernel or a panel reads is the value of its expression. The scene resolves again from the
// document's text — the same resolver — so what is drawn and what is edited agree.
//
// THE DRAG RULE IS APPLIED WHERE EVERY EDIT PASSES: Push. An entry that replaces objects or instances is compared, bound
// field by bound field, with what it replaces: an expression a tool dropped (a fresh object from the kernel) is put back;
// a bound component whose number the edit changed is solved for its name (C3dVariableEdits.Solve), and the entry becomes
// a document edit that writes the name — the VAR, or the cell parameter's .ccell default — so every other object using
// the name follows. A component that cannot be solved refuses the edit, and the document is put back byte for byte.
//
// PREVIEWS FOLLOW THE SAME RULE (the counter gate: objects previewed = objects whose resolved fields change). A face or
// vertex gesture's preview is a substituted object (brief 47); here the names it would write are substituted too, so
// the scene shows every object the release will move. Move and Rotate preview by per-draw transforms of the selection
// only, so they are refused at the grab when the placement's names reach any other object — nothing may move on commit
// that did not move in the preview.

using System.Collections.ObjectModel;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.ThreeD.Operations;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel
{
    private C3dResolution? _resolution;

    /// <summary>The live document's names and fields, resolved.</summary>
    public C3dResolution Resolution => _resolution ??= C3dResolver.Resolve(Document, Cell);

    /// <summary>The active frame's cell: its <c>.ccell</c> parameters, or none for a loose 3D view.</summary>
    public C3dCell Cell => C3dCell.Of(FilePath);

    /// <summary>Resolves the live document in place. Called on every change, before anything reads a number.</summary>
    private void ResolveDocument()
    {
        _resolution = C3dResolver.Resolve(Document, Cell);
        HoldWires();                                     // brief-em3d-135 — a held loop height or span, from the new numbers
        if (ShowVariables) Variables?.Reload();          // a hidden panel is brought up to date when it is shown
        // brief-em3d-76 R-em3d76-4c — the viewer mirrors a shown temperature across the document's symmetry planes
        double m = C3dLowering.Metres(1, Document.DbuPerMicron);
        Viewer.SetSymmetryPlanes([.. Document.SymmetryPlanes.Select(p => ((int)p.Axis, p.At * m))]);
    }

    partial void OnShowVariablesChanged(bool value)
    {
        if (value) Variables?.Reload();
    }

    /// <summary>The Variables panel (R-em3d51-4a).</summary>
    public C3dVariablesViewModel? Variables { get; private set; }

    [ObservableProperty] private bool _showVariables;

    // ── undo: document edits and groups ───────────────────────────────────────────────────────

    private List<IUiCommand>? _group;
    private string? _groupDescription;

    /// <summary>Starts collecting pushed entries into one (a Define strip and the object it completes).</summary>
    private void BeginGroup(string description)
    {
        _group ??= [];
        _groupDescription ??= description;
    }

    /// <summary>Ends the group: what was pushed since <see cref="BeginGroup"/> is one entry.</summary>
    private void EndGroup()
    {
        if (_group is not { } parts) return;
        string description = _groupDescription ?? "Edit";
        _group = null;
        _groupDescription = null;
        if (parts.Count == 0) return;
        // Each part was applied as it was pushed; the group's first Execute is therefore nothing.
        UndoRedo.Execute(new C3dGroupEdit(parts.Count == 1 ? parts[0].Description : description, parts));
        UndoEntries++;
    }

    /// <summary>The document's names, fields, or its cell's parameters, edited as ONE entry: <paramref name="mutate"/> changes
    /// a copy of the document and of the <c>.ccell</c> (null when the view is in no cell) and returns a refusal or null.</summary>
    public string? EditNames(string description, Func<C3dDocument, CcellFile?, string?> mutate)
    {
        var copy = C3dPersistence.Deserialize(C3dPersistence.Serialize(Document));
        string? ccellPath = Cell.CcellPath;
        string? ccellBefore = ccellPath is not null && File.Exists(ccellPath) ? File.ReadAllText(ccellPath) : null;
        var ccell = ccellBefore is not null ? CellPersistence.Deserialize(ccellBefore) : null;
        if (mutate(copy, ccell) is { } refusal) return refusal;
        string before = C3dPersistence.Serialize(Document);
        string after = C3dPersistence.Serialize(copy);
        string? ccellAfter = ccell is not null ? CellPersistence.Serialize(ccell) : null;
        // The .ccell is part of the entry only when its content changed — not when merely re-spelled.
        if (ccellBefore is not null && ccellAfter == CellPersistence.Serialize(CellPersistence.Deserialize(ccellBefore)))
            (ccellPath, ccellBefore, ccellAfter) = (null, null, null);
        if (after == before && ccellAfter == ccellBefore) return null;
        Push(new C3dDocumentEdit(description, before, after, ccellPath, ccellBefore, ccellAfter, ApplyDocumentText));
        return null;
    }

    /// <summary>A document edit forward or back: the document's lists from <paramref name="text"/>, and the <c>.ccell</c>.</summary>
    private void ApplyDocumentText(string text, string? ccellPath, string? ccellText)
    {
        DocumentWrites++;
        if (ccellPath is not null && ccellText is not null && (!File.Exists(ccellPath) || File.ReadAllText(ccellPath) != ccellText))
            AtomicFile.WriteAllText(ccellPath, ccellText);
        var d = C3dPersistence.Deserialize(text);
        Document.TechRef = d.TechRef;
        Document.Objects = d.Objects;
        Document.Instances = d.Instances;
        Document.Variables = d.Variables;
        Document.Ports = d.Ports;
        Document.FaceBoundaries = d.FaceBoundaries;
        // brief-em3d-75 — a thermal place's field is written through this entry too, and a rename reaches its expressions.
        Document.HeatSources = d.HeatSources;
        Document.Probes = d.Probes;
        Document.MeshRegions = d.MeshRegions;
        Document.ContactResistances = d.ContactResistances;
        Document.EffectiveBlocks = d.EffectiveBlocks;
        Document.SymmetryPlanes = d.SymmetryPlanes;
        Document.LumpedElements = d.LumpedElements;            // brief-sar-3
        // a VAR rename rewrites the thermal setups' expressions in this same entry, so undoing it restores them
        Document.Setups = d.Setups;
        DocumentChanged();
        RebuildThermalTree();
    }

    // ── Properties (R-em3d51-4b) ──────────────────────────────────────────────────────────────

    /// <summary>The object's named dimensions for the Properties panel: expression and resolved value, or the number.</summary>
    public IEnumerable<C3dDimensionField> DimensionFields(C3dObject obj) => DimensionFields(obj, obj.Name, path => C3dPropertiesViewModel.FieldLabel(obj, path),
                                                                                             path => C3dPropertiesViewModel.FieldGroup(obj, path));

    /// <summary>brief-em3d-75 R-em3d75-1c — any item's named dimensions (an object's, or a thermal place's), labelled by
    /// <paramref name="label"/> and grouped by <paramref name="group"/>.</summary>
    public IEnumerable<C3dDimensionField> DimensionFields(object item, string name, Func<string, string> label, Func<string, (string, string)> group)
    {
        var res = Resolution;
        var obj = item as C3dObject;
        foreach (var (prefix, owner) in C3dBindings.OwnersOf(item))
            foreach (var (spec, k, path) in C3dBindings.FieldsOf(owner, prefix))
            {
                // 3D editor round 3 — a wire has no placement (every operation bakes it into the points), so its placement
                // fields are not offered; its diameter is offered even when unstated, at the default it is built with.
                if (obj is C3dWire && prefix.StartsWith("Placement.", StringComparison.Ordinal)) continue;
                // brief-em3d-132 — a point list's components are bindable, but their rows are the wire's and the polyline's
                // own point rows (briefs 133 and 134), not this list.
                if (spec.Element is not null) continue;
                // brief-em3d-135 — a wire's held loop height and span are its Loop height and Span fields, not rows here.
                if (spec.Owner == typeof(C3dWire) && spec.Property is nameof(C3dWire.LoopHeight) or nameof(C3dWire.Span)) continue;
                bool wireDefault = obj is C3dWire { DiameterUm: null } && path == nameof(C3dWire.DiameterUm);
                if (!wireDefault && C3dBindings.GetNumber(owner, spec, k) is not { } n && C3dBindings.GetExpr(owner, spec, k) is null) continue;
                var (grp, axis) = group(path);
                yield return Field(res, name, owner, spec, k, path, label(path), grp, axis, wireDefault);
            }
        // 3D editor round 4 — a wire with no array is a row of one: its count is offered, and typing more makes the row.
        if (obj is C3dWire { Array: null })
        {
            const string countPath = "Array." + nameof(C3dWireArray.Count);
            yield return new C3dDimensionField
            {
                Path = countPath, Label = label(countPath), Text = "1", Loaded = "1",
                ValueText = "One wire. More makes a row, side by side across its run (its pitch then shows here).",
            };
        }
    }

    /// <summary>One dimension field: its text (the expression with its site unit, or the number in the display unit), what
    /// the expression resolves to, and the resolver's error for it.</summary>
    private C3dDimensionField Field(C3dResolution res, string name, IC3dBindable owner, C3dFieldSpec spec, int k, string path,
                                    string label, string group, string axis, bool wireDefault = false)
    {
        var unit = Document.DisplayUnit;
        var e = C3dBindings.GetExpr(owner, spec, k);
        string text;
        string value = "";
        if (e is not null)
        {
            text = e.Expr + (SiteSuffix(spec.Kind, e.Unit) is { } s ? " " + s : "");
            if (res.FieldValues.TryGetValue((name, path), out double si))
                value = "= " + spec.Kind switch
                {
                    // 3D editor round 4 — a µm field (a wire's diameter, a sheet's thickness) is a length like any
                    // other on screen: the file keeps µm, the Inspector speaks the display unit.
                    C3dFieldKind.Length or C3dFieldKind.Microns => C3dUnits.Spell(si, unit),
                    C3dFieldKind.Angle => (si * 180 / Math.PI).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) + "°",
                    _ => si.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
                };
        }
        else text = SpellNumber(spec.Kind, wireDefault ? C3dWires.DefaultDiameterUm : C3dBindings.GetNumber(owner, spec, k) ?? 0);
        if (e is null && wireDefault) value = "The default (1 mil)";
        string? error = res.FieldErrors.FirstOrDefault(x => x.Item == name && x.Path == path)?.Message;
        return new C3dDimensionField
        {
            Path = path, Label = label, Group = group, Axis = axis, Kind = spec.Kind,
            ValueText = value, Error = error,
            IsExpression = e is not null, Text = text, Loaded = text,
        };
    }

    /// <summary>brief-em3d-133 R-em3d133-1 — point <paramref name="k"/> of a wire as three dimension fields (x, y, z): a number
    /// or an expression each, read and refused exactly as every other dimension is.</summary>
    public IReadOnlyList<C3dDimensionField> WirePointFields(C3dWire w, int k)
    {
        var spec = C3dBindings.SpecOf(typeof(C3dWire), nameof(C3dWire.Points))!.ElementAt(k);
        var res = Resolution;
        return [.. new[] { "x", "y", "z" }.Select((axis, c) =>
        {
            string path = $"{nameof(C3dWire.Points)}[{k}][{c}]";
            return Field(res, w.Name, w, spec, c, path, C3dBindings.Label(spec, c, path), $"point {k + 1}", axis);
        })];
    }

    /// <summary>
    /// The owner's rule for the Inspector (2026-10-09): a typed dimension that names something nothing defines — a mistyped
    /// VAR, most often — is REFUSED, and the field keeps the value it had. Binding it as typed and showing it red left the
    /// document refused until the name was found. Null when it is a number, does not parse (the commit says so in its own
    /// words), or every name it uses is defined.
    /// </summary>
    public string? UndefinedNamesIn(string text)
    {
        string t = text.Trim();
        if (t.Length == 0 || LayoutUnits.TryParse(t, Document.DisplayUnit, Document.DbuPerMicron, out _)) return null;
        var (expr, _) = SplitUnit(t, Document.DisplayUnit);
        CircuitRF.Core.Expressions.Expr ast;
        try { ast = CircuitRF.Core.Expressions.Parser.Parse(expr); }
        catch (CircuitRF.Core.Expressions.ExpressionException) { return null; }
        var missing = CircuitRF.Core.Expressions.AstWalker.CollectRefs(ast).Where(n => !Resolution.IsDefined(n)).Order(StringComparer.Ordinal).ToList();
        if (missing.Count == 0) return null;
        return $"{string.Join(", ", missing.Select(n => $"'{n}'"))} {(missing.Count == 1 ? "is" : "are")} not defined in this 3D view, " +
               "so the field keeps its value. Check the spelling, or add it in Variables first.";
    }

    /// <summary>brief-em3d-135 — a wire's held loop height or span as a dimension field (its expression, what it resolves to,
    /// the resolver's error); null when it is not held.</summary>
    public C3dDimensionField? WireHeldField(C3dWire w, string property)
    {
        var spec = C3dBindings.SpecOf(typeof(C3dWire), property)!;
        if (C3dBindings.GetNumber(w, spec, 0) is null && C3dBindings.GetExpr(w, spec, 0) is null) return null;
        return Field(Resolution, w.Name, w, spec, 0, property, property, property, "");
    }

    /// <summary>3D editor round 4 — a new wire row's pitch: across the wire's run in plan, four diameters apart.</summary>
    internal C3dPoint3 DefaultWirePitch(C3dWire w)
    {
        long pitch = Math.Max(1, (long)Math.Round(4 * (w.DiameterUm ?? C3dWires.DefaultDiameterUm) * Document.DbuPerMicron));
        if (w.Points.Count < 2) return new C3dPoint3(0, pitch, 0);
        var (s, e) = (w.Points[0], w.Points[^1]);
        return Math.Abs(e.X - s.X) >= Math.Abs(e.Y - s.Y) ? new C3dPoint3(0, pitch, 0) : new C3dPoint3(pitch, 0, 0);
    }

    /// <summary>The unit a field's text is written with when it is not the one a bare number would be read in.</summary>
    private string? SiteSuffix(C3dFieldKind kind, string? stored)
    {
        string? engine = C3dUnits.Engine(stored, out _);
        return kind switch
        {
            C3dFieldKind.Length or C3dFieldKind.Microns => engine is null || engine == LayoutUnits.AsciiSuffix(Document.DisplayUnit) ? null : engine,
            _ => null,
        };
    }

    private string SpellNumber(C3dFieldKind kind, double n) => kind switch
    {
        C3dFieldKind.Length => Tools.C3dDimension.Spell((long)n, Document.DisplayUnit, Document.DbuPerMicron),
        C3dFieldKind.Microns => (n * 1e-6 / MetresPerDisplayUnit).ToString("0.##########", System.Globalization.CultureInfo.InvariantCulture),
        C3dFieldKind.Count => ((long)n).ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => n.ToString("G", System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>One display unit in metres.</summary>
    private double MetresPerDisplayUnit => CircuitRF.Core.Expressions.Units.Scale(LayoutUnits.AsciiSuffix(Document.DisplayUnit)) ?? 1e-6;

    /// <summary>
    /// Properties' commit of one dimension (R-em3d51-4b): a number is written as the number (the expression it held, if
    /// any, is replaced); anything else is bound as an expression at its site unit — a trailing unit, else the display unit
    /// (degrees for an angle). A thickness or diameter is stored in µm but typed and read in the display unit like any
    /// length (3D editor round 4). One entry. The refusal, or null.
    /// </summary>
    public string? SetFieldText(int index, string path, string text)
    {
        // brief-em3d-66 — an operand's field is its top-level object's, named under the operand's path (Blank.Size[0]).
        if (IsOperandIndex(index))
        {
            int top = TopOf(index, out string operand);
            return top < 0 ? null : SetFieldText(top, operand + path, text);
        }
        string name = Document.Objects[index].Name;
        text = text.Trim();
        if (text.Length == 0) return "Type a number or an expression.";
        return EditNames($"Set {name} {path} = {text}", (doc, _) =>
        {
            var obj = doc.Objects[index];
            // 3D editor round 4 — a wire's count typed with no array yet makes the row, at the default pitch.
            if (obj is C3dWire { Array: null } nw && path.StartsWith("Array.", StringComparison.Ordinal))
                nw.Array = new C3dWireArray { Pitch = DefaultWirePitch(nw) };
            return WriteField(doc, obj, name, path, text, C3dPropertiesViewModel.FieldLabel(obj, path));
        });
    }

    /// <summary>
    /// The one write of a typed dimension into <paramref name="item"/> of <paramref name="doc"/> (a copy the caller commits):
    /// an object's (SetFieldText), or — brief-em3d-75 R-em3d75-1c — a heat source's, a probe's or a mesh region's, whose
    /// dimensions brief 73 made bindable exactly as an object's are. The refusal, or null.
    /// </summary>
    private string? WriteField(C3dDocument doc, object item, string name, string path, string text, string label)
    {
        {
            if (C3dBindings.Find(item, path) is not { } f) return $"'{name}' has no {path}.";
            C3dResolver.Resolve(doc, Cell);
            switch (f.Spec.Kind)
            {
                case C3dFieldKind.Length:
                    if (LayoutUnits.TryParse(text, Document.DisplayUnit, Document.DbuPerMicron, out long dbu))
                    {
                        if (dbu <= 0 && MustBePositive(path)) return $"{label} is a positive length.";
                        C3dBindings.SetExpr(f.Owner, f.Spec, f.Component, null); C3dBindings.SetNumber(f.Owner, f.Spec, f.Component, dbu); return null;
                    }
                    var (expr, unit) = SplitUnit(text, Document.DisplayUnit);
                    return Bind(f, expr, C3dUnits.Stored(unit));
                case C3dFieldKind.Microns:
                    // A bare number is in the display unit; a length with a unit written against it ("1mil") is that
                    // length. Either is stored in µm.
                    bool isUm = NumericText.TryParseDouble(text, out double um);
                    if (isUm) um = Math.Round(um * MetresPerDisplayUnit * 1e6, 9);
                    else if (LayoutUnits.TryParse(text, Document.DisplayUnit, Document.DbuPerMicron, out long umDbu))
                        (isUm, um) = (true, (double)LayoutUnits.FromDbu(umDbu, LayoutUnit.Um, Document.DbuPerMicron));
                    if (isUm)
                    {
                        if (um <= 0 || !double.IsFinite(um)) return $"{label} is a positive length.";
                        C3dBindings.SetExpr(f.Owner, f.Spec, f.Component, null); C3dBindings.SetNumber(f.Owner, f.Spec, f.Component, um); return null;
                    }
                    var (e2, u2) = SplitUnit(text, Document.DisplayUnit);
                    return Bind(f, e2, C3dUnits.Stored(u2));
                case C3dFieldKind.Angle:
                    if (NumericText.TryParseDouble(text.TrimEnd('°'), out double deg))
                    { C3dBindings.SetExpr(f.Owner, f.Spec, f.Component, null); C3dBindings.SetNumber(f.Owner, f.Spec, f.Component, deg); return null; }
                    return Bind(f, text, C3dUnits.Degrees);
                default:
                    if (int.TryParse(text, out int count))
                    {
                        if (count < 1) return "An array count is at least 1.";
                        C3dBindings.SetExpr(f.Owner, f.Spec, f.Component, null); C3dBindings.SetNumber(f.Owner, f.Spec, f.Component, count); return null;
                    }
                    return Bind(f, text, null);
            }
        }

        // A box's or a rectangle's size, a radius: what a zero or negative number would make is no solid at all. A heat
        // sheet's rectangle, a spot's diameter: no place at all.
        static bool MustBePositive(string path)
            => path is "Size[0]" or "Size[1]" or "Size[2]" or "Rect.Size[0]" or "Rect.Size[1]" or "Radius"
                    or "Sheet.Rect.Size[0]" or "Sheet.Rect.Size[1]" or "Spot.Diameter";

        static string? Bind((IC3dBindable Owner, C3dFieldSpec Spec, int Component) f, string expr, string? unit)
        {
            if (!Parses(expr)) return $"'{expr}' is neither a number nor an expression the engine can read.";
            C3dBindings.SetExpr(f.Owner, f.Spec, f.Component, new C3dExpr(expr, unit));
            return null;
        }
    }

    // ── the drag rule at the commit ───────────────────────────────────────────────────────────

    /// <summary>What an edit that changes bound fields writes, or why it cannot.</summary>
    private sealed record NamePlan(IReadOnlyList<C3dWrite> Writes, string? Refusal, IReadOnlyList<(string Item, string Path)> Unsolvable);

    /// <summary>
    /// <paramref name="after"/> compared with <paramref name="before"/> (an object or an instance named <paramref name="item"/>):
    /// every expression <paramref name="before"/> holds is put back on <paramref name="after"/> where the tool dropped it, and
    /// every bound component whose number changed is solved for the name it writes.
    /// </summary>
    private NamePlan PlanNames(string item, object before, object after, C3dResolution res, C3dCell cell)
    {
        var writes = new List<C3dWrite>();
        var unsolvable = new List<(string, string)>();
        foreach (var f in C3dBindings.BoundOf(item, before).ToList())
        {
            if (after is C3dWire && f.Spec.Element is not null) continue;      // brief-em3d-133: WirePointPlan
            // brief-em3d-135 — a held loop height or span is set and let go only by the Inspector's own edit, which states it:
            // one an edit leaves out was let go, and putting it back (or solving its name) would undo exactly that.
            if (after is C3dWire && f.Spec.Property is nameof(C3dWire.LoopHeight) or nameof(C3dWire.Span)) continue;
            if (C3dBindings.Find(after, f.Path) is not { } at)
                return new NamePlan([], $"'{item}' {f.Path} holds {f.Expr.Expr}, and this edit would leave it no such field " +
                                        $"(it becomes a {C3dObject.KindOf(after.GetType()).ToLowerInvariant()}). Replace it with a number first.",
                                    [(item, f.Path)]);
            if (C3dBindings.GetExpr(at.Owner, at.Spec, at.Component) is null) C3dBindings.SetExpr(at.Owner, at.Spec, at.Component, f.Expr);
            double was = C3dBindings.GetNumber(f.Owner, f.Spec, f.Component) ?? 0;
            double now = C3dBindings.GetNumber(at.Owner, at.Spec, at.Component) ?? 0;
            if (Math.Abs(now - was) < 1e-9) continue;
            double target = f.Spec.Kind switch
            {
                C3dFieldKind.Length => now / (1e6 * Document.DbuPerMicron),
                C3dFieldKind.Angle => now * Math.PI / 180,
                C3dFieldKind.Microns => now * 1e-6,
                _ => now,
            };
            string site = C3dUnits.Engine(f.Expr.Unit, out _) ?? LayoutUnits.AsciiSuffix(Document.DisplayUnit);
            var w = C3dVariableEdits.Solve(res, cell, f.Expr, f.Spec.Kind, target, site, out string? why, item, f.Path);
            if (w is null) { unsolvable.Add((item, f.Path)); return new NamePlan([], why, unsolvable); }
            if (writes.FirstOrDefault(x => x.Name == w.Name) is { } other && other.Expression != w.Expression)
                return new NamePlan([], $"This edit would give '{w.Name}' two values ({other.Expression} and {w.Expression}): " +
                                        $"'{item}' uses it in more than one field that moved differently.", [(item, f.Path)]);
            if (!writes.Any(x => x.Name == w.Name)) writes.Add(w);
        }
        if (after is C3dWire wire && WirePointPlan(item, wire, res, cell, writes, unsolvable) is { } refused) return refused;
        return new NamePlan(writes, null, unsolvable);
    }

    /// <summary>
    /// brief-em3d-133 — a wire's points are not compared path by path with the points they replace: an insert or a remove
    /// renumbers them, a translation rewrites them (overview D2, D4) and a typed number unbinds one, so the expressions the
    /// edit leaves are what it states, and none is put back. A bound component the edit moved WITHOUT rewriting — its number
    /// is no longer its expression's value, as when a seat moved an end's z — takes the drag rule in its strict form
    /// (overview R-em3d131-3): a bare name is written; any other expression refuses the edit, naming the field.
    /// </summary>
    private NamePlan? WirePointPlan(string item, C3dWire after, C3dResolution res, C3dCell cell, List<C3dWrite> writes,
                                    List<(string, string)> unsolvable)
    {
        double dbuPerMetre = 1e6 * Document.DbuPerMicron;
        foreach (var f in C3dBindings.BoundOf(item, after).Where(f => f.Spec.Element is not null).ToList())
        {
            double now = C3dBindings.GetNumber(f.Owner, f.Spec, f.Component) ?? 0;
            double value;
            try { value = C3dVariableEdits.Evaluate(res, f.Expr, f.Spec.Kind) * dbuPerMetre; }
            catch (Exception) { continue; }        // an expression that does not evaluate is the resolver's to report, red, by name
            if (Math.Abs(value - now) <= 0.5) continue;
            string field = $"'{item}' {C3dBindings.Label(f.Spec, f.Component, f.Path)} holds {f.Expr.Expr}";
            bool bare;
            try { bare = CircuitRF.Core.Expressions.Parser.Parse(f.Expr.Expr) is CircuitRF.Core.Expressions.RefExpr; }
            catch (CircuitRF.Core.Expressions.ExpressionException) { bare = false; }
            if (!bare)
            {
                unsolvable.Add((item, f.Path));
                return new NamePlan([], $"{field}: this edit would move it, and only an expression that is one name can be rewritten " +
                                        "to follow. Replace it with a number, or edit the expression.", unsolvable);
            }
            string site = C3dUnits.Engine(f.Expr.Unit, out _) ?? LayoutUnits.AsciiSuffix(Document.DisplayUnit);
            var w = C3dVariableEdits.Solve(res, cell, f.Expr, f.Spec.Kind, now / dbuPerMetre, site, out string? why, item, f.Path);
            if (w is null) { unsolvable.Add((item, f.Path)); return new NamePlan([], why, unsolvable); }
            if (writes.FirstOrDefault(x => x.Name == w.Name) is { } other && other.Expression != w.Expression)
                return new NamePlan([], $"This edit would give '{w.Name}' two values ({other.Expression} and {w.Expression}): " +
                                        $"'{item}' uses it in more than one field that moved differently.", [(item, f.Path)]);
            if (!writes.Any(x => x.Name == w.Name)) writes.Add(w);
        }
        return null;
    }

    /// <summary>
    /// Push's filter: an entry replacing objects or instances whose bound fields it changed becomes one that writes the
    /// names they hold. Null when the edit is refused — the status line says why, the document is put back, and a
    /// one-click Replace with Number is offered for the field.
    /// </summary>
    private IUiCommand? ThroughNames(C3dEdit edit)
    {
        if (!edit.Slots.All(s => s.Before is not null && s.After is not null)) return edit;
        bool anyBound = false;
        foreach (var s in edit.Slots)
            if (s.Before!.Contains("\"Expr\"", StringComparison.Ordinal)) { anyBound = true; break; }
        if (!anyBound) return edit;

        var res = Resolution;
        var cell = Cell;
        var slots = new List<C3dEditSlot>();
        var writes = new List<C3dWrite>();
        foreach (var s in edit.Slots)
        {
            object before = s.Instance ? C3dPersistence.DeserializeInstance(s.Before!) : C3dPersistence.DeserializeObject(s.Before!);
            object after = s.Instance ? C3dPersistence.DeserializeInstance(s.After!) : C3dPersistence.DeserializeObject(s.After!);
            string item = before is C3dInstance bi ? bi.Name : ((C3dObject)before).Name;
            var plan = PlanNames(item, before, after, res, cell);
            if (plan.Refusal is { } why) return Refuse(edit, why, plan.Unsolvable);
            foreach (var w in plan.Writes)
            {
                if (writes.FirstOrDefault(x => x.Name == w.Name) is { } other && other.Expression != w.Expression)
                    return Refuse(edit, $"This edit would give '{w.Name}' two values ({other.Expression} and {w.Expression}).", []);
                if (!writes.Any(x => x.Name == w.Name)) writes.Add(w);
            }
            string afterText = after is C3dInstance ai ? C3dPersistence.SerializeInstance(ai) : C3dPersistence.SerializeObject((C3dObject)after);
            slots.Add(s with { After = afterText });
        }
        var fixedEdit = new C3dEdit(edit.Description, slots, ApplySlots, faceBoundaries: edit.FaceBoundaries, setBoundaries: SetBoundaries);
        if (writes.Count == 0 && slots.SequenceEqual(edit.Slots)) return edit;

        // The document before the edit (the edit may already be applied: a gesture) and after it, with the names written.
        var beforeDoc = C3dPersistence.Deserialize(C3dPersistence.Serialize(Document));
        if (edit.AlreadyApplied) C3dEdit.Apply(beforeDoc, edit.Slots, forward: false);
        if (edit.AlreadyApplied && edit.FaceBoundaries is { } fb0) C3dPersistence.ApplyFaceReferences(beforeDoc, fb0.Before);
        var afterDoc = C3dPersistence.Deserialize(C3dPersistence.Serialize(beforeDoc));
        C3dEdit.Apply(afterDoc, slots, forward: true);
        if (edit.FaceBoundaries is { } fb) C3dPersistence.ApplyFaceReferences(afterDoc, fb.After);
        string? ccellPath = cell.CcellPath;
        string? ccellBefore = ccellPath is not null && File.Exists(ccellPath) ? File.ReadAllText(ccellPath) : null;
        var ccell = ccellBefore is not null ? CellPersistence.Deserialize(ccellBefore) : null;
        foreach (var w in writes)
            if (C3dVariableEdits.Apply(afterDoc, ccell, w) is { } why) return Refuse(edit, why, []);
        string? ccellAfter = ccell is not null ? CellPersistence.Serialize(ccell) : null;
        if (!writes.Any(w => w.Parameter)) (ccellPath, ccellBefore, ccellAfter) = (null, null, null);
        NamesWritten = writes.Select(w => w.Name).ToList();
        string description = writes.Count == 0 ? edit.Description : $"{edit.Description} (writes {string.Join(", ", writes.Select(w => w.Name))})";
        return new C3dDocumentEdit(description, C3dPersistence.Serialize(beforeDoc), C3dPersistence.Serialize(afterDoc), ccellPath, ccellBefore,
                                   ccellAfter, ApplyDocumentText);
    }

    /// <summary>The names the last committed drag wrote — gate 11 reads it.</summary>
    public IReadOnlyList<string> NamesWritten { get; private set; } = [];

    private IUiCommand? Refuse(C3dEdit edit, string why, IReadOnlyList<(string Item, string Path)> fields)
    {
        if (edit.AlreadyApplied)
        {
            C3dEdit.Apply(Document, edit.Slots, forward: false);
            if (edit.FaceBoundaries is { } fb) C3dPersistence.ApplyFaceReferences(Document, fb.Before);
            DocumentChanged();
        }
        OfferReplace(why, fields);
        return null;
    }

    // ── Replace with Number ───────────────────────────────────────────────────────────────────

    private IReadOnlyList<(string Item, string Path)> _replaceable = [];

    /// <summary>A drag was refused on these fields: the one-click alternative writes their current numbers in place of
    /// their expressions.</summary>
    [ObservableProperty] private bool _canReplaceWithNumber;

    private void OfferReplace(string why, IReadOnlyList<(string Item, string Path)> fields)
    {
        StatusMessage = why;
        _replaceable = fields;
        CanReplaceWithNumber = fields.Count > 0;
        ExpressionRefusals++;
    }

    /// <summary>Drags refused by the drag rule — gate 11 reads it.</summary>
    public int ExpressionRefusals { get; private set; }

    /// <summary>The refusal's one-click alternative: each refused field holds its current number, as one entry.</summary>
    [RelayCommand]
    public void ReplaceWithNumber()
    {
        var fields = _replaceable;
        _replaceable = [];
        CanReplaceWithNumber = false;
        if (fields.Count == 0) return;
        EditNames($"Replace {string.Join(", ", fields.Select(f => $"{f.Item} {f.Path}"))} with a number", (doc, _) =>
        {
            C3dResolver.Resolve(doc, Cell);
            foreach (var (item, path) in fields)
                if (C3dBindings.ItemsOf(doc).FirstOrDefault(i => i.Name == item).Item is { } it && C3dBindings.Find(it, path) is { } f)
                    C3dBindings.SetExpr(f.Owner, f.Spec, f.Component, null);
            return null;
        });
        StatusMessage = "Replaced with the number it held; drag it again.";
    }

    // ── the drag rule in a gesture's preview ─────────────────────────────────────────────────

    /// <summary>The names a gesture's preview writes: the document's VARs and its cell as the release would leave them.</summary>
    private (List<C3dVariable> Variables, C3dCell Cell)? _namePreview;

    /// <summary>
    /// A face or vertex gesture's object, through the drag rule: its dropped expressions put back, and the names it writes
    /// held for the preview. False — with the status line saying why and the gesture ended — when it is refused.
    /// </summary>
    private bool PreviewThroughNames(int index, ref C3dObject obj)
    {
        _namePreview = null;
        var src = Document.Objects[index];
        if (!C3dBindings.HasAny(src)) return true;
        var copy = C3dPersistence.DeserializeObject(C3dPersistence.SerializeObject(obj));
        var cell = Cell;
        var plan = PlanNames(src.Name, src, copy, Resolution, cell);
        if (plan.Refusal is { } why)
        {
            OfferReplace(why, plan.Unsolvable);
            return false;
        }
        obj = copy;
        if (plan.Writes.Count == 0) return true;
        var vars = C3dPersistence.DeserializeVariables(C3dPersistence.SerializeVariables(Document.Variables));
        var scratch = new C3dDocument { Variables = vars };
        var parameters = cell.Parameters.Select(p => p.Clone()).ToList();
        var ccell = new CcellFile { Parameters = parameters };
        foreach (var w in plan.Writes) C3dVariableEdits.Apply(scratch, cell.InCell ? ccell : null, w);
        _namePreview = (scratch.Variables, cell with { Parameters = parameters });
        return true;
    }

    /// <summary>
    /// Move and Rotate preview by moving the selection's own drawing, so an edit that writes a name reaching any other
    /// object would move that object on commit without having previewed it. Refused at the grab, naming the field; a
    /// placement whose names only the selection uses goes ahead, and its commit writes them.
    /// </summary>
    private string? OperationGrabRefusal(C3dOperationTool op)
    {
        var res = Resolution;
        var targetNames = op.Targets.Select(t => t.Instance ? Document.Instances[t.Index].Name : ObjectLabel(t.Index)).ToHashSet(StringComparer.Ordinal);
        var fields = new List<(string, string)>();
        foreach (var t in op.Targets)
        {
            object item = t.Instance ? Document.Instances[t.Index] : ObjectAt(t.Index)!;
            string name = t.Instance ? ((C3dInstance)item).Name : ((C3dObject)item).Name;
            foreach (var f in C3dBindings.BoundOf(name, item))
            {
                if (!f.Path.StartsWith("Placement.", StringComparison.Ordinal)) continue;
                bool rotation = f.Path.Contains("Rotate", StringComparison.Ordinal);
                if (rotation && op.TranslationOnly) continue;
                if (rotation)
                    return Offer($"'{name}' {f.Path} holds {f.Expr.Expr}: a rotation re-states the placement's angles, so an expression " +
                                 "in one cannot follow it. Replace it with a number to rotate it.", [(name, f.Path)]);
                var b = C3dVariableEdits.Classify(res, Cell, name, f.Path, f.Expr, f.Spec.Kind);
                if (b.Refusal is { } why) return Offer(why, [(name, f.Path)]);
                var elsewhere = res.Uses.TryGetValue(b.Name!, out var uses) ? uses.Where(u => !targetNames.Contains(u.Item)).Select(u => u.Item).Distinct().ToList() : [];
                if (elsewhere.Count > 0)
                    return Offer($"'{name}' {f.Path} holds {f.Expr.Expr}: moving it would write '{b.Name}', which also sizes " +
                                 $"{string.Join(", ", elsewhere.Take(4).Select(x => $"'{x}'"))} — they would move without having been previewed. " +
                                 $"Drag a face instead, change '{b.Name}' in the Variables panel, or replace the field with a number.", [(name, f.Path)]);
                fields.Add((name, f.Path));
            }
        }
        return null;

        string Offer(string why, IReadOnlyList<(string, string)> f)
        {
            OfferReplace(why, f);
            return why;
        }
    }
}
