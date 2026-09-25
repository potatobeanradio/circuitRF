using System;
using System.Linq;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Controls;

/// <summary>
/// What a double-click on the cross-section resolved to: which entry, which field, what the box
/// opens holding, and exactly where it goes.
/// </summary>
/// <param name="Seed">The ROW VM's staged string, never a freshly formatted number — see
/// <see cref="StackupInlineEditor.TargetAt"/>.</param>
/// <param name="TextX">The label's own text left edge, from <c>StackupLabel.TextX</c>.</param>
/// <param name="Baseline">The label's own Skia baseline, from <c>StackupLabel.Baseline</c>.</param>
/// <param name="FontSize">The size that label was measured and drawn at.</param>
internal readonly record struct StackupEditTarget(
    string       LayerName,
    StackupField Field,
    string       Seed,
    float        TextX,
    float        Baseline,
    float        FontSize);

/// <summary>
/// <b>Editing a value ON the drawing, with the editor the schematic already has</b> —
/// brief-stackup-render-4-inline-edit.md.
///
/// <h3>The box is <see cref="SchematicInlineEditBox"/> and it is used AS-IS</h3>
/// <para>Owner, 2026-09-13: the schematic editor's inline text editor has already been implemented
/// and debugged, so reuse it rather than debugging a second one. Three things in that class took real
/// debugging and all three are what a box floated over a Skia canvas needs — the
/// <c>StyleKeyOverride</c> that keeps a <c>TextBox</c> subclass from silently measuring zero high,
/// the margin that lands the box's text on the label's own BASELINE, and a Skia MEASUREMENT of the
/// string rather than a character count. Nothing here reimplements any of them, and nothing here
/// modifies that class.</para>
///
/// <h3>What is genuinely this host's, and is therefore what this file is</h3>
/// <para>The class's own comment says the hosting is not reusable: which surface the box floats over,
/// what a hit means, and what a commit writes differ in every host. Those three are
/// <see cref="TargetAt"/>, <see cref="TryOpen"/> and <see cref="Commit"/>.</para>
///
/// <h3>A commit goes THROUGH the row VM, never around it (R-stk4-5)</h3>
/// <para>It does exactly two things: assign the staged string the card's own <c>TextBox</c> binds,
/// and call the method the card's own LostFocus handler calls. <see cref="StackupLayerRowViewModel"/>
/// already owns the parse, the unit resolution in the technology's display unit, the invariant-culture
/// rule, the error text, the readiness re-ask, the no-op check and the <c>CommitEdit</c> that pushes
/// the undo entry — so a bad value behaves on the drawing exactly as it does on the card, and
/// there is no second refusal message here to disagree with the first.</para>
///
/// <h3>Framework-free on purpose, except for the box itself</h3>
/// <para>Everything this type decides is a function of a <see cref="StackupScene"/>, a
/// <see cref="TechEditorViewModel"/> and a string. The box is an Avalonia control, but it constructs,
/// opens, measures and selects with no application host — which is what lets the gate exercise the
/// REAL control rather than a stand-in for it.</para>
/// </summary>
internal sealed class StackupInlineEditor
{
    private readonly SchematicInlineEditBox _box;

    /// <summary>The entry being edited, held by NAME — R-stk3-1's rule, at the one point in this
    /// brief where it could be broken. A row-VM reference would not survive the rebuild that any
    /// committed edit, undo or redo performs, and although <see cref="Close"/> is wired to every one
    /// of those (R-stk4-8), a reference held across them is a trap waiting for the next event that
    /// is not.</summary>
    private string?             _layerName;
    private StackupField        _field;
    private TechEditorViewModel? _vm;

    public StackupInlineEditor(SchematicInlineEditBox box) => _box = box;

    /// <summary>Raised after the box is shown, so the host can take focus — which needs a visual
    /// root, and is therefore deliberately NOT done here.</summary>
    public event Action? Opened;

    /// <summary>
    /// Raised after the box is HIDDEN, and <see cref="Opened"/>'s other half: the host has to put
    /// keyboard focus back somewhere inside itself.
    ///
    /// <para><b>Hiding a focused control drops focus out of the tab entirely.</b> Owner, 2026-09-13:
    /// a second Esc did not clear the band selection. The first one reverted correctly, and then
    /// nothing in the editor held focus any more — so the second keystroke routed nowhere near
    /// <c>TechEditorView</c>, whose tunnelling handler is what clears the selection. The same gap
    /// silenced Page Up/Down after any committed edit, which is R-stk2-10's whole concern.</para>
    ///
    /// <para>It fires only when the box was actually open, so the rebuild-driven <see cref="Close"/>
    /// on every committed edit, undo and redo (R-stk4-8) does not yank focus out of whatever the user
    /// is typing in on the card below.</para>
    /// </summary>
    public event Action? Closed;

    public bool IsOpen => _box.IsVisible;

    /// <summary>Which field the open box is editing. <see cref="StackupField.None"/> when closed —
    /// for the gate, and for nobody else.</summary>
    internal StackupField OpenField => IsOpen ? _field : StackupField.None;

    internal string? OpenLayerName => IsOpen ? _layerName : null;

    /// <summary>The box itself, for a host that has to wire its key and focus events.</summary>
    internal SchematicInlineEditBox Box => _box;

    // ── Open (R-stk4-3, R-stk4-4) ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// What a double-click at <paramref name="x"/>, <paramref name="y"/> would edit, or null for a
    /// hit that is not an editable value.
    /// </summary>
    /// <remarks>
    /// <b>The seed is the row VM's staged string</b> and never a second formatting of the same
    /// number. Formatting twice in two places is how the drawing and the card come to disagree about
    /// a trailing zero, and then an edit that touched nothing writes a changed file. It happens to
    /// agree today for six of the seven fields — the scene formats them with the same calls the row
    /// VM does, which is R-stk1-10's own rule — and it does NOT agree for a via wall thickness the
    /// process never stated: the drawing shows "0" where the card stages "", and "" is the string
    /// whose commit means "clear it".
    ///
    /// <para><see cref="StackupField.Span"/> is refused along with <see cref="StackupField.None"/>.
    /// A span is a PAIR of conductor names, chosen from the card's combo boxes and dragged in brief 5
    /// — it is not free text, so its label carries the field for selection and reporting and this
    /// declines it (§4).</para>
    /// </remarks>
    internal static StackupEditTarget? TargetAt(StackupScene scene, TechEditorViewModel vm, float x, float y)
    {
        if (scene.HitTest(x, y) is not { Kind: StackupHitKind.Label } hit) return null;
        if (hit.Field is StackupField.None or StackupField.Span) return null;

        // The LABEL behind the hit, for its text origin. The hit rect IS the label's padded rect —
        // one is built from the other in StackupScene.PieceRun.Place — and R-stk1-9 guarantees no two
        // label rects intersect, so the match is unique.
        var label = scene.Labels.FirstOrDefault(l =>
            l.Field == hit.Field &&
            string.Equals(l.LayerName, hit.LayerName, StringComparison.Ordinal) &&
            l.Rect.Equals(hit.Rect));
        if (label is null) return null;

        var row = RowFor(vm, hit.LayerName);
        if (row is null) return null;
        // brief-em3d-2 R-em3d2-5c: a number the entry's named material states is read-only on the
        // card, so it is not typeable here either — a box that opened and then silently reverted on
        // commit would be the card and the drawing disagreeing about what can be edited.
        if (row.IsLockedByMaterial(hit.Field)) return null;
        if (SeedFor(row, hit.Field) is not { } seed) return null;

        return new StackupEditTarget(
            hit.LayerName, hit.Field, seed,
            label.TextX, label.Baseline, StackupScene.FontSizeFor(label.Style));
    }

    /// <summary>Opens the box over one label. Returns false when there is nothing editable there.</summary>
    internal bool TryOpen(StackupScene scene, TechEditorViewModel vm, float x, float y)
    {
        if (TargetAt(scene, vm, x, y) is not { } t) return false;

        _vm        = vm;
        _layerName = t.LayerName;
        _field     = t.Field;

        _box.Open(t.Seed, t.TextX, t.Baseline, t.FontSize);

        // The unit is left standing so typing replaces only the number. On this surface the staged
        // strings carry no unit of their own — a thickness stages "1.6" and the drawing draws the
        // "mm" as its own separate label piece — so today this selects the whole string. It is called
        // anyway: it is the contract every inline editor in this application honours, and a later
        // change to how a value is staged must not silently turn "type over the number" into "type
        // over the number and its unit".
        _box.SelectValueOnly();

        Opened?.Invoke();
        return true;
    }

    // ── Commit and revert (R-stk4-5, R-stk4-6, R-stk4-7) ──────────────────────────────────────────

    /// <summary>
    /// Return, and losing focus. <b>Closes the box FIRST</b> (R-stk4-7): the commit rebuilds every row
    /// VM and raises <c>StackupChanged</c>, which rebuilds the scene, so a box still open afterwards
    /// is sitting on a rect that no longer exists.
    /// </summary>
    public void Commit()
    {
        if (!IsOpen) return;

        string text  = _box.Text ?? "";
        var    field = _field;
        var    name  = _layerName;
        var    vm    = _vm;

        // Before the two statements below, and before anything they raise. Close() clears IsOpen
        // first of all, which is also what makes this re-entrant safely: hiding a focused box raises
        // LostFocus, and LostFocus is itself a commit path — the second call finds the box shut and
        // returns, so one Return commits once.
        Close();

        if (name is null) return;
        if (RowFor(vm, name) is not { } row) return;

        Apply(row, field, text);
    }

    /// <summary>Escape. Closes the box and writes nothing — not even the staged string, so the card
    /// below is untouched as well as the model.</summary>
    public void Revert() => Close();

    /// <summary>
    /// R-stk4-8. Every scene rebuild closes the box: an undo, a redo, an edit committed from the card
    /// below, brief 6's Add Via. All of them invalidate the label the box is sitting on.
    /// </summary>
    public void Close()
    {
        if (!IsOpen) return;
        _box.IsVisible = false;
        _layerName     = null;
        _field         = StackupField.None;
        _vm            = null;

        // LAST, with this editor's own state already reset: the host's handler takes focus, and a
        // handler that ran while IsOpen still read true would be taking it out of a box this method
        // is halfway through shutting.
        Closed?.Invoke();
    }

    // ── The field mapping table — the whole of R-stk4-5 ───────────────────────────────────────────

    /// <summary>The staged string the card shows for this field, or null for a field this editor
    /// does not type.</summary>
    private static string? SeedFor(StackupLayerRowViewModel row, StackupField field) => field switch
    {
        StackupField.Name          => row.StagedName,
        StackupField.Thickness     => row.StagedThicknessText,
        StackupField.Sigma         => row.StagedSigmaSm,
        StackupField.Epsr          => row.StagedEpsr,
        StackupField.TanD          => row.StagedTanD,
        StackupField.Mur           => row.StagedMur,
        StackupField.WallThickness => row.StagedWallThickness,
        _                          => null,
    };

    /// <summary>
    /// Stages the text on the same property the card's own <c>TextBox</c> binds and calls the same
    /// method the card's own LostFocus handler calls. <b>Two statements, and nothing else</b> — no
    /// write to <c>Working.Stackup</c>, no <c>CommitEdit</c>, no <c>UndoRedo.Execute</c>.
    /// </summary>
    internal static void Apply(StackupLayerRowViewModel row, StackupField field, string text)
    {
        switch (field)
        {
            case StackupField.Name:
                row.StagedName = text;
                row.CommitName();
                break;
            case StackupField.Thickness:
                row.StagedThicknessText = text;
                row.CommitThickness();
                break;
            case StackupField.Sigma:
                row.StagedSigmaSm = text;
                row.CommitSigmaSm();
                break;
            case StackupField.Epsr:
                row.StagedEpsr = text;
                row.CommitEpsr();
                break;
            case StackupField.TanD:
                row.StagedTanD = text;
                row.CommitTanD();
                break;
            case StackupField.Mur:
                row.StagedMur = text;
                row.CommitMur();
                break;
            case StackupField.WallThickness:
                row.StagedWallThickness = text;
                row.CommitWallThickness();
                break;
        }
    }

    private static StackupLayerRowViewModel? RowFor(TechEditorViewModel? vm, string layerName) =>
        vm?.StackupLayers.FirstOrDefault(r =>
            string.Equals(r.Layer.Name, layerName, StringComparison.Ordinal));
}
