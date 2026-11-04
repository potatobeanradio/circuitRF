using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using CircuitRF.Design.Layout;
using CircuitRF.Ui.Converters;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Controls;

/// <summary>
/// brief 6 — the right-click menu over the cross-section.
///
/// <h3>The canvas RECORDS and BUILDS; it never opens, and it never writes</h3>
/// <para>R-stk6-1: the single <c>ContextMenu</c> instance is declared once on this control in
/// <c>TechEditorView.axaml</c>. A right-click here only records where it landed and selects what is
/// under it; Avalonia opens the menu itself and raises <c>Opening</c>, whose handler calls
/// <see cref="ConsumeContextMenuTarget"/> and replaces <c>ItemsSource</c> with a FRESH list from
/// <see cref="BuildContextMenuItems"/>. That is <c>LayoutCanvas</c>'s own fix adopted whole, and the
/// bug it exists for is not hypothetical: a menu built and opened per right-click STACKS, and item
/// instances reused across openings fire their <c>Click</c> N times on the Nth opening.</para>
///
/// <para>R-stk6-2: every item calls a method or a property setter on
/// <see cref="TechEditorViewModel"/> or <see cref="StackupLayerRowViewModel"/> — the ones the card
/// below already calls. Nothing in this file touches <c>Working</c>, pushes an undo entry or
/// serializes anything, which is what R-stk6-7's source scan holds shut for the whole series.</para>
/// </summary>
public sealed partial class StackupCanvas
{
    /// <summary>
    /// How many drawing layers a submenu may list before it stops and points at the card.
    ///
    /// <para>R-stk6-8: a real process carries several hundred drawing layers — an imported one
    /// measured 377, which is why the card's own picker is filtered, bounded and virtualized. A flat
    /// submenu of 377 items is not a menu. Twelve is what the card's picker shows at once
    /// (<see cref="TechEditorMetrics.DrawingLayerPickerColumns"/> across by
    /// <see cref="TechEditorMetrics.DrawingLayerPickerRows"/> down), so the two surfaces show the
    /// same amount before either asks the user to narrow.</para>
    /// </summary>
    internal const int ContextMenuLayerLimit =
        TechEditorMetrics.DrawingLayerPickerColumns * TechEditorMetrics.DrawingLayerPickerRows;

    // ── Tooltips: on the SUBMENUS, never on the root menu (owner, 2026-09-13) ──────────────────────
    //
    // Every root item used to carry the card's own tooltip for the field it sets, which is where
    // R-stk6-8 put them. On the real menu they are wider than the menu is and pop up OVER it, so the
    // item names underneath cannot be read — and a menu you cannot read is worse than one that
    // explains nothing. They stay on the items INSIDE a submenu, which is where the closed choices
    // are and where a reader who does not know what "Bottom of its own band" means is actually
    // looking; a submenu flyout is also not the thing a tooltip over the root menu covers up.
    //
    // The one root tooltip left is Add Via's REFUSAL, and only while the item is disabled: it is the
    // whole of R-stk6-4's "disabled WITH a reason", a disabled item cannot be hovered so it covers
    // nothing, and dropping it would leave an item that is greyed out and says why nowhere.

    /// <summary>Scene coordinates of the pending right-click, or null when nothing is pending — in
    /// which case the <c>Opening</c> handler cancels the menu rather than showing whatever the last
    /// one built.</summary>
    internal Point? ContextMenuTarget { get; private set; }

    /// <summary>Returns and clears the pending target — atomic, so a stale target can never be
    /// reused for a later, unrelated opening.</summary>
    internal Point? ConsumeContextMenuTarget()
    {
        var t = ContextMenuTarget;
        ContextMenuTarget = null;
        return t;
    }

    /// <summary>
    /// The right-click gesture, without the pointer event — the same seam <see cref="PressAt"/> is,
    /// and for the same reason: there is no input device in this project's tests.
    ///
    /// <para><b>It selects before the menu builds</b> (R-stk6-1). A menu acting on something that is
    /// not visibly selected is a menu whose target the user has to guess — and because the selection
    /// is the view model's, the card list below scrolls to the same entry at the same moment.</para>
    ///
    /// <para>A live left-drag is CANCELLED rather than committed. The pointer that was dragging is
    /// still down; a right-click mid-gesture is the user changing their mind about it, and dropping
    /// a band wherever the second button happened to be pressed is not what they asked for.</para>
    /// </summary>
    /// <returns>False when there is nothing to select into (no view model).</returns>
    internal bool RightClickAt(Point p)
    {
        if (_viewModel is null) { ContextMenuTarget = null; return false; }

        CancelDrag();
        var hit = HitAt(p);
        _viewModel.SelectFromDrawing(hit?.LayerName, hit?.FilmLayer);
        ContextMenuTarget = p;
        return true;
    }

    /// <summary>
    /// A FRESH item list for a right-click at <paramref name="p"/> — built anew on every
    /// <c>Opening</c>, never reused.
    ///
    /// <para>The shape is R-stk6-0's table, with §5's closed-choice parameters folded into the same
    /// menu: the kind's own items, a separator, the delete labelled BY KIND, a separator, and Copy.
    /// Delete is labelled "Delete Conductor" / "Delete Dielectric" / "Delete Via" because a menu that
    /// says "Delete" over a picture with three kinds of thing in it is a menu people misfire.</para>
    /// </summary>
    internal List<object> BuildContextMenuItems(Point p)
    {
        var items = new List<object>();
        var vm = _viewModel;
        var row = vm is null ? null : RowAt(p);

        // A film has its own menu, and no Delete Conductor: it is not the band under it.
        if (vm is not null && HitAt(p)?.FilmLayer is { } film)
        {
            AddFilmItems(items, vm, film);
            items.Add(new Separator());
            items.Add(CopyItem());
            return items;
        }

        if (vm is not null && row is not null)
        {
            if (row.IsDielectric)   AddDielectricItems(items, vm, row);
            else if (row.IsVia)     AddViaItems(items, row);
            else                    AddConductorItems(items, row);

            items.Add(new Separator());
            items.Add(DeleteItem(vm, row));
            items.Add(new Separator());
        }

        items.Add(CopyItem());
        return items;
    }

    /// <summary>The row VM for whatever is under <paramref name="p"/>, resolved BY NAME through the
    /// view model — never a reference cached from the scene, which the next committed edit
    /// invalidates (R-stk3-1).</summary>
    private StackupLayerRowViewModel? RowAt(Point p)
    {
        if (HitAt(p)?.LayerName is not { Length: > 0 } name) return null;
        return _viewModel?.StackupLayers.FirstOrDefault(
            r => string.Equals(r.Layer.Name, name, System.StringComparison.Ordinal));
    }

    // ── Delete (R-stk6-3) ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>RemoveStackupLayer</c> already snapshots, already commits and already describes itself, so
    /// calling it IS the implementation and undo comes with it. <b>No confirmation dialog</b> — undo
    /// is the confirmation, and it is already there.
    ///
    /// <para>Deleting a conductor a via's span names leaves that via unresolvable, and that is
    /// deliberately NOT fixed here: <c>TechValidation</c> reports it, the card list flags it and
    /// R-stk1-6 draws it as a refusal marker. Deleting the via too, or silently re-pointing its
    /// span, would make one deletion do two things.</para>
    /// </summary>
    private static MenuItem DeleteItem(TechEditorViewModel vm, StackupLayerRowViewModel row)
    {
        var mi = new MenuItem { Header = $"Delete {row.Kind}" };
        mi.Click += (_, _) => vm.RemoveStackupLayer(row);
        return mi;
    }

    // ── Copy (R-stk7-4) ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// R-stk7-4 — on every target and on empty space alike, because what it copies is the whole
    /// drawing rather than whatever was right-clicked.
    ///
    /// <para>It copies the WHOLE stackup at the page's width, not the scrolled window (R-stk7-2), and
    /// without a selection outline or any other overlay (R-stk7-3) — so a Copy taken with a band
    /// selected and one taken with nothing selected put the same picture on the clipboard. The work
    /// is <see cref="StackupGraphicExport"/>'s; this is the gesture.</para>
    /// </summary>
    private MenuItem CopyItem()
    {
        var mi = new MenuItem { Header = "Copy" };
        mi.Click += async (_, _) => await CopyPictureAsync();
        return mi;
    }

    /// <summary>The copy itself, without a menu or a keystroke — the same seam
    /// <see cref="PressAt"/> and <see cref="RightClickAt"/> are, so the gate can take it without an
    /// input device or a clipboard.</summary>
    internal Task CopyPictureAsync()
        => StackupGraphicExport.CopyToClipboardAsync(this, _viewModel?.Working);

    // ── Conductor ─────────────────────────────────────────────────────────────────────────────────

    private void AddConductorItems(List<object> items, StackupLayerRowViewModel row)
    {
        bool ground = row.IsGroundReference;
        var gr = new MenuItem
        {
            Header    = StackupCardText.GroundReference,
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = ground,
        };
        gr.Click += (_, _) => row.IsGroundReference = !ground;
        items.Add(gr);

        // The stored value is a surface of the conductor's own z band; what the rows SAY is the
        // consequence, through the converter the card's own ComboBox renders each row with. Two
        // spellings of the same choice is how a control comes to ask two different questions.
        var sheet = new MenuItem { Header = StackupCardText.SheetAt };
        var sheetItems = new List<object>();
        foreach (var surface in StackupLayerRowViewModel.SheetAtChoices)
        {
            var choice = surface;
            var mi = new MenuItem
            {
                Header     = SheetAtLabelConverter.Label(choice),
                ToggleType = MenuItemToggleType.Radio,
                IsChecked  = row.SelectedSheetAt == choice,
            };
            ToolTip.SetTip(mi, StackupCardText.SheetAtTip);
            mi.Click += (_, _) => row.SelectedSheetAt = choice;
            sheetItems.Add(mi);
        }
        sheet.ItemsSource = sheetItems;
        items.Add(sheet);

        items.Add(DrawingLayersItem(row));

        if (_viewModel is { } vm && row.CanAddFilm) AddConductorFilmItems(items, vm, row);
    }

    // ── Resistive films (R-stk9-8) ────────────────────────────────────────────────────────────────

    /// <summary>What asks for a film's name and R_s. Settable so the gate can answer without a window.
    /// The name is null when only the value was asked for.</summary>
    internal System.Func<string, bool, Task<(string? Name, double OhmPerSq)?>>? FilmPrompt { get; set; }

    private Task<(string? Name, double OhmPerSq)?> AskForFilm(string title, bool askName)
    {
        if (FilmPrompt is { } prompt) return prompt(title, askName);
        return TopLevel.GetTopLevel(this) is Window owner
            ? Views.Dialogs.ResistorFilmPromptDialog.AskAsync(owner, title, askName)
            : Task.FromResult<(string?, double)?>(null);
    }

    private void AddFilmItems(List<object> items, TechEditorViewModel vm, LayerKey film)
    {
        var filmRow = vm.StackupLayers.SelectMany(r => r.FilmRows).FirstOrDefault(r => r.Key == film);
        var face = new MenuItem { Header = "Face" };
        var faces = new List<object>();
        foreach (var surface in StackupFilmRowViewModel.FaceChoices)
        {
            var choice = surface;
            var mi = new MenuItem
            {
                Header     = choice == ConductorSheetSurface.Top ? "Top" : "Bottom",
                ToggleType = MenuItemToggleType.Radio,
                IsChecked  = filmRow?.SelectedFace == choice,
            };
            mi.Click += (_, _) => vm.SetFilmFace(film, choice);
            faces.Add(mi);
        }
        face.ItemsSource = faces;
        items.Add(face);

        var targets = vm.FilmMoveTargets(film);
        var move = new MenuItem { Header = "Move to", IsEnabled = targets.Count > 0 };
        var moves = new List<object>();
        foreach (string name in targets)
        {
            string target = name;
            var mi = new MenuItem { Header = target };
            mi.Click += (_, _) => vm.MoveFilm(film, target);
            moves.Add(mi);
        }
        move.ItemsSource = moves;
        items.Add(move);

        var remove = new MenuItem { Header = "Remove Film" };
        remove.Click += (_, _) => vm.RemoveFilm(film);
        items.Add(remove);
    }

    private void AddConductorFilmItems(List<object> items, TechEditorViewModel vm, StackupLayerRowViewModel row)
    {
        string conductor = row.Layer.Name;
        var choices = vm.FilmLayerChoices();
        if (choices.Count > 0)
        {
            var add = new MenuItem { Header = "Add Resistor Film" };
            var adds = new List<object>();
            var shown = Bounded(choices, _ => false);
            foreach (var (key, name) in shown)
            {
                var (k, n) = (key, name);
                var mi = new MenuItem { Header = n };
                mi.Click += async (_, _) =>
                {
                    if (await AskForFilm($"Resistor film on {conductor}: {n}", askName: false) is { } answer)
                        vm.AddFilmOnLayer(conductor, k, answer.OhmPerSq);
                };
                adds.Add(mi);
            }
            AddMoreItem(adds, row, choices.Count - shown.Count);
            add.ItemsSource = adds;
            items.Add(add);
        }

        var create = new MenuItem { Header = "New Resistor Film…" };
        create.Click += async (_, _) =>
        {
            if (await AskForFilm($"New resistor film on {conductor}", askName: true) is { Name: { } name } answer)
                vm.AddResistorFilm(conductor, name, answer.OhmPerSq);
        };
        items.Add(create);
    }

    /// <summary>The Add button on a conductor card: what the menu's own film items offer, in a flyout.</summary>
    internal List<object> FilmAddItems(StackupLayerRowViewModel row)
    {
        var items = new List<object>();
        if (_viewModel is { } vm && row.CanAddFilm) AddConductorFilmItems(items, vm, row);
        return items;
    }

    // ── Dielectric ────────────────────────────────────────────────────────────────────────────────

    private void AddDielectricItems(
        List<object> items, TechEditorViewModel vm, StackupLayerRowViewModel row)
    {
        // R-stk6-4. DISABLED WITH A REASON, never enabled then refused: a dielectric above the top
        // metal has nothing to span up to, and a via with one end unset is a broken entry the user
        // did not ask for.
        string? refusal = vm.AddViaAroundRefusal(row);
        var addVia = new MenuItem { Header = "Add Via", IsEnabled = refusal is null };
        // The refusal, and nothing when the item is enabled — see the note at the top of this file.
        if (refusal is not null) ToolTip.SetTip(addVia, refusal);
        addVia.Click += (_, _) => vm.AddViaSpanningAround(row);
        items.Add(addVia);

        var pattern = new MenuItem { Header = StackupCardText.PresentWith };
        var choices = new List<object>();
        foreach (var name in row.PresentWithChoices)
        {
            var choice = name;
            var mi = new MenuItem
            {
                Header     = choice,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked  = string.Equals(row.SelectedPresentWith, choice, System.StringComparison.Ordinal),
            };
            ToolTip.SetTip(mi, StackupCardText.PresentWithTip);
            mi.Click += (_, _) => row.SelectedPresentWith = choice;
            choices.Add(mi);
        }
        pattern.ItemsSource = choices;
        items.Add(pattern);
    }

    /// <summary>Says which two conductors the via would join, before it exists — the whole of what
    /// the item decides, and not something the user can otherwise see from a dielectric.</summary>
    private static string AddViaTip(TechEditorViewModel vm, StackupLayerRowViewModel row)
    {
        var (above, below) = vm.ConductorsAround(row.Layer);
        return $"Adds a via spanning {above} to {below} — the conductors immediately above and below "
             + "this dielectric. Its drawing layer and wall thickness are on the card below, which "
             + "the new entry is selected in.";
    }

    // ── Via: Plated, and Fill — two fields, two items, never merged (R-stk6-5) ────────────────────

    /// <summary>
    /// <c>StackupLayer.Plated</c> and <c>ViaFillKind.Plated</c> are different things with the same
    /// word, and the consequence of merging them is not cosmetic: <c>PlanarExtractor</c> turns every
    /// via entry into a conductive <c>PlanarVia</c>, so a millimetre-scale unplated hole modelled as
    /// a barrel SHORTS every layer it passes through and the run completes cleanly.
    ///
    /// <para><b>Fill is HIDDEN, not disabled, when the hole is not plated</b> — exactly as the card
    /// hides its Fill combo behind the same flag. A fill model for a hole that is not metal is not a
    /// meaningful choice.</para>
    /// </summary>
    private void AddViaItems(List<object> items, StackupLayerRowViewModel row)
    {
        bool plated = row.IsPlated;
        var hole = new MenuItem
        {
            Header     = StackupCardText.PlatedHoleMenu,
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked  = plated,
        };
        hole.Click += (_, _) => row.IsPlated = !plated;
        items.Add(hole);

        if (plated)
        {
            var fill = new MenuItem { Header = StackupCardText.Fill };
            var fills = new List<object>();
            foreach (var kind in StackupLayerRowViewModel.FillChoices)
            {
                var choice = kind;
                var mi = new MenuItem
                {
                    Header     = choice == ViaFillKind.Plated ? StackupCardText.FillPlated : StackupCardText.FillSolid,
                    ToggleType = MenuItemToggleType.Radio,
                    IsChecked  = row.SelectedFill == choice,
                };
                ToolTip.SetTip(mi, StackupCardText.FillTip);
                mi.Click += (_, _) => row.SelectedFill = choice;
                fills.Add(mi);
            }
            fill.ItemsSource = fills;
            items.Add(fill);
        }

        items.Add(DrawingLayersItem(row));
    }

    // ── Drawing layers — BOUNDED, and it says so (R-stk6-8) ───────────────────────────────────────

    /// <summary>
    /// The layer binding, as a submenu of at most <see cref="ContextMenuLayerLimit"/> items plus, when
    /// that was not all of them, one that says so and hands the job to the card's own picker.
    ///
    /// <para><b>Bound layers come first and are never truncated away</b>: a submenu that hid the very
    /// binding the user right-clicked to check would be worse than no submenu. What the limit drops is
    /// the tail of the unbound ones.</para>
    ///
    /// <para>A conductor binds one or MORE, so its items are checkboxes and each toggles
    /// independently; a via binds AT MOST ONE, so its items are radios over the same
    /// "(none)"-plus-layers list its card ComboBox shows.</para>
    /// </summary>
    private MenuItem DrawingLayersItem(StackupLayerRowViewModel row)
    {
        var parent = new MenuItem { Header = row.DrawingLayersLabel };
        var items  = new List<object>();

        if (row.AllowMultipleDrawingLayers)
        {
            var options = row.DrawingLayerOptions;
            var shown   = Bounded(options, o => o.IsChecked);
            foreach (var option in shown)
            {
                var o = option;
                bool wasChecked = o.IsChecked;
                var mi = new MenuItem
                {
                    Header     = o.Name,
                    ToggleType = MenuItemToggleType.CheckBox,
                    IsChecked  = wasChecked,
                    IsEnabled  = o.IsEnabled,
                };
                mi.Click += (_, _) => row.SetDrawingLayerChecked(o.Key, !wasChecked);
                items.Add(mi);
            }
            AddMoreItem(items, row, options.Count - shown.Count);
        }
        else
        {
            var choices = row.DrawingLayerChoices;
            var shown   = Bounded(choices, c => ReferenceEquals(c, row.SelectedDrawingLayerChoice) || c.IsNone);
            foreach (var choice in shown)
            {
                var c = choice;
                var mi = new MenuItem
                {
                    Header     = c.Name,
                    ToggleType = MenuItemToggleType.Radio,
                    IsChecked  = ReferenceEquals(row.SelectedDrawingLayerChoice, c),
                };
                mi.Click += (_, _) => row.SelectedDrawingLayerChoice = c;
                items.Add(mi);
            }
            AddMoreItem(items, row, choices.Count - shown.Count);
        }

        parent.ItemsSource = items;
        return parent;
    }

    /// <summary>Everything <paramref name="keep"/> says to keep, in list order, then as many of the
    /// rest as <see cref="ContextMenuLayerLimit"/> still allows. A technology whose bindings alone
    /// exceed the limit shows all of them and nothing else — the limit bounds the BROWSING, not the
    /// truth about what is bound.</summary>
    private static List<T> Bounded<T>(IEnumerable<T> all, System.Func<T, bool> keep)
    {
        var list = all.ToList();
        var shown = list.Where(keep).ToList();
        foreach (var item in list)
        {
            if (shown.Count >= ContextMenuLayerLimit) break;
            if (!shown.Contains(item)) shown.Add(item);
        }
        return list.Where(shown.Contains).ToList();
    }

    /// <summary>R-stk6-8's "do not silently truncate". It names the number left out and says where
    /// the rest are, and it puts the user in front of them: the entry is (re-)selected, which scrolls
    /// the card list to its card, and the card's own layer filter is cleared so the picker they land
    /// on is showing everything.</summary>
    private void AddMoreItem(List<object> items, StackupLayerRowViewModel row, int hidden)
    {
        if (hidden <= 0) return;

        items.Add(new Separator());
        var more = new MenuItem { Header = $"More… ({hidden} not shown)" };
        ToolTip.SetTip(more, $"{hidden} more drawing layers are not listed here. The full, filterable "
                           + "picker is on this entry's card below; this selects the entry and scrolls to it.");
        more.Click += (_, _) =>
        {
            row.DrawingLayerFilter = "";
            if (_viewModel is not null) _viewModel.SelectedStackupLayerName = row.Layer.Name;
        };
        items.Add(more);
    }
}
