using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>What the user chose for one cell arriving from another workspace.</summary>
/// <param name="Reference">True to reference the cell where it is; false to copy it in.</param>
/// <param name="SubCells">Only meaningful when copying — a referenced cell's sub-cells are ALWAYS
/// by reference (MW2 R-mw2-17), which is why the nested choice is disabled under Reference rather
/// than offering a third combination that does not exist.</param>
/// <param name="BringTechnology">§5C.2a/R47g — asked only when the destination would read the cell's
/// layers differently, or has no table at all. True copies the source <c>.ctech</c> into the
/// destination's <c>tech/</c>; what it is then used FOR depends on the mode, because the two modes
/// resolve a technology by different routes. A COPY is pointed at it directly (each copied
/// <c>.clay</c> gets a <c>TechRef</c>). A REFERENCE cannot be — the cell stays where it is, and the
/// host layouts that will place it resolve through the workspace DEFAULT — so it becomes that,
/// through R47f's confirmation.</param>
public sealed record AddCellChoice(bool Reference, SubCellMode SubCells, bool BringTechnology = false);

/// <summary>
/// The prompt MW3 §1 specifies: a cell has arrived from another workspace, and the receiving
/// workspace asks whether to take a copy of it or to reference it where it is.
///
/// <para><b>R-mw3-1 — the sub-cell choice is nested under Copy and disabled under Reference</b>,
/// because a referenced cell's sub-cells are always by reference. Offering the fourth combination
/// would imply a mode that does not exist.</para>
///
/// <para><b>R-mw3-2 — the last choice is remembered for the SESSION and pre-selected</b>, so moving
/// six cells is six confirmations rather than six decisions. Deliberately NOT persisted across
/// launches: the right answer depends on what the user is doing that day, and a silently remembered
/// "Reference" would be a nasty surprise months later.</para>
/// </summary>
public partial class AddCellToWorkspaceDialog : Window
{
    // Session memory (R-mw3-2). Static, never written to AppPreferences.
    private static bool        _lastReference;
    private static SubCellMode _lastSubCells = SubCellMode.Copy;

    // Not readonly, and not a constant of the dialog: bringing the technology in is what MAKES a
    // reference legal (it is R47e's first remedy, applied at the workspace instead of one layout), so
    // the checkbox toggles this. Offering the checkbox beside a permanently-disabled Reference was
    // read — correctly — as the checkbox promising something it did not do.
    private bool _referenceAllowed;
    private bool _referenceRefusalIsTechnologyOnly;

    /// <summary>True until the hierarchy walk lands. Reference is held unavailable throughout, because
    /// whether it is legal is one of the answers still coming — showing it enabled and then taking it
    /// away, or the reverse, would be the dialog changing its mind in front of the user. Copy is
    /// selectable the whole time; only OK waits.</summary>
    private bool _planPending = true;

    /// <summary>Design-time only. The plan never lands, so the previewer shows the dialog in the
    /// state a real one opens in — which is the state worth previewing.</summary>
    public AddCellToWorkspaceDialog()
        : this("Cell", "Source", "This workspace", false, true,
               new TaskCompletionSource<CrossWorkspaceCellCopy.CellCopyPlan>().Task) { }

    /// <param name="cellName">The cell being added.</param>
    /// <param name="sourceWorkspaceName">The workspace it lives in — "(no workspace)" when it is in none.</param>
    /// <param name="destWorkspaceName">The workspace receiving it.</param>
    /// <param name="hasSubCells">Whether the cell places any cell of its own workspace. When it does
    /// not, the nested choice changes nothing and is hidden rather than shown inert. Answered from the
    /// top cell alone (<c>CrossWorkspaceCellCopy.HasSubCells</c>) so it is available immediately.</param>
    /// <param name="sourceIsInAWorkspace">False for a cell in no workspace at all — the one refusal
    /// that is known without any walk, and the one no technology can undo.</param>
    /// <param name="pendingPlan">
    /// The full plan, still running. <b>This is why the dialog opens in milliseconds</b>: the two
    /// answers that need the whole hierarchy — which kits the destination has not imported (R-mw3-8)
    /// and what its layer table would make of the copied shapes (R47g) — arrive here instead of being
    /// waited for before the window is shown. A large library cell made that wait ~60 s of frozen UI
    /// (owner, 2026-09-05); it is now a visible dialog that fills itself in.
    ///
    /// <para><b>OK is disabled until it lands</b>, and Cancel is not. Both outstanding answers change
    /// what OK MEANS — whether Reference is available, and whether a technology comes with the copy —
    /// so completing the dialog early would either drop a question the user never saw or ask it after
    /// they had already committed. An indeterminate bar appears directly ABOVE that button (after
    /// <see cref="BusyIndicatorDelay"/>) to say why it is disabled; the app is responsive throughout
    /// and the gesture can be abandoned.</para>
    /// </param>
    public AddCellToWorkspaceDialog(
        string cellName, string sourceWorkspaceName, string destWorkspaceName,
        bool hasSubCells, bool sourceIsInAWorkspace, Task<CrossWorkspaceCellCopy.CellCopyPlan> pendingPlan)
    {
        InitializeComponent();

        _cellName         = cellName;
        _destWorkspaceName = destWorkspaceName;
        _hasSubCells      = hasSubCells;

        // The one refusal that needs no walk. A ws:// reference names a workspace, so a cell in none
        // cannot be reached through one however the technologies compare.
        _referenceRefusal = sourceIsInAWorkspace ? null
            : $"'{cellName}' is not inside a workspace, so there is nothing to reference it through — "
            + "a ws:// reference names a workspace. It can be copied in.";
        _referenceRefusalIsTechnologyOnly = false;

        HeaderText.Text = $"{cellName}   →   {destWorkspaceName}";

        CopyRadio.Content      = $"Copy the cell into {destWorkspaceName}";
        SubRefRadio.Content    = $"Keep them referenced in {sourceWorkspaceName}";
        ReferenceRadio.Content = $"Reference {sourceWorkspaceName}'s cell from {destWorkspaceName}";
        // What the reference actually costs the receiving tree, which is the question a user is
        // really asking here: ONE row. Referencing a cell used to list the whole source workspace,
        // and this sentence used to say so.
        ReferenceNoteText.Text =
            $"Lists {cellName} in {destWorkspaceName}'s Project Tree — one row, nothing copied. "
          + $"{sourceWorkspaceName}'s other cells do not come with it.";

        SubCellPanel.IsVisible = hasSubCells;

        OkButton.IsEnabled = false;
        _ = ShowBusyIfStillWorkingAsync();
        SyncReferenceAvailability();

        // R-mw3-2's pre-selection, clamped to what is actually offered here. Re-clamped when the plan
        // lands, since that is what can make Reference available.
        ApplyRemembered();

        _ = FillInFromPlanAsync(pendingPlan);
    }

    /// <summary>
    /// How long the walk may run before it is worth saying so. Below this the plan lands, OK enables,
    /// and the indicator never appears at all — which is the point: a bar that flashes for one frame
    /// on every ordinary cell is noise, and noise is what teaches people to ignore the indicator that
    /// matters. Most cells finish in single-digit milliseconds; the one that prompted this took ~60 s.
    /// </summary>
    private static readonly TimeSpan BusyIndicatorDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Reveals the indeterminate bar, if the walk is still going after
    /// <see cref="BusyIndicatorDelay"/>.
    ///
    /// <para><b>Indeterminate, and that is not a shortcut.</b> The walk is a graph traversal that
    /// discovers what it has to visit as it goes — <c>CollectHierarchy</c> cannot know the total up
    /// front — so there is no honest fraction to fill. A bar that invents one is worse than a bar that
    /// says only "still working", which is the true statement available here.</para>
    /// </summary>
    private async Task ShowBusyIfStillWorkingAsync()
    {
        await Task.Delay(BusyIndicatorDelay);
        // An indeterminate bar, not just a sentence: a disabled OK with a static line beside it reads
        // as "broken", and the thing that reads as "working" is motion (owner, 2026-09-05).
        if (_planPending) BusyPanel.IsVisible = true;
    }

    private readonly string  _cellName;
    private readonly string  _destWorkspaceName;
    private readonly bool    _hasSubCells;
    private          string? _referenceRefusal;

    /// <summary>
    /// Populates everything that needed the hierarchy walk, on the UI thread, once it is done.
    ///
    /// <para>A failed plan is not a silent success: the walk decides whether a reference is legal and
    /// whether a technology should travel, so on an exception the dialog keeps Reference refused,
    /// says why, and lets the copy proceed on the destination's own terms — the same conservative
    /// direction the gate itself takes when it cannot read one side.</para>
    /// </summary>
    private async Task FillInFromPlanAsync(Task<CrossWorkspaceCellCopy.CellCopyPlan> pendingPlan)
    {
        CrossWorkspaceCellCopy.CellCopyPlan plan;
        try { plan = await pendingPlan; }
        catch (Exception ex)
        {
            BusyPanel.IsVisible = false;
            _planPending = false;
            _referenceRefusal ??= $"'{_cellName}' could not be examined ({ex.Message}), so it cannot "
                                + "be referenced. It can still be copied in.";
            SyncReferenceAvailability();
            ApplyRemembered();
            OkButton.IsEnabled = true;
            return;
        }

        BusyPanel.IsVisible = false;
        _planPending = false;

        // MW2 R-mw2-7's refusal, now that the technologies have actually been compared. Only reached
        // when the cell IS in a workspace — the other refusal was settled before the window opened
        // and must not be overwritten by a technology answer that cannot undo it.
        if (_referenceRefusal is null && !plan.Technology.Permitted)
        {
            _referenceRefusal = plan.Technology.Refusal;
            _referenceRefusalIsTechnologyOnly = true;
        }

        ApplyTechnologyOffer(plan.Technology);
        ApplyKitWarning(plan.UnimportedKits);
        SyncReferenceAvailability();
        ApplyRemembered();
        OkButton.IsEnabled = true;
    }

    private void ApplyRemembered()
    {
        bool reference = _lastReference && _referenceAllowed;
        ReferenceRadio.IsChecked = reference;
        CopyRadio.IsChecked      = !reference;

        bool subRef = _lastSubCells == SubCellMode.KeepReferenced && _referenceAllowed;
        SubRefRadio.IsChecked  = subRef;
        SubCopyRadio.IsChecked = !subRef;
    }

    /// <summary>
    /// R47g. The offer, not a refusal: a copy is a file operation the user has already asked for,
    /// and the destination's own default IS a legitimate answer when they say so. What was wrong
    /// was doing it without asking, in the route the placement refusal itself points at.
    /// </summary>
    private void ApplyTechnologyOffer(ExternalRefCheck copyTechnology)
    {
        if (copyTechnology.Outcome is ExternalRefOutcome.Permitted) return;

        string techName = copyTechnology.TechnologyDisplay;

        BringTechWarningText.Text = copyTechnology.Outcome is ExternalRefOutcome.AdoptTheirTechnology
            ? $"{_cellName} was drawn with {techName}, and {_destWorkspaceName} has no technology of "
            + "its own. Nothing here would interpret its layers."
            : $"{_cellName} was drawn with {techName}, which {_destWorkspaceName}'s technology "
            + "disagrees with on a layer the cell uses. Without it, its shapes keep their layer "
            + "numbers and take this workspace's meanings for them.";

        BringTechCheck.Content   = $"Bring {techName} into {_destWorkspaceName}";
        BringTechCheck.IsChecked = true;
        BringTechPanel.IsVisible = true;

        // The two modes use it differently, and saying so is what makes the next click predictable —
        // one writes a TechRef into the copies, the other changes what the whole workspace draws
        // with, which is a much larger act and is confirmed separately.
        BringTechEffectText.Text =
            $"Copy: the copied layouts point at it.   Reference: it becomes {_destWorkspaceName}'s "
          + "technology, which is what lets the reference be placed.";
        BringTechEffectText.IsVisible = true;

        BringTechCheck.IsCheckedChanged += (_, _) => { SyncReferenceAvailability(); ApplyRemembered(); };
    }

    /// <summary>R-mw3-8's trap, stated before the copy: a <c>pdk://</c> reference is not rewritten, so
    /// a cell full of pin-less placeholders is the outcome this warning exists to prevent.</summary>
    private void ApplyKitWarning(IReadOnlyList<string> unimportedKits)
    {
        if (unimportedKits.Count == 0) return;

        string kits = string.Join(", ", unimportedKits);
        string s    = unimportedKits.Count == 1 ? "kit" : "kits";
        KitWarningText.Text =
            $"{_cellName} uses parts from {s} {kits}, which {_destWorkspaceName} has not imported. "
          + "Copy it anyway and the parts show as unresolved until you import the kit"
          + (_referenceAllowed ? ", or reference the cell instead." : ".");
        KitWarningText.IsVisible = true;
    }

    /// <summary>
    /// Whether Reference is offered right now, and the sentence shown when it is not. Re-run whenever
    /// the technology checkbox changes, because that checkbox is the one thing in this dialog that can
    /// turn a technology refusal into a permitted reference — and again when the plan lands, which is
    /// when a technology refusal can first exist at all.
    /// </summary>
    private void SyncReferenceAvailability()
    {
        bool fixedByBringing = _referenceRefusalIsTechnologyOnly && BringTechCheck.IsChecked == true;
        _referenceAllowed = !_planPending && (_referenceRefusal is null || fixedByBringing);

        ReferenceRadio.IsEnabled    = _referenceAllowed;
        SubRefRadio.IsEnabled       = _referenceAllowed;
        ReferenceNoteText.IsVisible = _referenceAllowed;
        RefusalText.Text            = _referenceRefusal;
        RefusalText.IsVisible       = !_planPending && !_referenceAllowed && _referenceRefusal is not null;
        SubRefRefusalText.Text      = "Unavailable for the same reason.";
        SubRefRefusalText.IsVisible  = !_planPending && !_referenceAllowed && _hasSubCells;

        // Un-ticking the box under a selected Reference would otherwise leave a disabled radio
        // checked, and OK would silently fall through to Copy.
        if (!_referenceAllowed && ReferenceRadio.IsChecked == true)
        {
            ReferenceRadio.IsChecked = false;
            CopyRadio.IsChecked      = true;
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        bool reference = ReferenceRadio.IsChecked == true && _referenceAllowed;
        var  subCells  = SubRefRadio.IsChecked == true && _referenceAllowed
            ? SubCellMode.KeepReferenced
            : SubCellMode.Copy;

        _lastReference = reference;
        _lastSubCells  = subCells;

        // Deliberately NOT remembered across cells the way the mode is (R-mw3-2): which technology a
        // cell should land on is a fact about THAT cell, and a silently-reused "no" would put a second
        // cell onto the wrong layer table without asking again.
        Close(new AddCellChoice(reference, subCells,
                                BringTechnology: BringTechPanel.IsVisible && BringTechCheck.IsChecked == true));
    }
}
