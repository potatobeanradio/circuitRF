using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Design.Smith;
using CircuitRF.Ui.Matching;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Smith;

/// <summary>
/// One row of the Add / Insert menu: an element kind in one of its legal placements.
/// </summary>
/// <remarks>
/// <b>One list serves both menus and the shell's Insert menu</b> (<c>R-smith6-2</c>, "one command, two
/// surfaces"). Two hand-written vocabularies would drift, and the symptom of the drift is a part that
/// can be added but not inserted.
/// </remarks>
/// <param name="Kind">Which element.</param>
/// <param name="Placement">Where it goes. For a kind with a fixed placement this is that placement —
/// <see cref="SmithComponentMap.AllowedPlacement"/>'s answer, never a second one.</param>
/// <param name="Header">What the menu row says.</param>
public sealed record SmithElementMenuEntry(
    SmithElementKind Kind, SmithPlacement Placement, string Header);

/// <summary>
/// One row of the Add / Insert menus as the VIEW lays them out: either a single element to place,
/// or a submenu holding several.
/// </summary>
/// <remarks>
/// <b>The grouping is here and not in the view</b>, for the reason
/// <see cref="SmithChartViewModel.ElementMenu"/> exists at all: two surfaces build these menus and a
/// shape decided in one of them is a shape the other does not have.
///
/// <para><see cref="SmithChartViewModel.ElementMenu"/> is unchanged and still flat. It is the
/// VOCABULARY — what may be placed — and every command, test and future surface reads it; this is a
/// PRESENTATION of the same rows and holds none that the flat list does not.</para>
/// </remarks>
/// <param name="Title">The submenu's own label, or null when <paramref name="Entries"/> is a single
/// row sitting at the top level.</param>
/// <param name="GlyphKind">The kind whose symbol illustrates the row — its own, or the family's
/// archetype for a submenu.</param>
/// <param name="Entries">What the row places, or what the submenu holds.</param>
public sealed record SmithElementMenuGroup(
    string? Title, SmithElementKind GlyphKind, IReadOnlyList<SmithElementMenuEntry> Entries);

/// <summary>
/// The network strip: the projection, selection, the element operations, the sliders and the mirror
/// (<c>brief-smith-6-network-strip.md</c>; <c>docs/design/smith-chart.md</c> §5.5, §5.6).
/// </summary>
public sealed partial class SmithChartViewModel
{
    // ── The projection (R-smith6-1) ──────────────────────────────────────────

    /// <summary>The cascade as a schematic — what the strip draws and what brief 7 copies.</summary>
    public SmithNetworkProjection Network { get; private set; } = SmithNetworkProjection.Empty;

    /// <summary>Raised after <see cref="Network"/> is rebuilt — the strip's cue to redraw.</summary>
    public event Action? NetworkChanged;

    /// <summary>
    /// The drawing as it would be if the element at <paramref name="from"/> were dropped at
    /// <paramref name="to"/> — <b>the whole picture, not the dragged part of it</b>.
    /// </summary>
    /// <remarks>
    /// <b>Two owner-reported faults, one cause</b> (2026-09-19): a dragged component slid on top of
    /// the one already in that slot, and a shunt element's tap on the spine stayed behind while the
    /// part itself moved. The strip's drag preview moved the dragged COLUMN and nothing else, so
    /// everything a reorder actually changes — where the other elements sit, where the spine is
    /// drawn between them, which junctions carry a dot — was still the pre-drag drawing until the
    /// pointer came up. Overlaying two components is what "moving into the centre of the adjacent
    /// component" looks like, and a wire that is not a component is what the shunt's missing
    /// connection was.
    ///
    /// <para><b>So the preview is a real projection of the reordered list</b>, built by the ONE
    /// build. Nothing here can draw a drag differently from a drop, because what the drag shows IS
    /// what the drop produces — and a second, partial preview drawing is exactly what produced both
    /// reports.</para>
    ///
    /// <para><b>The document is not edited.</b> The list is put back before this returns, under a
    /// <c>finally</c>: no undo entry, no dirty mark, no event. <see cref="MoveElement"/> is still the
    /// only thing that reorders anything, and it runs once, on release. The reorder is done on the
    /// live list rather than on a copy because <see cref="SmithNetworkModel.Build"/> reads the
    /// elements' own objects — their values, names, enabled state and file references — and a
    /// shallow copy of the design would have to reproduce every one of them to project identically.
    /// </para>
    /// </remarks>
    public SmithNetworkProjection BuildReorderPreview(int from, int to)
    {
        int count = _design.Elements.Count;
        if (from < 0 || from >= count) return Network;

        int target = Math.Clamp(to, 0, count - 1);
        if (target == from) return Network;

        var moved = _design.Elements[from];
        _design.Elements.RemoveAt(from);
        _design.Elements.Insert(target, moved);
        try
        {
            return SmithNetworkModel.Build(_design, DocumentDirectory);
        }
        finally
        {
            _design.Elements.RemoveAt(target);
            _design.Elements.Insert(from, moved);
        }
    }

    /// <summary>
    /// Re-projects the cascade. Called from <c>RefreshDerived</c>, so the picture, the chart and the
    /// status strip are always three views of ONE evaluation of one design.
    /// </summary>
    private void RebuildNetwork()
    {
        Network = SmithNetworkModel.Build(_design, DocumentDirectory);

        // Selection is kept by NAME and not by index, for the reason the generator table keeps its own
        // by frequency: every committed edit and every undo replaces the whole design, and a reorder
        // moves an element to a different index without changing what the user selected. The index is
        // the fallback for the one case a name cannot cover — a deletion, where the name is gone and
        // what the user is left looking at is the position.
        int index = _selectedElementName is { Length: > 0 } name
            ? IndexOfName(name)
            : -1;

        if (index < 0) index = Math.Min(_selectedElementIndex, _design.Elements.Count - 1);

        _selectedElementIndex = index;
        _selectedElementName  = index >= 0 ? _design.Elements[index].Name : null;

        RebuildSliderRows();
        NotifySelection();
        NetworkChanged?.Invoke();
    }

    private int IndexOfName(string name)
    {
        for (int i = 0; i < _design.Elements.Count; i++)
            if (string.Equals(_design.Elements[i].Name, name, StringComparison.Ordinal))
                return i;
        return -1;
    }

    /// <summary>The element at <paramref name="index"/>, or null — what the slider rows read through,
    /// because they hold an index into a design object that is replaced on every edit.</summary>
    internal SmithElement? ElementAt(int index)
        => index >= 0 && index < _design.Elements.Count ? _design.Elements[index] : null;

    // ── Selection (R-smith6-2) ───────────────────────────────────────────────

    private int     _selectedElementIndex = -1;
    private string? _selectedElementName;

    /// <summary>Which element the sliders and Delete act on, or −1.</summary>
    public int SelectedElementIndex => _selectedElementIndex;

    /// <summary>That element, or null.</summary>
    public SmithElement? SelectedElement => ElementAt(_selectedElementIndex);

    /// <summary>True when there is one.</summary>
    public bool HasSelectedElement => SelectedElement is not null;

    /// <summary>
    /// The drawn component the selection outlines — what the strip hands the renderer's overlay.
    /// </summary>
    /// <remarks>
    /// <b>The outline is <c>SchematicOverlay.SelectedComponentIds</c>'s</b>, which the schematic editor
    /// already draws. This tool adds no selection rendering of its own, exactly as it adds no renderer
    /// of its own; what it has instead of a selection MODEL is this one integer.
    /// </remarks>
    public string? SelectedComponentId
        => Network.ElementIndexByComponentId
                  .FirstOrDefault(kv => kv.Value == _selectedElementIndex).Key;

    /// <summary>Selects element <paramref name="index"/>, or clears the selection with −1.</summary>
    /// <remarks>
    /// <b>The chart follows the strip</b> (owner instruction, 2026-09-19): the selected element's
    /// trajectory is drawn thicker and the other trajectories fade back, so clicking a component
    /// answers "which of these curves is this part" — and deselecting puts every one of them back.
    /// <see cref="SmithPlotBuilder.ApplyElementHighlight"/> says why that is a mutation of the
    /// traces on the plot rather than a rebuild of them.
    /// </remarks>
    public void SelectElement(int index)
    {
        int next = index >= 0 && index < _design.Elements.Count ? index : -1;
        if (next == _selectedElementIndex) return;

        _selectedElementIndex = next;
        _selectedElementName  = next >= 0 ? _design.Elements[next].Name : null;

        HighlightSelectedTrace();
        RebuildSliderRows();
        NotifySelection();
        NetworkChanged?.Invoke();
    }

    /// <summary>
    /// Selects whatever element a drawn component belongs to.
    /// </summary>
    /// <remarks>
    /// <b>A click on the generator, the load marker or a ground selects NOTHING and clears nothing</b>:
    /// they are parts of the drawing that are not elements, and either interpretation of a click on one
    /// — select the neighbour, or drop the selection — is a guess. Returning false lets the canvas
    /// treat it as a click on empty strip.
    /// </remarks>
    public bool SelectByComponentId(string? componentId)
    {
        if (componentId is null) return false;
        if (!Network.ElementIndexByComponentId.TryGetValue(componentId, out int index)) return false;

        SelectElement(index);
        return true;
    }

    /// <summary>
    /// Puts the selection emphasis on the chart's trajectories and asks for a redraw.
    /// </summary>
    /// <remarks>
    /// Null-guarded on the container because <see cref="SelectElement"/> is reachable during
    /// construction — <c>RebuildRows</c> runs before the chart host exists — and a selection made
    /// then is re-applied by the first <c>RebuildChart</c> anyway.
    /// </remarks>
    private void HighlightSelectedTrace()
    {
        if (ChartContainer is null) return;

        SmithPlotBuilder.ApplyElementHighlight(_traceKeys, _selectedElementIndex);
        ChartContainer.RequestPlotRedraw();
    }

    private void NotifySelection()
    {
        OnPropertyChanged(nameof(SelectedElementIndex));
        OnPropertyChanged(nameof(SelectedElement));
        OnPropertyChanged(nameof(HasSelectedElement));
        OnPropertyChanged(nameof(SelectedComponentId));
        OnPropertyChanged(nameof(SelectedElementHeader));
        OnPropertyChanged(nameof(SelectedElementNameEntry));
        OnPropertyChanged(nameof(SelectedElementEnabled));
        OnPropertyChanged(nameof(SelectedFileSummary));
        OnPropertyChanged(nameof(HasFileSummary));
        DeleteElementCommand.NotifyCanExecuteChanged();
        MoveElementTowardGeneratorCommand.NotifyCanExecuteChanged();
        MoveElementTowardLoadCommand.NotifyCanExecuteChanged();
        MoveElementLeftCommand.NotifyCanExecuteChanged();
        MoveElementRightCommand.NotifyCanExecuteChanged();
        InsertElementCommand.NotifyCanExecuteChanged();
    }

    // ── The vocabulary, written down once (R-smith6-2) ───────────────────────

    /// <summary>
    /// Every element in every placement it is legal in — the Add menu, the Insert menu and the shell's
    /// own Insert menu, from one list.
    /// </summary>
    public static IReadOnlyList<SmithElementMenuEntry> ElementMenu { get; } = BuildElementMenu();

    private static IReadOnlyList<SmithElementMenuEntry> BuildElementMenu()
    {
        var rows = new List<SmithElementMenuEntry>();

        foreach (var kind in SmithComponentMap.AllKinds)
        {
            if (SmithComponentMap.AllowedPlacement(kind) is { } only)
            {
                rows.Add(new SmithElementMenuEntry(kind, only, Header(kind, only)));
                continue;
            }

            rows.Add(new SmithElementMenuEntry(kind, SmithPlacement.Series,
                                               Header(kind, SmithPlacement.Series)));
            rows.Add(new SmithElementMenuEntry(kind, SmithPlacement.Shunt,
                                               Header(kind, SmithPlacement.Shunt)));
        }

        return rows;

        static string Header(SmithElementKind kind, SmithPlacement placement)
            => $"{(placement == SmithPlacement.Series ? "Series" : "Shunt")} {DisplayName(kind)}";
    }

    /// <summary>The label of the one submenu — the eight-member RLC family (owner, 2026-09-20).</summary>
    public const string RlcGroupTitle = "RLC";

    /// <summary>
    /// <see cref="ElementMenu"/> as the menus draw it: the RLC family under ONE submenu, everything
    /// else at the top level, all of it in the flat list's own order.
    /// </summary>
    /// <remarks>
    /// <b>The family went behind a submenu because the flat menu outgrew the window</b> (owner,
    /// 2026-09-20). Each row is a 39 px glyph and the vocabulary is 17 kinds in up to two placements
    /// each, so a flat flyout is 30 rows — about 1,350 px against a window 741 px tall. It already
    /// scrolled at 18; adding the six two-element RLC members is what made the scroll the normal way
    /// to reach the bottom half of the list rather than an edge case.
    ///
    /// <para><b>The eight that moved are the ones a reader picks BETWEEN</b>, which is what makes
    /// this a grouping rather than a hiding place: SRLC, SRL, SRC, SLC and their four parallel duals
    /// are one part with a different subset of R, L and C in it, and choosing among them is a
    /// second question after "I want a lumped combination". The three SINGLE parts stay at the top
    /// level, because reaching for an L is not that question.</para>
    ///
    /// <para>The submenu sits where the first of its members sat in the flat order, so nothing else
    /// moves relative to anything it was already beside.</para>
    /// </remarks>
    public static IReadOnlyList<SmithElementMenuGroup> ElementMenuGroups { get; } = BuildElementMenuGroups();

    private static IReadOnlyList<SmithElementMenuGroup> BuildElementMenuGroups()
    {
        var groups = new List<SmithElementMenuGroup>();
        var family = new List<SmithElementMenuEntry>();
        int at     = -1;

        foreach (var entry in ElementMenu)
        {
            // The family is asked, not listed — SmithComponentMap already knows which kinds are one
            // part holding two or three of R, L and C, and a list here would be the ninth member's
            // chance to be left out of the group and appear alone among the lines.
            if (SmithComponentMap.RlcElementsOf(entry.Kind) is not null)
            {
                if (at < 0) at = groups.Count;
                family.Add(entry);
                continue;
            }

            groups.Add(new SmithElementMenuGroup(null, entry.Kind, [entry]));
        }

        if (family.Count > 0)
            groups.Insert(at, new SmithElementMenuGroup(RlcGroupTitle, SmithElementKind.Srlc, family));

        return groups;
    }

    /// <summary>
    /// What this tool calls one element kind.
    /// </summary>
    /// <remarks>
    /// <b>The §3.3 table's own spellings, which are not always the component registry's.</b> A shorted
    /// stub and an open stub and a TLIN are one <c>SymbolKind</c> told apart by how their far end is
    /// wired, so <c>ComponentTypeRegistry.DisplayName</c> answers "TLIN" for all three — right on a
    /// schematic label, where the wiring is visible, and useless in a menu where it is the only thing
    /// being chosen.
    /// </remarks>
    public static string DisplayName(SmithElementKind kind) => kind switch
    {
        SmithElementKind.R           => "R",
        SmithElementKind.L           => "L",
        SmithElementKind.C           => "C",
        SmithElementKind.Srlc        => "SRLC",
        SmithElementKind.Prlc        => "PRLC",
        SmithElementKind.Srl         => "SRL",
        SmithElementKind.Src         => "SRC",
        SmithElementKind.Slc         => "SLC",
        SmithElementKind.Prl         => "PRL",
        SmithElementKind.Prc         => "PRC",
        SmithElementKind.Plc         => "PLC",
        SmithElementKind.Z1P         => "Z1P",
        SmithElementKind.S1P         => "S1P",
        SmithElementKind.S2P         => "S2P",
        SmithElementKind.Tline       => "TLIN",
        SmithElementKind.StubOpen    => "open stub",
        SmithElementKind.StubShorted => "shorted stub",
        _                            => kind.ToString(),
    };

    // ── Add / Insert (R-smith6-2, R-smith6-3) ────────────────────────────────

    /// <summary>
    /// How a file element gets its file at placement time. Set by the view; null in a headless caller.
    /// </summary>
    /// <remarks>
    /// <b>The picker is the VIEW's and the decision is not.</b> An <c>S1P</c>/<c>S2P</c> opens one on
    /// placement and <b>an element placed with no file is refused rather than created</b>
    /// (<c>R-smith6-3</c>) — a file-less file element has nothing to evaluate, and creating one would
    /// put a refusal in the status strip about a part the user never finished asking for.
    /// </remarks>
    public Func<SmithElementKind, Task<string?>>? TouchstoneFileChooser { get; set; }

    /// <summary><b>Add</b> — appends at the end, nearest the load.</summary>
    [RelayCommand]
    private Task AddElement(SmithElementMenuEntry? entry)
        => PlaceElement(entry, _design.Elements.Count, "Add");

    private bool CanInsertElement() => true;

    /// <summary>
    /// <b>Insert</b> — places BEFORE the selected element, or appends when nothing is selected.
    /// </summary>
    /// <remarks>
    /// Appending on an empty selection rather than refusing: "before nothing" and "at the end" are the
    /// same place in a list, and a menu row that did nothing at all would read as broken.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanInsertElement))]
    private Task InsertElement(SmithElementMenuEntry? entry)
        => PlaceElement(entry,
                        _selectedElementIndex >= 0 ? _selectedElementIndex : _design.Elements.Count,
                        "Insert");

    private async Task PlaceElement(SmithElementMenuEntry? entry, int at, string verb)
    {
        if (entry is null) return;

        string? file = null;
        if (SmithComponentMap.UsesFile(entry.Kind))
        {
            file = TouchstoneFileChooser is null ? null : await TouchstoneFileChooser(entry.Kind);

            if (string.IsNullOrWhiteSpace(file))
            {
                StripNotice = $"Nothing was placed — a {DisplayName(entry.Kind)} IS its file, and "
                            + "there is nothing for one with no file to evaluate.";
                return;
            }
        }

        var element = SmithElementFactory.Create(
            entry.Kind, entry.Placement, _design.DesignFrequencyHz,
            _design.Elements.Select(e => e.Name));
        element.FileRef = file;

        int index = Math.Clamp(at, 0, _design.Elements.Count);

        Edit($"{verb} {DisplayName(entry.Kind)}", () => _design.Elements.Insert(index, element));
        SelectElement(index);
    }

    // ── Delete and reorder (R-smith6-2) ──────────────────────────────────────

    private bool CanActOnSelection() => SelectedElement is not null;

    /// <summary><b>Delete</b> — removes the selected element; the chain closes up.</summary>
    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private void DeleteElement()
    {
        int i = _selectedElementIndex;
        if (ElementAt(i) is not { } e) return;

        // The name goes with the element, so the NEXT rebuild has to fall back to the index — which is
        // what leaves the selection on whatever slid into the gap, and is what a user deleting a run of
        // elements wants.
        _selectedElementName = null;

        Edit($"Delete {e.Name}", () => _design.Elements.RemoveAt(i));
    }

    private bool CanMoveTowardGenerator() => _selectedElementIndex > 0;
    private bool CanMoveTowardLoad()
        => _selectedElementIndex >= 0 && _selectedElementIndex < _design.Elements.Count - 1;

    /// <summary>One step toward the generator (one index down).</summary>
    [RelayCommand(CanExecute = nameof(CanMoveTowardGenerator))]
    private void MoveElementTowardGenerator() => MoveElement(_selectedElementIndex, _selectedElementIndex - 1);

    /// <summary>One step toward the load (one index up).</summary>
    [RelayCommand(CanExecute = nameof(CanMoveTowardLoad))]
    private void MoveElementTowardLoad() => MoveElement(_selectedElementIndex, _selectedElementIndex + 1);

    private bool CanMoveLeft()  => MirrorNetwork ? CanMoveTowardLoad() : CanMoveTowardGenerator();
    private bool CanMoveRight() => MirrorNetwork ? CanMoveTowardGenerator() : CanMoveTowardLoad();

    /// <summary>
    /// The ⇅ buttons, which are <b>drawing-relative and not list-relative</b>.
    /// </summary>
    /// <remarks>
    /// A left-pointing button beside a drawing moves the part LEFT, and in a mirrored strip that is
    /// toward the load rather than toward the generator. Binding the glyph to the list's own direction
    /// instead would make the two buttons swap meanings the moment the mirror was pressed — silently,
    /// and while their arrows went on pointing the other way.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanMoveLeft))]
    private void MoveElementLeft()
    {
        if (MirrorNetwork) MoveElementTowardLoad(); else MoveElementTowardGenerator();
    }

    /// <inheritdoc cref="MoveElementLeft"/>
    [RelayCommand(CanExecute = nameof(CanMoveRight))]
    private void MoveElementRight()
    {
        if (MirrorNetwork) MoveElementTowardGenerator(); else MoveElementTowardLoad();
    }

    /// <summary>
    /// Moves the element at <paramref name="from"/> to <paramref name="to"/> — the ⇅ buttons and the
    /// reorder drag, in one place and as one undo entry.
    /// </summary>
    /// <remarks>
    /// <b>The element object is MOVED, not rebuilt.</b> Its values, its name, its enabled state, its
    /// active parameter and its stored slider ranges all travel with it; a reorder that re-created the
    /// element from its kind would silently reset every one of them, and the only visible symptom would
    /// be a number that changed when nothing about the part did.
    /// </remarks>
    public void MoveElement(int from, int to)
    {
        if (ElementAt(from) is not { } e) return;

        int target = Math.Clamp(to, 0, _design.Elements.Count - 1);
        if (target == from) return;

        // Follow the element, not the slot: after the move the user is still looking at the same part.
        _selectedElementName = e.Name;

        Edit($"Move {e.Name}", () =>
        {
            _design.Elements.RemoveAt(from);
            _design.Elements.Insert(target, e);
        });

        SelectElement(IndexOfName(e.Name));
    }

    // ── The selected element's own row (R-smith6-2) ──────────────────────────

    /// <summary>What the selected element IS — its kind and its placement, for the strip's header.</summary>
    public string SelectedElementHeader
        => SelectedElement is not { } e
               ? "Nothing selected"
               : $"{(e.Placement == SmithPlacement.Series ? "Series" : "Shunt")} {DisplayName(e.Kind)}";

    /// <summary>
    /// The instance name — editable, and <b>unique</b>.
    /// </summary>
    /// <remarks>
    /// A name that collides with another element's is REFUSED rather than de-duplicated, and the
    /// sentence names both. A silent rename would mean the name the user typed and the name the tool
    /// uses disagree — and the name is what every refusal, every undo entry and brief 7's copied
    /// schematic says about the part.
    /// </remarks>
    public string SelectedElementNameEntry
    {
        get => SelectedElement?.Name ?? "";
        set
        {
            if (SelectedElement is not { } e) return;

            string name = (value ?? "").Trim();
            if (name.Length == 0 || string.Equals(name, e.Name, StringComparison.Ordinal))
            {
                OnPropertyChanged();
                return;
            }

            if (_design.Elements.Any(o => !ReferenceEquals(o, e)
                                       && string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                StripNotice = $"'{name}' is already the name of another element — an element's name is "
                            + "what every refusal, every slider and every undo entry says about it, so "
                            + "two of them cannot share one.";
                OnPropertyChanged();
                return;
            }

            _selectedElementName = name;
            Edit($"Rename {e.Name} to {name}", () => e.Name = name);
        }
    }

    /// <summary>
    /// The per-element Enabled checkbox.
    /// </summary>
    /// <remarks>
    /// <b>A disabled element is not a deleted one</b> (<c>R-smith6-2</c>): it contributes nothing, draws
    /// no trajectory, and <b>keeps its values and its place</b>. It costs one boolean and it is the
    /// difference between trying something and losing it.
    /// </remarks>
    public bool SelectedElementEnabled
    {
        get => SelectedElement?.Enabled ?? false;
        set
        {
            if (SelectedElement is not { } e || e.Enabled == value) { OnPropertyChanged(); return; }
            Edit($"{(value ? "Enable" : "Disable")} {e.Name}", () => e.Enabled = value);
        }
    }

    /// <summary>
    /// What an <c>S1P</c>/<c>S2P</c> shows instead of sliders — its file, its port count and its
    /// frequency span (<c>R-smith6-4</c>), or null for every other kind.
    /// </summary>
    /// <remarks>
    /// <b>The read is <see cref="SmithCascade.FileSummary"/>'s</b>, below the firewall, so the path is
    /// resolved by the rule the evaluator resolves it by. A refusal is shown here verbatim rather than
    /// swallowed: a file that has moved is exactly what this panel exists to make visible.
    /// </remarks>
    public string? SelectedFileSummary
    {
        get
        {
            if (SelectedElement is not { } e || !SmithComponentMap.UsesFile(e.Kind)) return null;

            try
            {
                var (path, ports, minHz, maxHz) = SmithCascade.FileSummary(e, DocumentDirectory);
                string span = $"{Hz(minHz)} … {Hz(maxHz)}";
                return $"{System.IO.Path.GetFileName(path)} · {ports}-port · {span}";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }

            static string Hz(double f) => MatchValueFormat.FormatWithUnit(
                f, MatchQuantity.Frequency, MatchValueFormat.AutoUnit, 4);
        }
    }

    /// <summary>True when <see cref="SelectedFileSummary"/> has something to show.</summary>
    public bool HasFileSummary => SelectedFileSummary is not null;

    // ── The sliders (R-smith6-4, R-smith6-5) ─────────────────────────────────

    /// <summary>
    /// One row per settable parameter of the selected element — <b>and none at all for an <c>S1P</c> or
    /// an <c>S2P</c></b>, whose value is a file.
    /// </summary>
    /// <remarks>
    /// The list is <see cref="SmithComponentMap.Parameters"/>'s, in its order. That table is also what
    /// the evaluator, the gripper inverse and brief 7's copy read, so a kind whose parameters changed
    /// could not grow a slider here without growing one everywhere.
    /// </remarks>
    public ObservableCollection<SmithSliderRowViewModel> SliderRows { get; } = [];

    private void RebuildSliderRows()
    {
        var wanted = SelectedElement is { } e
            ? SmithComponentMap.Parameters(e.Kind)
            : [];

        // Rebuilt only when the SHAPE changes — a value edit, a drag and an undo all keep the same
        // rows and only re-read them. Replacing the collection on every keystroke would close an
        // InlineEditText the user is still typing into.
        bool same = SliderRows.Count == wanted.Count
                 && SliderRows.All(r => r.ElementIndex == _selectedElementIndex)
                 && SliderRows.Select(r => r.Parameter).SequenceEqual(wanted);

        if (!same)
        {
            SliderRows.Clear();
            foreach (var p in wanted)
                SliderRows.Add(new SmithSliderRowViewModel(this, _selectedElementIndex, p));
        }

        foreach (var row in SliderRows) row.NotifyAll();
    }

    /// <summary>
    /// Writes one parameter of one element.
    /// </summary>
    /// <remarks>
    /// <b>The last-touched slider becomes the element's <see cref="SmithElement.ActiveParameter"/></b>
    /// (<c>R-smith6-5</c>) — which is what brief 5's gripper drags. Set HERE rather than in the row, so
    /// a typed value and a dragged one agree about what "touched" means, and so it lands inside the
    /// same undo entry as the value it belongs to.
    ///
    /// <para><b>A write DURING a drag pushes nothing.</b> <see cref="BeginSliderDrag"/> captured the
    /// before-state and <see cref="EndSliderDrag"/> pushes the one entry; every pointer move in between
    /// mutates and refreshes. That is the gripper drag's own contract and it exists for the same
    /// reason — the Match Designer's coercing write-back turned each undo into a new entry, so eight
    /// edits took fourteen undos to unwind.</para>
    /// </remarks>
    internal void SetElementValue(int elementIndex, SmithParameter p, double value, string description)
    {
        if (ElementAt(elementIndex) is not { } e) return;
        if (!double.IsFinite(value)) return;

        // THE DISCRETE-VALUE DOOR (§5.6a). One of two — the other is DragGripperTo — and both are
        // here rather than inside SmithInverse.Apply, which is below the firewall and has no view of
        // the per-user ladders. A typed value is snapped along with a dragged one on purpose: the
        // toggle says the design holds buyable values, and a typed 2.37 nH that stayed would make
        // that false with no symptom but the number itself.
        value = SnapIfEnabled(p, value);

        if (_sliderDragBefore is not null)
        {
            e.ActiveParameter = p;
            SmithInverse.Apply(e, p, value);
            RefreshDerived();
            return;
        }

        Edit($"{description} on {e.Name}", () =>
        {
            e.ActiveParameter = p;
            SmithInverse.Apply(e, p, value);
        });
    }

    /// <summary>Stores one row's range in the document — one undo entry, because a range is part of
    /// the design.</summary>
    internal void SetSliderRange(int elementIndex, SmithParameter p, double min, double max)
    {
        if (ElementAt(elementIndex) is not { } e) return;

        Edit($"Set {SmithSliderRowViewModel.LabelFor(p)} range on {e.Name}",
             () => e.SliderRange[p] = new SmithSliderRange(min, max));
    }

    /// <summary>
    /// Makes one row's parameter the element's active one without changing its value — what clicking a
    /// slider row's label does (<c>R-smith6-5</c>).
    /// </summary>
    [RelayCommand]
    private void MakeParameterActive(SmithSliderRowViewModel? row)
    {
        if (row is null || ElementAt(row.ElementIndex) is not { } e) return;
        if (SmithComponentMap.ActiveParameterOf(e) == row.Parameter) return;

        Edit($"Set {e.Name}'s gripper to {SmithSliderRowViewModel.LabelFor(row.Parameter)}",
             () => e.ActiveParameter = row.Parameter);
    }

    // ── One drag is one undo entry (R-smith6-4) ──────────────────────────────

    private string? _sliderDragBefore;
    private string  _sliderDragDescription = "";

    /// <summary>True while a slider is being dragged.</summary>
    public bool IsDraggingSlider => _sliderDragBefore is not null;

    /// <summary>
    /// A slider was pressed. The before-value is captured HERE and the undo entry is pushed on release.
    /// </summary>
    public void BeginSliderDrag()
    {
        if (_sliderDragBefore is not null) EndSliderDrag();

        _sliderDragBefore      = SmithDesignIo.SerializeUnvalidated(_design);
        _sliderDragDescription = SelectedElement is { } e ? $"Drag {e.Name}" : "Drag slider";
    }

    /// <summary>
    /// The drag finished — <b>one undo entry</b>, carrying the state captured on press.
    /// </summary>
    /// <remarks>
    /// <b>A drag that changed nothing pushes nothing</b>: a press and release on a thumb is a click,
    /// and a stack full of no-ops is the Match Designer's defect by a slower route.
    /// </remarks>
    public void EndSliderDrag()
    {
        if (_sliderDragBefore is not { } before) return;
        _sliderDragBefore = null;

        string after = SmithDesignIo.SerializeUnvalidated(_design);
        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            RefreshDerived();
            return;
        }

        UndoRedo.Execute(new SmithSnapshotCommand(this, before, after, _sliderDragDescription));
    }

    // ── The mirror (R-smith6-6) ──────────────────────────────────────────────

    /// <summary>
    /// Whether the drawing runs generator-on-the-right.
    /// </summary>
    /// <remarks>
    /// <b>Exposed as a PROPERTY and not a private field on the view</b>, because brief 7 reads it twice
    /// — the copy follows the mirror, and the paste's geometric end-rule reads it to decide which end
    /// of an un-ported selection is the generator.
    /// </remarks>
    public bool MirrorNetwork => _design.View.MirrorNetwork;

    /// <summary>
    /// The mirror button — <b>one undo entry, and it mirrors the drawing and nothing else</b>.
    /// </summary>
    /// <remarks>
    /// <b>The topology does not change.</b> Element 0 is still the one nearest the generator, the list
    /// order is untouched, and brief 2's recurrence runs exactly as before; the flag reaches only the
    /// projection, where it negates the direction the x-cursor advances and turns each symbol round
    /// with its position (<see cref="SmithNetworkModel"/>).
    ///
    /// <para><b>The chart does not mirror and there is no button offering to.</b> The Γ plane's
    /// orientation is fixed by physics — inductive above the real axis, capacitive below — and a
    /// mirrored Smith chart is a wrong one.</para>
    ///
    /// <para><b>It is a view setting that IS an edit.</b> Unlike a splitter or a pan, it is undoable and
    /// it marks the document dirty — the Data Display's own precedent that a persisted view change is
    /// undoable (<c>PushAxesWindowChange</c>). A user who flips it by accident takes it back with the
    /// key they already use.</para>
    /// </remarks>
    [RelayCommand]
    private void ToggleMirror()
    {
        Edit(_design.View.MirrorNetwork ? "Unmirror network" : "Mirror network",
             () => _design.View.MirrorNetwork = !_design.View.MirrorNetwork);

        OnPropertyChanged(nameof(MirrorNetwork));
    }

    // ── The strip's own sentence ─────────────────────────────────────────────

    /// <summary>
    /// A NOTE the status strip shows when there is no refusal — what the tool decided, rather than what
    /// is wrong.
    /// </summary>
    /// <remarks>
    /// It replaces the reading rather than joining it, on §5.3's own rule: two rows of text read as two
    /// different problems. Cleared by the next placement, the next design-frequency edit, or a click.
    /// </remarks>
    public string? StripNotice
    {
        get => _stripNotice;
        set
        {
            if (_stripNotice == value) return;
            _stripNotice = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasStripNotice));
            OnPropertyChanged(nameof(ShowStatusLine));
        }
    }
    private string? _stripNotice;

    /// <summary>True when <see cref="StripNotice"/> has something to say and nothing is refused.</summary>
    public bool HasStripNotice => _stripNotice is { Length: > 0 } && !HasRefusal;

    /// <summary>True when the strip shows its READING — the design frequency, the load, Γ, VSWR and the
    /// mismatch — rather than a refusal or a note. The three are mutually exclusive by construction, so
    /// the strip is one line and never two.</summary>
    public bool ShowStatusLine => !HasRefusal && !HasStripNotice;

    /// <summary>
    /// Says, <b>once</b>, that a line's reference frequency did not follow the design frequency
    /// (<c>R-smith6-3</c>).
    /// </summary>
    /// <remarks>
    /// <b>Once per document, not once per edit.</b> The behaviour is deliberate and permanent — a line
    /// whose <c>F_ref</c> moved under the chart would be a different physical line every time it was
    /// retuned — so the note is a thing to learn rather than a thing to keep being told. Repeating it on
    /// every frequency edit would make the strip's one line useless for everything else.
    /// </remarks>
    private void NoteStrandedReferenceFrequencies()
    {
        if (_fRefNoticeShown) return;

        double f = _design.DesignFrequencyHz;
        var stranded = _design.Elements
            .Where(e => SmithComponentMap.IsLine(e.Kind)
                     && e.Values.ReferenceFrequencyHz > 0
                     && Math.Abs(e.Values.ReferenceFrequencyHz - f) > 1e-9 * Math.Max(f, 1.0))
            .Select(e => e.Name)
            .ToList();

        if (stranded.Count == 0) return;

        _fRefNoticeShown = true;
        StripNotice =
            $"{string.Join(", ", stranded)} still quote{(stranded.Count == 1 ? "s" : "")} an electrical "
          + "length at the reference frequency in force when placed — a line's F_ref does not follow the "
          + "design frequency, because one that did would be a different physical line each time you "
          + "retuned. Edit F on the element to change it.";
    }

    private bool _fRefNoticeShown;
}
