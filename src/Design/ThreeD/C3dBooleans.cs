// brief-em3d-66 — the rules a boolean is MADE by, in the editor and anywhere else: what may be an operand and why not,
// which selected object is the Blank, what the result keeps, and the sentences each of those is said in. Pure functions of
// the document's objects: the editor, its tree, its inspector and its tests all call these, so no rule is written twice.
//
// FIRST-SELECTED IS A TOOL, LAST-SELECTED IS THE BLANK (D4). Written once, in DefaultBlank, and said once, in RowsTip.
//
// AN OPERAND IS ADDRESSED BY A PATH, NOT AN INDEX (R-em3d66-3c). A path is the operand prefixes C3dOperands.Of already
// spells, concatenated — "Blank.", "Tools[1].", "Tools[0].Blank." — from a top-level object down. brief-em3d-67 adds
// "Target.", a Fillet's or Chamfer's, so the operands of a rounded boolean are still addressed: "Target.Tools[0].". The empty path is the
// top-level object itself. The spelling is the one C3dBindings names an operand's fields with, so a field of an operand is
// the path followed by the field.
//
// DISSOLVE IS WHAT DISABLED MEANS (R-em3d66-4a). Dissolve writes the Blank under the boolean's name at the boolean's place,
// then the Tools in order, each carried by the boolean's placement — exactly the objects the elaborator makes of a disabled
// boolean. A placement is composed only when the boolean's is not the identity, so the usual case rewrites nothing.

using System.Globalization;
using System.Text.RegularExpressions;

namespace CircuitRF.Design.ThreeD;

public static partial class C3dBooleans
{
    // ── what may be an operand (R-em3d66-1) ─────────────────────────────────────────────────────

    /// <summary>Fewer than two legal solids are selected.</summary>
    public const string SelectTwo = "Select two or more solids, or two or more sheets in one plane: the first one you select is a Tool.";

    /// <summary>brief-hzn-10 — sheets and solids selected together.</summary>
    public static string MixedRefused(string sheet, string solid)
        => $"'{sheet}' is a sheet and '{solid}' is a solid: a boolean combines solids with solids, or sheets with sheets in one plane.";

    /// <summary>The rule, as the panel's rows say it.</summary>
    public const string RowsTip = "The first object you select is a Tool; the last is the Blank. Choose another Blank here.";

    /// <summary>A face or vertex selection — the sentence object operations already use.</summary>
    public const string ObjectModeOnly = "Booleans act on whole objects: switch to Object mode (O).";

    /// <summary>An instance among the selection.</summary>
    public const string InstanceRefused =
        "An instance's solids belong to its own cell and resolve through its own technology: cutting them here would edit " +
        "another cell. Push into the cell, or Flatten it.";

    /// <summary>Why <paramref name="o"/> cannot be an operand, or null when it can (R-em3d66-1a/-1b). brief-hzn-10 — a flat sheet
    /// can, with other sheets in its plane (<see cref="SelectionRefusal"/> says when it is among solids).</summary>
    public static string? OperandRefusal(C3dObject o) => o switch
    {
        _ when C3dSheetBooleans.IsSheet(o) => null,
        C3dCylinderSheet => $"'{o.Name}' is a sheet on a cylinder: only flat sheets in one plane are combined.",
        C3dPolyline => $"'{o.Name}' is a polyline — construction geometry, never in the problem: Extrude it first.",
        C3dWire => $"'{o.Name}' is a bond wire: its shape is made again from its points and its pads, so a cut wire would stop being a wire.",
        _ when C3dOperands.IsSolid(o) => null,
        _ => $"'{o.Name}' is not a solid.",
    };

    /// <summary>
    /// Why the selection cannot be a boolean's operands, or null when it can: <paramref name="selected"/> in selection
    /// order, an instance as null. The first refused object is named; with none refused, fewer than two is
    /// <see cref="SelectTwo"/>.
    /// </summary>
    public static string? SelectionRefusal(IReadOnlyList<C3dObject?> selected)
    {
        if (selected.Any(o => o is null)) return InstanceRefused;
        foreach (var o in selected)
            if (OperandRefusal(o!) is { } why) return why;
        // brief-hzn-10 — all sheets, or all solids; sheets in one plane, each placed within it.
        var sheet = selected.FirstOrDefault(o => C3dSheetBooleans.IsSheet(o!));
        var solid = selected.FirstOrDefault(o => !C3dSheetBooleans.IsSheet(o!));
        if (sheet is not null && solid is not null) return MixedRefused(sheet.Name, solid.Name);
        if (selected.Count < 2) return SelectTwo;
        return sheet is not null ? C3dSheetBooleans.SelectionRefusal([.. selected.Select(o => o!)]) : null;
    }

    /// <summary>brief-hzn-10 — whether <paramref name="selected"/> would make a Boolean of sheets, which the kernel is never asked
    /// to build.</summary>
    public static bool AllSheets(IReadOnlyList<C3dObject?> selected) => selected.Count > 0 && selected.All(o => o is not null && C3dSheetBooleans.IsSheet(o));

    // ── Tool and Blank (D4, R-em3d66-2a) ──────────────────────────────────────────────────────────

    /// <summary>The Blank's row for <paramref name="count"/> objects in selection order: the LAST-selected; every other row,
    /// the first-selected among them, is a Tool.</summary>
    public static int DefaultBlank(int count) => count - 1;

    // ── the words ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The operation's verb, the panel's title.</summary>
    public static string Verb(C3dBooleanOp op) => op.ToString();

    /// <summary>The undo entry's words: <c>Subtract cavity from lid</c>, <c>Unite lid and pin</c>, <c>Intersect lid with pin</c>.</summary>
    public static string Description(C3dBooleanOp op, string blank, IReadOnlyList<string> tools) => op switch
    {
        C3dBooleanOp.Subtract => $"Subtract {Plain(tools)} from {blank}",
        C3dBooleanOp.Unite => $"Unite {Plain([blank, .. tools])}",
        _ => $"Intersect {blank} with {Plain(tools)}",
    };

    /// <summary>
    /// R-em3d66-2c — what the result keeps, one line: its name, its material, and every material a Unite replaces, named,
    /// because a silent material change is the error nobody sees (D11).
    /// </summary>
    public static string Keeps(C3dBooleanOp op, string blank, string? blankMaterial, IReadOnlyList<(string Name, string? Material)> tools,
                               bool keepTools = false)
    {
        string material = blankMaterial is { Length: > 0 } m ? m : "no material";
        string names = Quoted(tools.Select(t => t.Name).ToList());
        switch (op)
        {
            case C3dBooleanOp.Subtract:
                return $"'{blank}' keeps its name and {material}; {names} {(tools.Count == 1 ? "is" : "are")} removed from it" +
                       (keepTools ? $" and {(tools.Count == 1 ? "stays a solid of its own" : "stay solids of their own")}." : ".");
            case C3dBooleanOp.Unite:
            {
                var replaced = tools.Where(t => t.Material is { Length: > 0 } tm && tm != blankMaterial).ToList();
                string line = $"The result is '{blank}', {material}.";
                if (replaced.Count > 0)
                    line += " " + string.Join(", ", replaced.Select(t => $"'{t.Name}' ({t.Material})")) +
                            $" {(replaced.Count == 1 ? "becomes" : "become")} {material}.";
                return line;
            }
            default:
                return $"The result is '{blank}', {material}: what {Quoted([blank, .. tools.Select(t => t.Name)])} share.";
        }
    }

    /// <summary>The note a result in several pieces carries — not a refusal (R-em3d66-2f).</summary>
    public static string? Pieces(int solids) => solids > 1 ? $"{solids} pieces, one object." : null;

    /// <summary>R-em3d66-2f — an operation that leaves no solid, in the operation's own words.</summary>
    public static string EmptyResult(C3dObject obj, string name) => obj switch
    {
        C3dBoolean { Op: C3dBooleanOp.Intersect } b => $"{Quoted([name, .. b.Tools.Select(t => t.Name)])} share nothing: the intersection is empty.",
        C3dBoolean { Op: C3dBooleanOp.Subtract } b => $"Subtracting {Quoted(b.Tools.Select(t => t.Name).ToList())} from '{name}' removes all of it.",
        C3dBoolean when C3dSheetBooleans.IsSheetBoolean(obj) => $"'{name}' is empty: the operation leaves no sheet.",
        _ => $"'{name}' is empty: the operation leaves no solid.",
    };

    /// <summary>D13 — a face or vertex edit on a kernel-made solid, refused with what to edit instead.</summary>
    public static string ResultNotEditable(string name, C3dObject obj) => obj is C3dBoolean
        ? $"'{name}' is made by a boolean: edit its operands (double-click it) or the operation in Properties."
        : $"'{name}' is made by the kernel: edit what it is made from, or the operation in Properties.";

    /// <summary>Deleting a boolean's Blank (R-em3d66-6).</summary>
    public const string BlankNeeded = "A boolean needs its Blank: choose another Blank first, or Dissolve.";

    /// <summary>The Keep-tools checkbox's tooltip (D10).</summary>
    public const string KeepToolsTip =
        "Subtract only: each Tool stays in the model as a solid of its own beside the result — subtract a dielectric slug from " +
        "a lid and keep the slug as the fill.";

    // ── making and unmaking ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// R-em3d66-2g — the boolean <paramref name="op"/> of <paramref name="operands"/> (selection order; each is COPIED) with row
    /// <paramref name="blank"/> the Blank. It takes the Blank's name; the Blank inside keeps its material, role and placement
    /// and no name of its own (R-em3d64-1d); the Tools keep theirs. Nothing is hidden. <c>KeepTools</c> is a Subtract's.
    /// </summary>
    public static C3dBoolean Make(C3dBooleanOp op, IReadOnlyList<C3dObject> operands, int blank, bool keepTools)
    {
        var copies = operands.Select(Copy).ToList();
        foreach (var c in copies) c.Hidden = false;
        var b = copies[blank];
        string name = b.Name;
        string? group = b.Group;
        int? transparency = b.Transparency;
        var appearance = b.Appearance;
        bool model = b.Model;
        b.Name = "";
        // The result is in the Blank's group, as it takes the Blank's name; an operand is in none (C3dGroups). brief-em3d-92 —
        // likewise its transparency: the result carries the Blank's, and an operand has none of its own.
        // brief-em3d-93 — and its Model: the result is modelled as the Blank was.
        // brief-em3d-105 — and its appearance, as its transparency.
        foreach (var c in copies) (c.Group, c.Transparency, c.Appearance, c.Model) = (null, null, null, true);
        return new C3dBoolean
        {
            Name = name, Group = group, Transparency = transparency, Appearance = appearance, Model = model, Op = op, KeepTools = keepTools && op == C3dBooleanOp.Subtract,
            Blank = b, Tools = [.. copies.Where((_, i) => i != blank)],
        };
    }

    /// <summary>
    /// R-em3d66-6 — the boolean's operands as top-level objects: the Blank under the boolean's name, then the Tools in order,
    /// each carried by the boolean's placement. What a disabled boolean elaborates (R-em3d66-4a).
    /// </summary>
    public static List<C3dObject> Dissolve(C3dBoolean b)
    {
        var list = new List<C3dObject>();
        if (b.Blank is { } blank)
        {
            var c = Carried(blank, b);
            c.Name = b.Name;
            list.Add(c);
        }
        foreach (var t in b.Tools) list.Add(Carried(t, b));
        foreach (var c in list) (c.Group, c.Transparency, c.Appearance, c.Model) = (b.Group, b.Transparency, b.Appearance?.Clone(), b.Model);   // in the boolean's group, at its transparency and look, modelled as it was
        return list;
    }

    /// <summary>A copy of <paramref name="operand"/> as a top-level object: placed where <paramref name="parent"/> puts it.</summary>
    public static C3dObject Carried(C3dObject operand, C3dObject parent)
    {
        var c = Copy(operand);
        if (!parent.Placement.IsDefault) c.Placement = c.Placement.Then(parent.Placement.ToTransform(), out _);
        return c;
    }

    /// <summary>A deep copy, as the file spells it.</summary>
    public static C3dObject Copy(C3dObject o) => C3dPersistence.DeserializeObject(C3dPersistence.SerializeObject(o));

    // ── paths (R-em3d66-3c) ───────────────────────────────────────────────────────────────────────

    [GeneratedRegex(@"\G(?:(Target\.)|Blank\.|Tools\[(\d+)\]\.)")]
    private static partial Regex Step();

    /// <summary>The step a Fillet's or Chamfer's Target is (brief-em3d-67).</summary>
    public const int TargetStep = -2;

    /// <summary>A path's steps: −1 for a Blank, k for Tools[k], <see cref="TargetStep"/> for a feature's Target. Null when it
    /// is not a path.</summary>
    public static List<int>? Steps(string path)
    {
        var steps = new List<int>();
        int at = 0;
        var step = Step();
        while (at < path.Length)
        {
            var m = step.Match(path, at);
            if (!m.Success) return null;
            steps.Add(m.Groups[1].Success ? TargetStep : m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : -1);
            at += m.Length;
        }
        return steps;
    }

    /// <summary>One step's spelling.</summary>
    public static string StepText(int step) => step == TargetStep ? "Target." : step < 0 ? "Blank." : $"Tools[{step.ToString(CultureInfo.InvariantCulture)}].";

    /// <summary>The path of the operand's parent — the path with its last step removed.</summary>
    public static string ParentPath(string path)
    {
        var steps = Steps(path) ?? [];
        return string.Concat(steps.Take(Math.Max(0, steps.Count - 1)).Select(StepText));
    }

    /// <summary>The last step of a non-empty path: −1 for a Blank, k for Tools[k].</summary>
    public static int LastStep(string path) => Steps(path) is { Count: > 0 } s ? s[^1] : int.MinValue;

    /// <summary>The object at <paramref name="path"/> under <paramref name="top"/>, or null when the path leads nowhere.</summary>
    public static C3dObject? At(C3dObject top, string path)
    {
        if (Steps(path) is not { } steps) return null;
        C3dObject? o = top;
        foreach (int s in steps)
            o = s == TargetStep ? (o is C3dFillet or C3dChamfer ? C3dOperands.Inner(o) : null)
              : o is C3dBoolean b ? (s < 0 ? b.Blank : s < b.Tools.Count ? b.Tools[s] : null) : null;
        return o;
    }

    /// <summary>A copy of <paramref name="top"/> with the object at <paramref name="path"/> replaced by <paramref name="replacement"/>
    /// (the empty path replaces the top itself).</summary>
    public static C3dObject With(C3dObject top, string path, C3dObject replacement)
    {
        if (path.Length == 0) return replacement;
        var copy = Copy(top);
        // An internal invariant: every path here was spelt by this class (or C3dOperands), never typed by anyone.
        var steps = Steps(path) ?? throw new ArgumentException(null, nameof(path));
        C3dObject o = copy;
        for (int k = 0; k < steps.Count - 1; k++)
            o = steps[k] == TargetStep ? C3dOperands.Inner(o)! : steps[k] < 0 ? ((C3dBoolean)o).Blank! : ((C3dBoolean)o).Tools[steps[k]];
        switch (o)
        {
            case C3dFillet f when steps[^1] == TargetStep: f.Target = replacement; break;
            case C3dChamfer c when steps[^1] == TargetStep: c.Target = replacement; break;
            case C3dBoolean parent when steps[^1] < 0: parent.Blank = replacement; break;
            case C3dBoolean parent: parent.Tools[steps[^1]] = replacement; break;
        }
        return copy;
    }

    /// <summary>
    /// The transform that places the operand at <paramref name="path"/> in the world of its top-level object: the placements
    /// of every boolean above it, innermost first. The identity for a top-level object.
    /// </summary>
    public static C3dTransform ParentTransform(C3dObject top, string path)
    {
        var chain = new List<C3dObject>();
        var steps = Steps(path) ?? [];
        C3dObject? o = top;
        foreach (int s in steps)
        {
            if (s == TargetStep && o is C3dFillet or C3dChamfer) { chain.Add(o); o = C3dOperands.Inner(o); continue; }
            if (o is not C3dBoolean b) break;
            chain.Add(b);
            o = s < 0 ? b.Blank : s < b.Tools.Count ? b.Tools[s] : null;
        }
        var t = C3dTransform.Identity;
        for (int k = chain.Count - 1; k >= 0; k--)
            if (!chain[k].Placement.IsDefault) t = t.Then(chain[k].Placement.ToTransform());
        return t;
    }

    // ── lists in a sentence ───────────────────────────────────────────────────────────────────────

    /// <summary><c>'a'</c>, <c>'a' and 'b'</c>, <c>'a', 'b' and 'c'</c>.</summary>
    public static string Quoted(IReadOnlyList<string> names) => Join(names.Select(n => $"'{n}'").ToList());

    private static string Plain(IReadOnlyList<string> names) => Join(names);

    private static string Join(IReadOnlyList<string> items)
        => items.Count <= 1 ? string.Concat(items) : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];
}
