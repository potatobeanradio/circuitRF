// The railRF window's view model (docs/sonnet-briefs/brief-railrf-7-window.md; railrf.md §11).
//
// ── WHAT THIS FILE IS, AND WHAT IT IS NOT ─────────────────────────────────────────────────────
//
// §11 is "deliberately short on invention: it names what is taken, and only describes what is
// genuinely new", and this file is the same. It EDITS a RailDocument, PROPOSES a reference layer,
// holds the four lists, and hands a request to briefs 3-6. It extracts nothing, solves nothing,
// draws nothing and plots nothing of its own:
//
//   * the extraction and the solve are RailDcRun's (briefs 3-6). The Fast/Accurate split is
//     PdnModelKind's, not a mode flag invented here.
//   * the board in the centre is LayoutCanvas — the layout editor's own canvas (§11.6). Brief 8
//     owns every gesture and every pixel of overlay; this holds the session and the tab strip,
//     and the tab strip switches the OVERLAY and never the geometry.
//   * the plots are PlotControls in rectangular mode fed from a DataSet (§11.1). Brief 12 builds
//     the DataSet; what is here is the host the plots hang off.
//
// ── THE ONE INVARIANT THIS FILE OWES (R-rail4-2, R-rail7-5) ───────────────────────────────────
//
// THE MODEL KIND ON SCREEN ALWAYS MATCHES THE NUMBERS ON SCREEN. A strip reading "Fast" over an
// Accuracy result, for even one frame, is the exact failure PdnModelKind exists to prevent — so
// there is ONE field holding both (RailResultView), it is assigned atomically, and nothing anywhere
// stores a model kind beside a result. See `RailRfViewModel.Solve.cs`.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.RailRf;

/// <summary>Which map the board tab strip is showing. <b>The tabs switch the OVERLAY and never the
/// geometry</b> (§11.3's first point) — the artwork underneath is the same on all four.</summary>
public enum RailBoardOverlay
{
    /// <summary>The artwork as drawn, with the rail's own copper highlighted.</summary>
    Copper,

    /// <summary>§2.4's drop map — the artwork coloured by node voltage.</summary>
    Drop,

    /// <summary>The |Z| map over frequency.</summary>
    Impedance,

    /// <summary>§2.9's trace-versus-mesh classification. <b>A requirement rather than a
    /// diagnostic</b>: a wide supply polygon mistaken for a trace is optimistic and invisible, and
    /// drawing it is what makes it neither.</summary>
    Class,
}

/// <summary>Which half of the results column is showing.</summary>
public enum RailResultsTab
{
    /// <summary>The drop, the breakdown, the via check.</summary>
    Dc,

    /// <summary>|Z| against frequency, its mask and the aggressor lines.</summary>
    Frequency,
}

/// <summary>One entry of the reference-layer combo: the drawing layer, and what it is called.</summary>
/// <param name="Key">The drawing layer, which is what <see cref="RailSpec.ReferenceLayer"/> holds.
/// <see cref="LayerKey"/>'s own default where <see cref="Unavailable"/> says there is none.</param>
/// <param name="Name">The technology's own name for it.</param>
public sealed record RailLayerOption(LayerKey Key, string Name)
{
    /// <summary>
    /// Why this row cannot be chosen, or null for an ordinary one.
    /// </summary>
    /// <remarks>
    /// <b>A conductor the stackup declares and attaches no DRAWING LAYER to</b> (owner, 2026-09-21).
    /// A reference is named by a drawing layer — <see cref="RailSpec.ReferenceLayer"/> is a
    /// <see cref="LayerKey"/>, and <c>PdnStackupGeometry.ConductorOf</c> finds a conductor's z by
    /// that key — so such a conductor has nothing to be named by and cannot be a reference until
    /// somebody attaches one in the technology editor.
    ///
    /// <para><b>It is LISTED and disabled rather than left out.</b> Leaving it out is what was
    /// reported: a designer added an inner ground plane to the stackup, came back to railRF, and the
    /// combo offered the same two outer layers as before with nothing anywhere to say why — the
    /// stackup in front of them plainly had three conductors in it. A row that is present, greyed
    /// and says what is missing is the difference between a tool that cannot do something and a tool
    /// that looks broken.</para>
    /// </remarks>
    public string? Unavailable { get; init; }

    /// <summary>True for a row that can be chosen — every row with a drawing layer behind it.</summary>
    public bool IsSelectable => Unavailable is null;

    public override string ToString() => Unavailable is { Length: > 0 } why ? $"{Name} — {why}" : Name;
}

/// <summary>
/// One railRF window's state.
/// </summary>
public sealed partial class RailRfViewModel : ObservableObject, IDisposable
{
    /// <summary>A window with no document behind it yet — Tools ▸ railRF, before an import.</summary>
    public RailRfViewModel() : this(new RailDocument(), null) { }

    public RailRfViewModel(RailDocument document, string? path)
    {
        _document = document;
        _documentPath = path;
        BuildPlotHost();     // the one response container — see RailRfViewModel.Response.cs
        BuildBoardPanel();   // the board canvas's overlay — see RailRfViewModel.Board.cs
        RebuildRails();

        // What the window opened on. A scratch document is captured too, so an EMPTY window is not
        // reported as having unsaved work — see RailRfViewModel.Save.cs.
        CaptureSnapshot();

        // And the undo history starts there too — see RailRfViewModel.Undo.cs.
        BeginUndoHistory();
    }

    private RailDocument _document;
    private string? _documentPath;

    /// <summary>The document this window edits. Never null — a window with nothing imported holds an
    /// empty one, which is what the import fills.</summary>
    public RailDocument Document => _document;

    /// <summary>Where it came from, or null for one that has never been saved.</summary>
    public string? DocumentPath => _documentPath;

    /// <summary>
    /// The window's own title — "railRF — evk_1v8_compact", with a leading bullet while the
    /// document differs from disk.
    /// </summary>
    /// <remarks>
    /// <b>wBond's own spelling</b>, leading bullet and all, because a mark at the front is the one
    /// still visible when a platform truncates a long title. Save is enabled whether or not it is
    /// showing: the mark is information, and a disabled Save would be a refusal.
    /// </remarks>
    public string Title =>
        (IsDirty ? "• " : "") + "railRF" + (DocumentName is { Length: > 0 } n ? $" — {n}" : "");

    /// <summary>
    /// What the text at the top left of the window says — <b>the file name alone</b>.
    /// </summary>
    /// <remarks>
    /// <b>No "railRF —" prefix, on the owner's instruction</b> (2026-09-19). The application's name
    /// belongs in the OS title bar, where a window is one row among every other window on the
    /// machine; inside the window it is a word the reader has already read, repeated above content
    /// that could not be anything else. The dirty bullet stays on both, because it is the one mark
    /// that is information rather than decoration.
    ///
    /// <para>A window that has never been saved and holds no board says so rather than showing an
    /// empty strip.</para>
    /// </remarks>
    public string DocumentLabel =>
        (IsDirty ? "• " : "") + (DocumentName is { Length: > 0 } n ? n : "untitled");

    /// <summary>
    /// The document's FILE NAME, extension and all — <c>evk_1v8_compact.crail</c>.
    /// </summary>
    /// <remarks>
    /// <b>The extension is on purpose</b> (owner, 2026-09-19). A railRF document is a file a user
    /// opens, saves, sends and puts under revision control, and a bare stem does not say which of
    /// the several circuitRF documents named after this board it is. The document's own
    /// <see cref="RailDocument.Name"/> still wins over the path — a board is named in the file and
    /// that name is what the rest of the window reads — so the extension is APPENDED to it rather
    /// than taken from disk, and never doubled for a name that already carries one.
    /// </remarks>
    private string? DocumentName =>
        _document.Name is { Length: > 0 } n ? WithCrailExtension(n)
        : _documentPath is { Length: > 0 } p ? System.IO.Path.GetFileName(p)
        : null;

    private static string WithCrailExtension(string name) =>
        name.EndsWith(RailDocumentIo.Extension, StringComparison.OrdinalIgnoreCase)
            ? name
            : name + RailDocumentIo.Extension;

    /// <summary>
    /// The full path this document came from, for the title's tooltip — or the sentence that says
    /// there is not one yet.
    /// </summary>
    /// <remarks>
    /// A title says which document; only the path says <i>which copy of it</i>, and on a machine
    /// with a board in three places that is the whole question. A window that has never been saved
    /// answers it rather than showing an empty tip, which reads as a broken tooltip.
    /// </remarks>
    public string DocumentPathTip =>
        _documentPath is { Length: > 0 } p ? p : "Not saved yet — Save writes it somewhere.";

    /// <summary>Replaces the document this window shows — what the import and a re-open do.</summary>
    public void SetDocument(RailDocument document, string? path)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _documentPath = path;

        // A fast curve beside an accurate one from a DIFFERENT board is worse than no comparison at
        // all (PdnResultsByModel's own note), so the results go with the document.
        ClearResults();
        ForgetRestoredMarkers();   // a new document's markers are its own — see the rail-change note
        RebuildRails();
        AnnouncePanels();

        // The new document carries its own hidden layers (R-rail20-1b). The rows are NOT rebuilt
        // here — they are built from the BOARD, which the open path replaces immediately after this
        // and which rebuilds them itself. This re-reads the set the existing rows are showing, for
        // the one case where the new document's artwork does not resolve and the old board stands.
        ApplyLayerVisibility();

        // And its own display unit — or, unseeded, the board's until the board replaced after this
        // seeds it.
        OnPropertyChanged(nameof(DisplayUnit));
        RefreshIfUnitChanged();

        CaptureSnapshot();

        // A new document is a new history. Keeping the old one would offer to undo an edit into a
        // document that is no longer on screen — the snapshot would be applied to the wrong file.
        BeginUndoHistory();
        OnPropertyChanged(nameof(IsDirty));

        OnPropertyChanged(nameof(Document));
        OnPropertyChanged(nameof(DocumentPath));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(DocumentLabel));
        OnPropertyChanged(nameof(DocumentPathTip));
    }

    // ── The rail selector (§11.3's fourth point) ───────────────────────────────────────────────

    /// <summary>Every rail this document holds, in declaration order — what the selector shows.
    /// <see cref="RailOrder"/> computes the SOLVE order, which is not this one.</summary>
    public ObservableCollection<string> Rails { get; } = [];

    /// <summary>
    /// Which rail the whole window is showing (§2.2: one rail is ANALYSED at a time).
    /// </summary>
    /// <remarks>
    /// <b>It is the selector above the source and load lists, and it moves the whole window</b> —
    /// the lists, the target, the band, the aggressors, the board's highlight and the results. That
    /// is what §11.3 means by "says which rail the window is currently showing"; a selector that
    /// moved only the lists would leave the results reading a different rail's numbers with nothing
    /// saying so.
    /// </remarks>
    [ObservableProperty]
    private string? _selectedRailName;

    partial void OnSelectedRailNameChanged(string? value)
    {
        // The plot is about to show a DIFFERENT rail's ports, and a curve key is the reading and the
        // port — which both rails have. Without this the markers still on the plot would be captured
        // onto the rail being moved to. See RailRfViewModel.Markers.cs.
        ForgetRestoredMarkers();
        RebuildForSelectedRail();
        ShowSelectedRailRefusal();
        RemoveRailCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanRemoveRail));
    }

    /// <summary>The selected rail, or null where the document holds none.</summary>
    public RailSpec? SelectedRail =>
        SelectedRailName is { Length: > 0 } n ? _document.Rail(n) : null;

    private void RebuildRails()
    {
        Rails.Clear();
        foreach (var r in _document.Rails) Rails.Add(r.Name);

        // Set ItemsSource BEFORE SelectedItem, always — the wBond round-6 blank-Group-combo bug
        // (§7's second trap): a combo whose ItemsSource binding attaches after a selection was set
        // drops the selection silently, and the user sees an empty combo over a populated list.
        SelectedRailName = Rails.FirstOrDefault();
        RebuildForSelectedRail();
    }

    // ── The four lists ─────────────────────────────────────────────────────────────────────────

    /// <summary>The rail's sources — a list with add and remove, each row a refdes and a pin.</summary>
    public ObservableCollection<RailSourceRowViewModel> Sources { get; } = [];

    /// <summary>The rail's loads. <b>A row with no current reads <i>observe</i></b>.</summary>
    public ObservableCollection<RailLoadRowViewModel> Loads { get; } = [];

    /// <summary>What on this board excites this rail.</summary>
    public ObservableCollection<RailAggressorRowViewModel> Aggressors { get; } = [];

    /// <summary>The parts table (R-rail7-9). Rebuilt by <c>RailRfViewModel.Parts.cs</c>.</summary>
    public ObservableCollection<RailPartRowViewModel> Parts { get; } = [];

    private void RebuildForSelectedRail()
    {
        // The row objects below are about to be replaced, so a selection pointing at one of them is
        // pointing at a row that is no longer in any list — and with no ListBox attached (a test, a
        // window not yet shown) nothing else would ever null it.
        ClearRowSelection();

        foreach (var s in Sources) s.Edited -= OnRowEdited;
        foreach (var l in Loads) l.Edited -= OnRowEdited;
        foreach (var a in Aggressors) a.Edited -= OnRowEdited;

        Sources.Clear();
        Loads.Clear();
        Aggressors.Clear();

        if (SelectedRail is { } rail)
        {
            foreach (var s in rail.Sources) Sources.Add(Track(new RailSourceRowViewModel(rail, s, BoardLengthFormat, BoardPads)));
            foreach (var l in rail.Loads) Loads.Add(Track(new RailLoadRowViewModel(rail, l, BoardLengthFormat, BoardPads)));
            for (int i = 0; i < rail.Aggressors.Count; i++)
                Aggressors.Add(Track(new RailAggressorRowViewModel(rail, rail.Aggressors[i], i)));
        }

        RebuildReferenceOptions();
        RefreshTargets();
        RefreshRunGate();
        SyncPlaneFrequencyDefault();

        // R-rail18-5a: the parts table is of the SELECTED rail, so selecting another rail rebuilds
        // it. Until then it was of the BOM, which is the whole board and does not change with the
        // selector above it.
        RebuildParts();

        OnPropertyChanged(nameof(SelectedRail));
        OnPropertyChanged(nameof(StatusLine));
        SyncBoardOverlayResult();
    }

    private RailSourceRowViewModel Track(RailSourceRowViewModel row) { row.Edited += OnRowEdited; return row; }
    private RailLoadRowViewModel Track(RailLoadRowViewModel row) { row.Edited += OnRowEdited; return row; }
    private RailAggressorRowViewModel Track(RailAggressorRowViewModel row) { row.Edited += OnRowEdited; return row; }

    /// <summary>Every committed row edit lands here, and here is where the Fast loop starts.</summary>
    private void OnRowEdited(object? sender, EventArgs e) => QueueResolve();

    // ── Add and remove ─────────────────────────────────────────────────────────────────────────

    /// <summary>Adds a source row. It refuses until it is anchored, which is visible on the
    /// row rather than only at Run — an unanchored row the user cannot see is a row they will not
    /// fix.</summary>
    /// <remarks><b>Carrying a voltage</b> — see <c>RailRfViewModel.Seeds.cs</c> for which one and for
    /// why a seeded value is allowed here when the model itself still refuses to default one.</remarks>
    [RelayCommand]
    private void AddSource()
    {
        if (SelectedRail is not { } rail) return;
        rail.Sources.Add(NewSeededSource(rail));
        RebuildForSelectedRail();
        QueueResolve();
    }

    /// <inheritdoc cref="CanRemoveAggressor"/>
    private static bool CanRemoveSource(RailSourceRowViewModel? row) => row is not null;

    [RelayCommand(CanExecute = nameof(CanRemoveSource))]
    private void RemoveSource(RailSourceRowViewModel? row)
    {
        if (SelectedRail is not { } rail || row is null) return;
        rail.Sources.Remove(row.Source);
        RebuildForSelectedRail();
        QueueResolve();
    }

    /// <summary>Adds a load row, <b>carrying a small starting current rather than reading
    /// <i>observe</i></b>.</summary>
    /// <remarks>
    /// The absence is still representable and clearing the cell still restores it — what changed is
    /// only what the ADD gesture hands you, and <c>RailRfViewModel.Seeds.cs</c> argues that in one
    /// place rather than at each of the four sites that make a row.
    /// </remarks>
    [RelayCommand]
    private void AddLoad()
    {
        if (SelectedRail is not { } rail) return;
        rail.Loads.Add(NewSeededLoad());
        RebuildForSelectedRail();
        QueueResolve();
    }

    /// <inheritdoc cref="CanRemoveAggressor"/>
    private static bool CanRemoveLoad(RailLoadRowViewModel? row) => row is not null;

    [RelayCommand(CanExecute = nameof(CanRemoveLoad))]
    private void RemoveLoad(RailLoadRowViewModel? row)
    {
        if (SelectedRail is not { } rail || row is null) return;
        rail.Loads.Remove(row.Load);
        RebuildForSelectedRail();
        QueueResolve();
    }

    [RelayCommand]
    private void AddAggressor()
    {
        if (SelectedRail is not { } rail) return;
        rail.Aggressors.Add(new RailAggressor("new", 1e6, 1));
        RebuildForSelectedRail();

        // The aggressor lines on the |Z| plot and every row of the coincidence table come out of the
        // SWEEP RESULT, so adding one without re-solving leaves both showing the previous set — the
        // same defect the impedance-target field had. Add and remove both re-solve, as the load and
        // source buttons above already do.
        QueueResolve();
    }

    /// <summary>
    /// Removes the selected aggressor — <b>by position</b>.
    /// </summary>
    /// <remarks>
    /// <c>List.Remove</c> of a RECORD removes the first EQUAL one, so with two unedited rows (the add
    /// button makes identical ones) it took the wrong row away, and the row the user had selected
    /// stayed exactly where it was — which is the reported "the − button does not do anything"
    /// (owner, 2026-09-19). <see cref="RailAggressorRowViewModel.Index"/> carries the whole of the
    /// fix; see its own note.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanRemoveAggressor))]
    private void RemoveAggressor(RailAggressorRowViewModel? row)
    {
        if (SelectedRail is not { } rail || row is null) return;

        int i = row.ResolveIndex();
        if (i < 0) return;

        rail.Aggressors.RemoveAt(i);
        RebuildForSelectedRail();
        QueueResolve();
    }

    /// <summary>
    /// False with nothing selected, which GREYS the button.
    /// </summary>
    /// <remarks>
    /// The three remove buttons take the list's <c>SelectedItem</c> as their parameter, and with no
    /// selection that is null and the command did nothing at all — indistinguishable from a broken
    /// button, and these rows do not paint an obvious selection. Disabled says which of the two it is.
    /// </remarks>
    private static bool CanRemoveAggressor(RailAggressorRowViewModel? row) => row is not null;

    // ── The reference layer: PROPOSED, never assumed (§2.2, Q-8, R-rail7-8) ────────────────────

    /// <summary>Every conductor drawing layer the technology names — what the combo offers.</summary>
    public ObservableCollection<RailLayerOption> ReferenceLayerOptions { get; } = [];

    /// <summary>
    /// The layer railRF PROPOSES, or null where the stackup gives it no basis for one.
    /// </summary>
    /// <remarks>
    /// <b>A proposal is not a selection.</b> The window shows it and says why; the user confirms.
    /// Q-8 is explicit that the reference is asked for and never inferred, and a pre-selected combo a
    /// user tabs past is not a confirmation — which is why <see cref="IsReferenceConfirmed"/> is a
    /// separate flag and why Run is disabled until it is set. This is the one place this window
    /// deliberately costs the user a click.
    /// </remarks>
    [ObservableProperty]
    private RailLayerOption? _referenceProposal;

    /// <summary>Why that layer was proposed — shown beside it, because a proposal with no reason is
    /// indistinguishable from a default.</summary>
    [ObservableProperty]
    private string _referenceProposalReason = "";

    /// <summary>
    /// The one-press repair for a conductor listed as <see cref="NoDrawingLayer"/>, where the
    /// technology names the drawing layer that is very likely its copper — or null.
    /// </summary>
    /// <remarks>
    /// <b>The technology editor's own <see cref="TechFix"/>, offered where the problem is SEEN</b>
    /// (field report, 2026-09-23: the designer could not pick the GND plane as a reference, and the
    /// sentence under the combo sent him to another window to find a button there). The fix is
    /// <c>TechValidation</c>'s, so the two windows offer the same repair or neither does.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReferenceFix))]
    [NotifyPropertyChangedFor(nameof(ReferenceFixLabel))]
    private TechFix? _referenceFix;

    /// <summary>True while <see cref="ReferenceFix"/> has a repair to offer.</summary>
    public bool HasReferenceFix => ReferenceFix is not null;

    /// <summary>The repair's button text — "Attach 'gnd' to GND".</summary>
    public string ReferenceFixLabel => ReferenceFix?.Label ?? "";

    /// <summary>
    /// What the combo currently shows. Setting it does NOT confirm it — see
    /// <see cref="ConfirmReference"/>.
    /// </summary>
    [ObservableProperty]
    private RailLayerOption? _selectedReferenceLayer;

    partial void OnSelectedReferenceLayerChanged(RailLayerOption? value)
    {
        // Choosing a DIFFERENT layer from the combo is an affirmative act in itself — the user has
        // read the options and picked one. What is not affirmative is the proposal simply standing
        // there, which is why RebuildReferenceOptions sets this without confirming.
        if (value is not null && !_settingProposal) ConfirmReference();
    }

    private bool _settingProposal;

    /// <summary>True once the reference has been affirmatively set for this rail.</summary>
    [ObservableProperty]
    private bool _isReferenceConfirmed;

    /// <summary>
    /// Takes the combo's current layer as the rail's reference. Bound to the <c>Confirm</c> button
    /// beside the combo and to any change the user makes in it.
    /// </summary>
    [RelayCommand]
    public void ConfirmReference()
    {
        if (SelectedRail is not { } rail || SelectedReferenceLayer is not { } option) return;

        // A row that names a conductor with no drawing layer confirms NOTHING (owner, 2026-09-21).
        // Its Key is `default`, so taking it would set the rail's reference to layer 0/0 — a layer
        // most boards do not have, and one the extraction would then refuse for a reason that names
        // the wrong thing. The row is in the list to be READ, and the sentence under the combo says
        // what to do about it.
        if (!option.IsSelectable) return;

        rail.ReferenceLayer = option.Key;
        IsReferenceConfirmed = true;

        // R-rail19-1d: ONLY now is there anything to measure. Before the confirmation railRF does
        // not know which net the return is, and marking a row as though it did is the guess
        // R-rail19-1c refuses.
        RefreshNetMarks();
        RefreshRunGate();
        QueueResolve();
    }

    private void RebuildReferenceOptions()
    {
        ReferenceLayerOptions.Clear();

        var tech = Board?.Technology;
        if (tech is not null)
            foreach (var option in ConductorOptions(tech))
                ReferenceLayerOptions.Add(option);

        var (proposal, reason) = ProposeReference(tech, SelectedRail);
        ReferenceProposal = proposal;
        ReferenceProposalReason = reason;
        ReferenceFix = tech is null
            ? null
            : TechValidation.Analyze(tech).Select(p => p.Fix).FirstOrDefault(f => f is not null);

        // ItemsSource first, then the selection — always (§7's second trap).
        _settingProposal = true;
        try
        {
            // A rail that already NAMES a reference is one somebody confirmed before, on a document
            // that was saved with it. Re-asking would be a click charged for a decision already made.
            //
            // UNLESS THE LAYER IT NAMES IS NOT ONE THIS TECHNOLOGY OFFERS — a stackup edited, or a
            // document opened against a different one. The combo can only show a layer it has, so
            // it falls back to the proposal; calling that CONFIRMED would leave the window showing
            // one layer while the document held another, and Run would then solve against the one
            // nobody could see. An unresolvable reference is an unconfirmed one, and costs the click.
            // SELECTABLE rows only: a conductor with no drawing layer is listed with `default` as
            // its key, which would otherwise match a rail that genuinely names layer 0/0.
            var stated = SelectedRail?.ReferenceLayer;
            var statedOption = stated is { } key
                ? ReferenceLayerOptions.FirstOrDefault(o => o.IsSelectable && o.Key == key)
                : null;
            SelectedReferenceLayer = statedOption ?? proposal;
            IsReferenceConfirmed = statedOption is not null;
        }
        finally { _settingProposal = false; }

        // A document that arrived with its reference already stated confirms nothing and still has
        // a return to mark — see ConfirmReference's own note for the other half.
        RefreshNetMarks();
    }

    /// <summary>
    /// Every conductor of the stackup — <b>including the ones that claim no drawing layer</b>, which
    /// are listed disabled and say so.
    /// </summary>
    /// <remarks>
    /// See <see cref="RailLayerOption.Unavailable"/> for the report this answers. The rule the combo
    /// keeps is that it shows the STACKUP: a conductor that is in the stackup and not in this list
    /// is a conductor a user can see in one window and not in the other.
    /// </remarks>
    private static IEnumerable<RailLayerOption> ConductorOptions(Technology tech)
    {
        foreach (var entry in tech.Stackup.Layers)
        {
            if (entry.Kind != StackupKind.Conductor) continue;

            if (entry.DrawingLayers.Count == 0)
            {
                yield return new RailLayerOption(default, entry.Name)
                {
                    Unavailable = NoDrawingLayer,
                };
                continue;
            }

            foreach (var key in entry.DrawingLayers)
            {
                string name = tech.Layers.FirstOrDefault(l => l.Key == key)?.Name ?? entry.Name;
                yield return new RailLayerOption(key, name);
            }
        }
    }

    /// <summary>
    /// R-rail27-1d — <b>the copper that belongs to no conductor, said at OPEN and without a run.</b>
    /// </summary>
    /// <remarks>
    /// <b>The extraction already reports this and it is useless here.</b> That report needs a run, a
    /// run needs a confirmed reference, and the missing conductor is exactly why there is no
    /// reference to confirm — circular, with the user inside the circle. This window holds the
    /// flattened shapes and the technology the moment the board opens, so it answers it there.
    ///
    /// <para>Empty on an ordinary board, so nothing is added to a panel that was already right. The
    /// sentence is <c>PdnUnclaimedCopper</c>'s, which is where the extractor's own wording lives —
    /// one fact reported from two places must not come to be worded two ways.</para>
    /// </remarks>
    [ObservableProperty]
    private string _unclaimedCopperNote = "";

    /// <summary>True while <see cref="UnclaimedCopperNote"/> has something to say.</summary>
    public bool HasUnclaimedCopperNote => UnclaimedCopperNote.Length > 0;

    partial void OnUnclaimedCopperNoteChanged(string value)
        => OnPropertyChanged(nameof(HasUnclaimedCopperNote));

    private void RebuildUnclaimedCopperNote() =>
        UnclaimedCopperNote = PdnUnclaimedCopper.Sentence(
            PdnUnclaimedCopper.On(Board?.Technology, Board?.Shapes), Board?.Technology);

    /// <summary>What a conductor with no drawing layer reads in the combo. One spelling, so the row
    /// and the sentence under it cannot come to say different things.</summary>
    internal const string NoDrawingLayer = "no drawing layer";

    /// <summary>The sentence that names the remedy, for a stackup holding such a conductor.</summary>
    /// <remarks>
    /// <b>It names the ACTION and the window it is in.</b> "Attach a drawing layer" is not something
    /// anybody guesses from a combo that simply does not list their plane; Edit Technology is the
    /// button beside the board, which is why it is named here rather than described.
    /// </remarks>
    internal static string UnmappedConductorLine(IReadOnlyList<string> names) =>
        names.Count == 0 ? ""
        : $"{(names.Count == 1 ? "Conductor" : "Conductors")} {string.Join(", ", names.Select(n => $"'{n}'"))} " +
          $"{(names.Count == 1 ? "is" : "are")} in this stackup with no drawing layer attached, so " +
          $"{(names.Count == 1 ? "it cannot" : "they cannot")} be named as a reference — a reference " +
          "is a drawing layer. Press Edit Technology and attach the layer that carries that plane's " +
          "copper.";

    /// <summary>
    /// The reference railRF proposes for <paramref name="rail"/>, and why.
    /// </summary>
    /// <remarks>
    /// <b>It reads the stackup's own <c>IsGroundReference</c> mark and nothing else.</b> That flag is
    /// already what the microstrip PCell's substrate resolution keys on, precisely so an intervening
    /// unmarked signal conductor is never mistaken for ground — the same mistake, on the same data,
    /// and a second rule here would be a second answer to one question. Where the stackup marks
    /// nothing, railRF proposes NOTHING and says so: a guess dressed as a proposal is what Q-8 closed.
    /// </remarks>
    internal static (RailLayerOption? Proposal, string Reason) ProposeReference(
        Technology? tech, RailSpec? rail)
    {
        if (tech is null)
            return (null, "No technology is resolved yet, so railRF has no stackup to propose a "
                        + "reference from. Import the artwork first.");

        var marked = tech.Stackup.Layers
            .Where(l => l.Kind == StackupKind.Conductor && l.IsGroundReference && l.DrawingLayers.Count > 0)
            .ToList();

        // ── A MARKED CONDUCTOR WITH NO DRAWING LAYER IS NOT "NO MARKED CONDUCTOR" ───────────────
        //
        // (owner, 2026-09-21.) The filter above needs the drawing layer, because a proposal is a
        // LayerKey and such a conductor has none — but saying "this stackup marks no conductor as a
        // ground reference" to somebody whose stackup marks one, on the row they ticked themselves,
        // sends them to look for a fault in the flag. The one thing actually missing is the layer,
        // so that is what the sentence asks for.
        var unmapped = tech.Stackup.Layers
            .Where(l => l.Kind == StackupKind.Conductor && l.DrawingLayers.Count == 0)
            .Select(l => l.Name)
            .ToList();

        if (marked.Count == 0)
        {
            bool marksOne = tech.Stackup.Layers.Any(
                l => l.Kind == StackupKind.Conductor && l.IsGroundReference);

            if (marksOne)
                return (null, "This stackup marks a ground reference and it has no drawing layer, so "
                            + "railRF cannot propose it. " + UnmappedConductorLine(unmapped));

            string none = "This stackup marks no conductor as a ground reference, so railRF proposes "
                        + "none — it never infers one. ";
            return (null, unmapped.Count > 0
                ? none + UnmappedConductorLine(unmapped)
                : none + "Name the reference return's drawing layer.");
        }

        var chosen = marked[0];
        var key = chosen.DrawingLayers[0];
        string name = tech.Layers.FirstOrDefault(l => l.Key == key)?.Name ?? chosen.Name;

        string reason = marked.Count == 1
            ? $"'{name}' is the only conductor this stackup marks as a ground reference. Confirm it, "
            + "or pick another — railRF proposes and never assumes."
            : $"'{name}' is the first of {marked.Count} conductors this stackup marks as a ground "
            + "reference. Confirm it, or pick another — railRF proposes and never assumes.";

        // Said even when there IS a proposal: "pick another" is only true of the layers the combo can
        // offer, and a plane that is missing from it is exactly the one somebody would go looking for.
        if (unmapped.Count > 0) reason += " " + UnmappedConductorLine(unmapped);

        return (new RailLayerOption(key, name), reason);
    }

    // ── The target, the band and the extent ────────────────────────────────────────────────────

    /// <summary>
    /// The DC drop budget, in the settable column. Millivolts, because a budget of 0.08 V reads as a
    /// typo where 80 mV does not — which is why the document stores it that way too.
    /// </summary>
    /// <remarks>
    /// <b>An EMPTY field clears the budget; an unparseable one leaves it alone.</b> The two are not
    /// the same gesture: a rail with no stated budget is a rail with the DC question unanswered, which
    /// brief 5 reports rather than substituting a number for — and deleting it by mistyping would be a
    /// target silently removed, which reads afterwards as a design that passes.
    /// </remarks>
    public string DropBudgetEntry
    {
        get => SelectedRail?.DropBudget?.DropBudgetMillivolts is { } mv
            ? $"{RailValueFormat.Significant(mv, 4)} mV" : "";
        set
        {
            if (SelectedRail is { } rail)
            {
                if (string.IsNullOrWhiteSpace(value)) rail.DropBudget = null;
                else if (RailValueFormat.TryParse(value, RailQuantity.Voltage, out double v))
                    rail.DropBudget = RailTarget.OfDropBudget(v * 1e3);
            }
            OnPropertyChanged();
            QueueResolve();
        }
    }

    /// <summary>The flat impedance target, in milliohms. Empty clears it; unparseable leaves it —
    /// see <see cref="DropBudgetEntry"/>.</summary>
    /// <remarks>
    /// <b>It re-solves, like the budget beside it</b> (owner, 2026-09-19). It did not, and this is
    /// the one field on the window whose value is DRAWN: the mask is a trace on the |Z| plot, built
    /// from <c>PdnSweepResult</c>'s own ports, so a target typed and committed left the old ceiling
    /// on the picture and the old verdict in the mask table — a design judged against a number the
    /// field no longer showed. §2.3 step 6 is "the result follows the edit", and this is an edit.
    /// </remarks>
    public string ImpedanceTargetEntry
    {
        get => SelectedRail?.ImpedanceTarget?.FlatMilliohms is { } mo
            ? $"{RailValueFormat.Significant(mo, 4)} mΩ" : "";
        set
        {
            if (SelectedRail is { } rail)
            {
                if (string.IsNullOrWhiteSpace(value)) rail.ImpedanceTarget = null;
                else if (RailValueFormat.TryParse(value, RailQuantity.Resistance, out double r))
                    rail.ImpedanceTarget = RailTarget.OfFlatImpedance(r * 1e3);
            }
            OnPropertyChanged();
            QueueResolve();
        }
    }

    /// <summary>The bottom of the band Z(f) is answered over.</summary>
    /// <remarks>
    /// <b>And it re-solves</b>, for <see cref="ImpedanceTargetEntry"/>'s reason and more sharply: the
    /// band IS the plot's own X axis, so a band edit that did not re-sweep left a curve of the old
    /// band under an axis nothing had changed — every anti-resonance, every coincidence and every
    /// mask verdict still of the band the user had just replaced.
    /// </remarks>
    public string BandStartEntry
    {
        get => SelectedRail is { } r ? RailValueFormat.FormatWithUnit(r.Band.StartHz, RailQuantity.Frequency) : "";
        set
        {
            if (SelectedRail is not { } rail) return;
            if (RailValueFormat.TryParse(value, RailQuantity.Frequency, out double hz))
                rail.Band = rail.Band with { StartHz = hz };
            OnPropertyChanged();
            QueueResolve();
        }
    }

    /// <summary>The top of it.</summary>
    /// <inheritdoc cref="BandStartEntry"/>
    public string BandStopEntry
    {
        get => SelectedRail is { } r ? RailValueFormat.FormatWithUnit(r.Band.StopHz, RailQuantity.Frequency) : "";
        set
        {
            if (SelectedRail is not { } rail) return;
            if (RailValueFormat.TryParse(value, RailQuantity.Frequency, out double hz))
                rail.Band = rail.Band with { StopHz = hz };
            OnPropertyChanged();
            QueueResolve();
        }
    }

    /// <summary>
    /// What the reference conductor is taken to be.
    /// </summary>
    /// <remarks>
    /// <b>Behind <c>Settings</c>, not on the face of the window</b> (§11.2). It is an advanced choice
    /// two of whose three values are OPTIMISTIC, and the status strip states the one in force on every
    /// frame — which is the part that has to be unmissable, not the control.
    /// </remarks>
    public RailReferenceExtent ReferenceExtent
    {
        get => SelectedRail?.ReferenceExtent ?? RailReferenceExtent.AsImported;
        set
        {
            if (SelectedRail is not { } rail || rail.ReferenceExtent == value) return;
            rail.ReferenceExtent = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusLine));
            QueueResolve();
        }
    }

    /// <summary>The three extents, for the Settings combo.</summary>
    public IReadOnlyList<RailReferenceExtent> ReferenceExtents { get; } =
        [.. Enum.GetValues<RailReferenceExtent>()];

    // ── The rest of §11.2's list, behind Settings and nowhere else ─────────────────────────────
    //
    // EVERY ONE OF THESE IS STATED ON THE REPORT, which is the whole reason they are document state
    // rather than session state: §2.7's "everything is computed at 20 °C and every report says so" is
    // only true if the 20 is a number the report can read. RailSettings' own note says the same.

    /// <summary>
    /// The plated barrel thickness a via's current limit is computed from.
    /// </summary>
    /// <remarks>
    /// <b>Empty is not a default</b> (Q-22): brief 6 reads the thickness from the stackup's via entry
    /// where one states it, takes this where it does not, and falls back to the drill-size table as a
    /// sanity band — and every flag says which basis produced it. A number typed here by the control
    /// itself would make all three indistinguishable on the report, which is why clearing the field
    /// puts the null back rather than leaving the last value standing.
    /// </remarks>
    public string ViaPlatingEntry
    {
        get => _document.Settings.ViaPlatingThicknessMicrometres is { } um
            ? $"{RailValueFormat.Significant(um, 4)} µm" : "";
        set
        {
            string trimmed = (value ?? "").Trim();
            _document.Settings.ViaPlatingThicknessMicrometres =
                trimmed.Length == 0 ? null
                : NumericText.TryParseDouble(trimmed.TrimEnd('µ', 'u', 'm', ' '), out double um)
                    ? um : _document.Settings.ViaPlatingThicknessMicrometres;
            OnPropertyChanged();
            QueueResolve();
        }
    }

    /// <summary>
    /// The temperature RISE a via's current limit is stated at, in °C of rise.
    /// </summary>
    /// <remarks>
    /// <b>A budget, not a temperature.</b> There is no thermal model anywhere in railRF and this is
    /// not one: the via check computes a limit from the barrel's own annulus, its span and this
    /// number, and nothing else in railRF reads it.
    /// </remarks>
    public string ViaTemperatureRiseEntry
    {
        get => $"{RailValueFormat.Significant(_document.Settings.ViaTemperatureRiseCelsius, 4)} °C";
        set
        {
            if (ParseCelsius(value) is { } c) _document.Settings.ViaTemperatureRiseCelsius = c;
            OnPropertyChanged();
            QueueResolve();
        }
    }

    /// <summary>
    /// The one stated temperature every resistance in this document is computed at.
    /// </summary>
    /// <remarks>
    /// <b>Not a thermal model either</b> (§2.7). It is the single stated basis the copper conductivity
    /// is taken at, so a report can say which — and the status strip says it on every frame for the
    /// same reason.
    /// </remarks>
    public string TemperatureEntry
    {
        get => $"{RailValueFormat.Significant(_document.Settings.CopperTemperatureCelsius, 4)} °C";
        set
        {
            if (ParseCelsius(value) is { } c) _document.Settings.CopperTemperatureCelsius = c;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusLine));
            QueueResolve();
        }
    }

    /// <summary>
    /// How finely <c>Accuracy</c> meshes, as a cell size.
    /// </summary>
    /// <remarks>
    /// <b>Read by the accurate reading only</b> — the fast one traces instead, and a meshing density
    /// on the face of the window would read as a knob that changes the default answer. It does not.
    /// </remarks>
    public string MeshCellEntry
    {
        // IN THE BOARD'S OWN UNITS, read and written (owner, 2026-09-18). It was micrometres
        // unconditionally, which on a PCB drawn in mils or millimetres is a field asking for a number
        // in a unit nothing else on the window uses. LayoutUnits.TryParse still accepts an explicit
        // suffix, so a user who types "0.2 mm" into a µm board is understood.
        get => MeshCellMetres is { } m ? BoardLengthFormat().Metres(m) : "";
        set
        {
            string trimmed = (value ?? "").Trim();
            _meshCellMetres = trimmed.Length == 0
                ? null
                : BoardLengthFormat().ParseMetres(trimmed) ?? _meshCellMetres;
            OnPropertyChanged();
        }
    }

    private double? _meshCellMetres;

    /// <summary>The cell size <c>Accuracy</c> is asked for, in METRES — <c>PdnMeshSettings</c>'
    /// own unit — or null for the extractor's computed one, which is the ordinary case.</summary>
    internal double? MeshCellMetres => _meshCellMetres;

    private static double? ParseCelsius(string? text)
    {
        string trimmed = (text ?? "").Trim().TrimEnd('C', 'c', '°', ' ');
        return NumericText.TryParseDouble(trimmed, out double c)
            ? c : null;
    }

    private void RefreshTargets()
    {
        OnPropertyChanged(nameof(DropBudgetEntry));
        OnPropertyChanged(nameof(ImpedanceTargetEntry));
        OnPropertyChanged(nameof(BandStartEntry));
        OnPropertyChanged(nameof(BandStopEntry));
        OnPropertyChanged(nameof(ReferenceExtent));
    }

    // ── The board tab strip and the results tabs ───────────────────────────────────────────────

    /// <summary>
    /// Which map the board is showing. <b>Switching it switches the OVERLAY and never the
    /// geometry</b> — brief 8 draws each one; what is here is which one it is asked for.
    /// </summary>
    [ObservableProperty]
    private RailBoardOverlay _selectedBoardOverlay = RailBoardOverlay.Copper;

    /// <summary>The four tabs, in the strip's own order.</summary>
    public IReadOnlyList<RailBoardOverlay> BoardOverlays { get; } = [.. Enum.GetValues<RailBoardOverlay>()];

    /// <summary>Which half of the results column is showing.</summary>
    [ObservableProperty]
    private RailResultsTab _selectedResultsTab = RailResultsTab.Dc;

    /// <summary>Both tabs.</summary>
    public IReadOnlyList<RailResultsTab> ResultsTabs { get; } = [.. Enum.GetValues<RailResultsTab>()];

    partial void OnSelectedResultsTabChanged(RailResultsTab value) => AnnounceCardVisibility();

    // ── R-rail18-6a: the strip PARTITIONS the column ───────────────────────────────────────────
    //
    // Until this, SelectedResultsTab's only reader was RailRfWindow.SyncTabs, which sets the two
    // ToggleButtons' IsChecked and nothing else: every card lived in one ScrollViewer gated on its
    // own Has… property, so pressing `frequency` lit a button and changed nothing. Nothing was
    // hidden and nothing was wrong — it simply is not what a tab MEANS, and a control that looks
    // like it filters and does not is one a user stops trusting.
    //
    // GATED rather than removed, which §11.3 gives the strip as a control rather than as decoration
    // supports: the column is long enough that a reader looking for the mask verdict scrolls past
    // four DC cards to reach it.
    //
    // Each card is `Has… && tab == …`, so a card with nothing to say still does not appear — the
    // tab narrows what is shown and never forces an empty card into view.

    /// <summary>The stackup readout — <b>DC</b>. §4.1's shunt branch is not stamped at ω = 0 and the
    /// number is still the cheapest check in the tool, which is why it is on the DC half at all.</summary>
    public bool ShowStackupCard => IsDc && HasPlaneCapacitance;

    /// <summary>The per-port drop — DC.</summary>
    public bool ShowDropCard => IsDc;

    /// <summary>The ranked breakdown — DC.</summary>
    public bool ShowBreakdownCard => IsDc;

    /// <summary>The via current check — DC.</summary>
    public bool ShowViaCheckCard => IsDc;

    /// <summary>
    /// The coincidence list — <b>frequency</b>.
    /// </summary>
    /// <remarks>
    /// It reads as a DC-side observation on the shipped example, and it is not one: a coincidence is
    /// between a SWEPT peak and an aggressor's harmonic, and neither half of that exists in a DC
    /// answer.
    /// </remarks>
    public bool ShowCoincidencesCard => IsFrequency && HasCoincidences;

    /// <summary>The verdict against the target mask — frequency.</summary>
    public bool ShowMaskCard => IsFrequency && HasMaskVerdict;

    /// <summary>The anti-resonance table — frequency.</summary>
    public bool ShowAntiResonancesCard => IsFrequency && HasAntiResonances;

    /// <summary>The removal ranking — frequency.</summary>
    public bool ShowRemovalCard => IsFrequency && HasRemovalRanking;

    /// <summary>The cavity mode card and its own Find button — frequency.</summary>
    public bool ShowPlaneResonancesCard => IsFrequency;

    /// <summary>Whatever the frequency answer has to say that is not a curve — frequency.</summary>
    public bool ShowImpedanceMessageCard => IsFrequency && HasImpedanceMessage;

    private bool IsDc => SelectedResultsTab == RailResultsTab.Dc;
    private bool IsFrequency => SelectedResultsTab == RailResultsTab.Frequency;

    /// <summary>
    /// Re-raises every card's visibility. Called from the tab change and from each of the three
    /// blocks that announce a new result — <b>explicitly, so the coupling is greppable</b> rather
    /// than inferred from a property name.
    /// </summary>
    private void AnnounceCardVisibility()
    {
        OnPropertyChanged(nameof(ShowStackupCard));
        OnPropertyChanged(nameof(ShowDropCard));
        OnPropertyChanged(nameof(ShowBreakdownCard));
        OnPropertyChanged(nameof(ShowViaCheckCard));
        OnPropertyChanged(nameof(ShowCoincidencesCard));
        OnPropertyChanged(nameof(ShowMaskCard));
        OnPropertyChanged(nameof(ShowAntiResonancesCard));
        OnPropertyChanged(nameof(ShowRemovalCard));
        OnPropertyChanged(nameof(ShowPlaneResonancesCard));
        OnPropertyChanged(nameof(ShowImpedanceMessageCard));
    }

    public void Dispose()
    {
        CancelInFlight();
        CancelBoardOpen();
        PlotHost.Dispose();
    }
}
