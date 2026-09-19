// Which of the window's four panels are on screen (owner, 2026-09-19).
//
// ── DOCUMENT STATE, ON THE OWNER'S OWN INSTRUCTION ────────────────────────────────────────────
//
// These four live in the `.crail` (RailPanels), not in the session. It is the one piece of pure
// VIEW state the document carries, and the reason it earns its place is that the question it
// answers is about a BOARD rather than about a sitting: a reader who gives the artwork the whole
// window to review one board wants it that way again the next time they open THAT board, not the
// next time they open any of them. Absent from the file means all four shown, so every `.crail`
// written before this — and every one nobody has collapsed a panel in — opens exactly as it did.
//
// So there is no backing field here. Each property is a window onto `_document.Panels`, which is
// the shape every other document-backed setting in this view model already takes (ViaPlatingEntry,
// TemperatureEntry): one copy of the fact, and SetDocument re-announces it rather than migrating it.
//
// ── THE SPECIFICATION PANEL NEVER EXPANDS ─────────────────────────────────────────────────────
//
// It holds a rail selector, two short lists and a target; it is legible at 300 px and gains nothing
// from more. So its column is fixed-or-gone, and the space it releases goes to the three panels
// that DO grow with it — which is why `ShowSpecification` appears in no predicate below.

using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.RailRf;

public sealed partial class RailRfViewModel
{
    /// <summary>The specification column. <b>Fixed-width or gone</b> — see the file header.</summary>
    public bool ShowSpecification
    {
        get => _document.Panels.ShowSpecification;
        set => SetPanel(v => _document.Panels.ShowSpecification = v, ShowSpecification, value);
    }

    /// <summary>The board artwork and the map strip above it.</summary>
    public bool ShowBoard
    {
        get => _document.Panels.ShowBoard;
        set => SetPanel(v => _document.Panels.ShowBoard = v, ShowBoard, value);
    }

    /// <summary>The parts table under the board.</summary>
    public bool ShowParts
    {
        get => _document.Panels.ShowParts;
        set => SetPanel(v => _document.Panels.ShowParts = v, ShowParts, value);
    }

    /// <summary>The results column.</summary>
    public bool ShowResults
    {
        get => _document.Panels.ShowResults;
        set => SetPanel(v => _document.Panels.ShowResults = v, ShowResults, value);
    }

    /// <summary>
    /// Whether the centre column is drawn at all — the board and the parts table SHARE it.
    /// </summary>
    /// <remarks>
    /// With both off the column collapses and the results column takes the width, which is the one
    /// case where results grows sideways rather than just taller.
    /// </remarks>
    public bool ShowBoardColumn => ShowBoard || ShowParts;

    /// <summary>Parts on its own in the centre column, so it takes the height the board released
    /// instead of staying at its capped 170 px and leaving the column empty below it.</summary>
    public bool PartsFillsBoardColumn => ShowParts && !ShowBoard;

    // ── AT LEAST ONE PANEL IS ALWAYS SHOWING (owner, 2026-09-19) ─────────────────────────────
    //
    // Four panels all hidden is a window with a title bar, a toolbar, a status strip and nothing
    // between them — a state with no content, reachable in four clicks and escapable only by
    // recognising four dark buttons as the way out. So the LAST lit button is DISABLED rather than
    // inert: a control that can be pressed and does nothing is indistinguishable from one that is
    // broken, which is the line this toolbar's own Report button already draws. Each tooltip says
    // why.
    //
    // It is a CanExecute rather than a guard inside the setter, so the button dims by itself — the
    // Command binding does that — and nothing has to remember to re-assert an IsEnabled.

    private int ShownPanelCount =>
        (ShowSpecification ? 1 : 0) + (ShowBoard ? 1 : 0) + (ShowParts ? 1 : 0) + (ShowResults ? 1 : 0);

    /// <summary>Whether a panel may be toggled: always when it is hidden, and when it is showing
    /// only while something else is too.</summary>
    private bool CanToggle(bool shown) => !shown || ShownPanelCount > 1;

    private bool CanToggleSpecification => CanToggle(ShowSpecification);
    private bool CanToggleBoard         => CanToggle(ShowBoard);
    private bool CanToggleParts         => CanToggle(ShowParts);
    private bool CanToggleResults       => CanToggle(ShowResults);

    [RelayCommand(CanExecute = nameof(CanToggleSpecification))]
    private void ToggleSpecification() => ShowSpecification = !ShowSpecification;

    [RelayCommand(CanExecute = nameof(CanToggleBoard))]
    private void ToggleBoard() => ShowBoard = !ShowBoard;

    [RelayCommand(CanExecute = nameof(CanToggleParts))]
    private void ToggleParts() => ShowParts = !ShowParts;

    [RelayCommand(CanExecute = nameof(CanToggleResults))]
    private void ToggleResults() => ShowResults = !ShowResults;

    /// <summary>Re-asks all four whether they may be pressed. <b>All four, on any change</b> —
    /// hiding one panel is what makes another one the last.</summary>
    private void RefreshPanelCommands()
    {
        ToggleSpecificationCommand.NotifyCanExecuteChanged();
        ToggleBoardCommand.NotifyCanExecuteChanged();
        TogglePartsCommand.NotifyCanExecuteChanged();
        ToggleResultsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Writes one panel through to the document and announces it — <b>and the two derived
    /// predicates with it</b>, which a computed property cannot do for itself.</summary>
    private void SetPanel(Action<bool> write, bool current, bool value,
                          [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (current == value) return;

        // The invariant, defended at the WRITE as well as at the button — a caller that is not the
        // button (a test, a future menu item) must not be able to empty the window either.
        if (!value && ShownPanelCount <= 1) return;

        write(value);
        OnPropertyChanged(name);
        RefreshPanelCommands();
        // These are document state and they queue no solve, so the funnel in QueueResolve does not
        // see them — the mark has to be refreshed here or hiding a panel would look free.
        RefreshDirty();
        OnPropertyChanged(nameof(ShowBoardColumn));
        OnPropertyChanged(nameof(PartsFillsBoardColumn));
    }

    /// <summary>
    /// Re-reads all four off the document — what <see cref="SetDocument"/> calls, because opening a
    /// second <c>.crail</c> changes every one of them and nothing else would say so.
    /// </summary>
    /// <remarks>
    /// <b>Announced explicitly rather than inferred</b>, the same line
    /// <see cref="AnnounceCardVisibility"/> draws: the coupling between "a new document arrived" and
    /// "the panels are not what they were" is greppable this way and invisible otherwise.
    /// </remarks>
    private void AnnouncePanels()
    {
        OnPropertyChanged(nameof(ShowSpecification));
        OnPropertyChanged(nameof(ShowBoard));
        OnPropertyChanged(nameof(ShowParts));
        OnPropertyChanged(nameof(ShowResults));
        OnPropertyChanged(nameof(ShowBoardColumn));
        OnPropertyChanged(nameof(PartsFillsBoardColumn));
        RefreshPanelCommands();
    }
}
