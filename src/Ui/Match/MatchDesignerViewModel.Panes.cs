// Which of the Designer's four panels are on screen (owner, 2026-09-20) — the same control railRF
// grew a day earlier, and deliberately the same shape, because the two windows are the same shape:
// a fixed input column, a working column that splits into two, and a results column.
//
// ── SESSION STATE, NOT DOCUMENT STATE ─────────────────────────────────────────────────────────
//
// railRF keeps its four flags in the `.crail`, on the owner's own instruction, because the question
// it answers is about a BOARD. This window's is not: a Match design is a base64 payload on one
// component parameter inside somebody's schematic, and putting view state in there would make an
// unrelated schematic file DIRTY every time a panel was collapsed. So these live on the view model
// for the life of the window, exactly as the two pane expanders they replace did.
//
// ── THEY REPLACE THE TWO DIAGONAL-ARROW EXPANDERS ─────────────────────────────────────────────
//
// `NetworkExpanded` / `ResponseExpanded` were a pair of mutually-exclusive toggles, each giving one
// of the two right-hand panes the other's column. Four independent switches say everything those
// two said — "network over response" is now the response button, off — and three things they could
// not: the specification column can be released, the transforms rack can be released on its own,
// and any two panels can be shown at once. The arrows went with them; there is nothing left for
// them to mean that a lit lamp does not say more plainly.

using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Matching;

public sealed partial class MatchDesignerViewModel
{
    private bool _showSpecification = true;
    private bool _showNetwork       = true;
    private bool _showTransforms    = true;
    private bool _showResponse      = true;

    /// <summary>The specification column — terminations, band, ripple and the solutions list.</summary>
    public bool ShowSpecification
    {
        get => _showSpecification;
        set => SetPanel(ref _showSpecification, value);
    }

    /// <summary>The Impedance Matching Network pane — the schematic and the value grid under it.</summary>
    public bool ShowNetwork
    {
        get => _showNetwork;
        set => SetPanel(ref _showNetwork, value);
    }

    /// <summary>The Transforms rack, under the network.</summary>
    public bool ShowTransforms
    {
        get => _showTransforms;
        set => SetPanel(ref _showTransforms, value);
    }

    /// <summary>The Response column — the two plots and their window controls.</summary>
    public bool ShowResponse
    {
        get => _showResponse;
        set => SetPanel(ref _showResponse, value);
    }

    /// <summary>
    /// Whether the centre column is drawn at all — the network and the transforms SHARE it.
    /// </summary>
    /// <remarks>
    /// With both off the column collapses and the response column takes the width, which is the one
    /// case where the response grows sideways rather than just taller. It is the state the old
    /// <c>ResponseExpanded</c> toggle used to reach in one click, and it still takes two.
    /// </remarks>
    public bool ShowNetworkColumn => ShowNetwork || ShowTransforms;

    // ── AT LEAST ONE PANEL IS ALWAYS SHOWING ────────────────────────────────────────────────
    //
    // Four panels all hidden is a window with a title bar, a toolbar and nothing between them — a
    // state with no content, reachable in four clicks and escapable only by recognising four dark
    // buttons as the way out. So the LAST lit button is DISABLED rather than inert: a control that
    // can be pressed and does nothing is indistinguishable from one that is broken.
    //
    // A CanExecute rather than a guard inside the setter, so the button dims by itself — the
    // Command binding does that — and nothing has to remember to re-assert an IsEnabled. The guard
    // is in the setter TOO, because a caller that is not the button (a test, a key binding) must
    // not be able to empty the window either.

    private int ShownPanelCount =>
        (ShowSpecification ? 1 : 0) + (ShowNetwork ? 1 : 0)
      + (ShowTransforms ? 1 : 0) + (ShowResponse ? 1 : 0);

    private bool CanToggle(bool shown) => !shown || ShownPanelCount > 1;

    private bool CanToggleSpecification => CanToggle(ShowSpecification);
    private bool CanToggleNetwork       => CanToggle(ShowNetwork);
    private bool CanToggleTransforms    => CanToggle(ShowTransforms);
    private bool CanToggleResponse      => CanToggle(ShowResponse);

    [RelayCommand(CanExecute = nameof(CanToggleSpecification))]
    private void ToggleSpecification() => ShowSpecification = !ShowSpecification;

    [RelayCommand(CanExecute = nameof(CanToggleNetwork))]
    private void ToggleNetwork() => ShowNetwork = !ShowNetwork;

    [RelayCommand(CanExecute = nameof(CanToggleTransforms))]
    private void ToggleTransforms() => ShowTransforms = !ShowTransforms;

    [RelayCommand(CanExecute = nameof(CanToggleResponse))]
    private void ToggleResponse() => ShowResponse = !ShowResponse;

    /// <summary>
    /// Writes one panel flag and announces it — <b>and the derived predicate with it</b>, which a
    /// computed property cannot do for itself.
    /// </summary>
    private void SetPanel(ref bool field, bool value,
                          [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (field == value) return;
        if (!value && ShownPanelCount <= 1) return;

        field = value;
        OnPropertyChanged(name);
        OnPropertyChanged(nameof(ShowNetworkColumn));
        RefreshPanelCommands();
    }

    /// <summary>Re-asks all four whether they may be pressed. <b>All four, on any change</b> —
    /// hiding one panel is what makes another one the last.</summary>
    private void RefreshPanelCommands()
    {
        ToggleSpecificationCommand.NotifyCanExecuteChanged();
        ToggleNetworkCommand.NotifyCanExecuteChanged();
        ToggleTransformsCommand.NotifyCanExecuteChanged();
        ToggleResponseCommand.NotifyCanExecuteChanged();
    }
}
