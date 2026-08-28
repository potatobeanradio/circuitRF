using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using CircuitRF.Core.Matching;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;

namespace CircuitRF.Ui.Matching;

/// <summary>
/// The Match Designer (docs/design/match.md §9): specification, live ladder, response plots, the
/// linked Norton-transform rack, and the solutions list.
///
/// <h3>It adds no synthesis</h3>
/// <para>Every number here comes out of <c>src/Core/Match</c>. This class decides <i>when</i> to
/// rebuild and <i>what</i> to say; it never decides what an element value is. If a formula appears in
/// this file it is in the wrong place.</para>
///
/// <h3>Nothing lives only in the view-model</h3>
/// <para>Brief §0.3: pi/T choice, every N, every lock, the link state, the applied solution and the
/// Q-adjust all have to survive a save and a reload. They do because every one of them is a property
/// of <see cref="MatchDesign"/> and every committed edit writes the whole design back to the
/// component's <c>Design</c> parameter through one <c>SetParametersCommand</c> — which is also what
/// makes a Designer edit undoable from the schematic's own stack. The only state this class owns is
/// display state (which pane is showing, whether the solutions panel is out) and the settings of
/// §9.9, which are display choices and deliberately NOT part of the design.</para>
///
/// <h3>What is expensive, and when it runs</h3>
/// <para>A rebuild is cheap and runs on every edit. Two things are not, and neither depends on a
/// transform's N, so both run only when the SPECIFICATION changes — and, since 2026-08-20, both run
/// on a worker rather than in front of the user's typing. <c>MatchDesignerViewModel.Analysis.cs</c>
/// holds them and records what they measured:</para>
/// <list type="bullet">
/// <item><b>Response feasibility.</b> Butterworth and Bessel go through
///   <c>MatchPrototypes.Search</c>, a 33-point shape sweep with two refinement rounds, each step
///   building a ladder and scoring it over 201 frequencies. Four of those per keystroke is not
///   affordable and buys nothing: dragging a slider cannot make Bessel feasible.</item>
/// <item><b>The solution search.</b> Same reasoning, and match.md §13.3 warns about it by name — the
///   reference implementation re-runs it inside a view body, which is affordable on a four-element
///   network and would not be here.</item>
/// </list>
/// <para><b>Everything else stays synchronous</b>, because everything else is about 1.5 ms: the
/// rebuild, the transform rows, the ladder, the grid, the status strip and the response plots all
/// still land on the same frame as the keystroke that caused them.</para>
/// </summary>
public sealed partial class MatchDesignerViewModel : ObservableObject, IDisposable
{
    private SchematicViewModel? _schematicVm;
    private EditableComponent?  _target;
    private UndoRedoStack?      _hookedStack;

    private MatchDesign _design = MatchEmbedding.DefaultDesign();
    private MatchDesign _openingDesign = MatchEmbedding.DefaultDesign();
    private MatchRebuildResult? _rebuild;
    private bool _isCommitting;
    private bool _isDragging;
    private bool _plotsStaleFromDrag;

    /// <summary>Undo and redo, delegated to the owning schematic's stack — the Designer has none.</summary>
    public IRelayCommand UndoCommand { get; }

    /// <inheritdoc cref="UndoCommand"/>
    public IRelayCommand RedoCommand { get; }

    /// <summary>Builds an unbound Designer; call <see cref="SetTarget"/> before using it.</summary>
    public MatchDesignerViewModel()
    {
        UndoCommand = new RelayCommand(
            () => _schematicVm?.UndoRedo.Undo(), () => _schematicVm?.UndoRedo.CanUndo ?? false);
        RedoCommand = new RelayCommand(
            () => _schematicVm?.UndoRedo.Redo(), () => _schematicVm?.UndoRedo.CanRedo ?? false);

        BuildPlotHost();   // the two response containers — see MatchDesignerViewModel.Response.cs

        Term1 = new MatchTerminationViewModel(this, 1);
        Term2 = new MatchTerminationViewModel(this, 2);
        Settings = new MatchDesignerSettings();
        Settings.PropertyChanged += OnSettingsChanged;

        ResponseOptions =
        [
            // match.md §6.9: the names say the OUTCOME, not the derivation. "single-match" and
            // "double-match" are the broadband-matching literature's own terms for one prescribed
            // reactive termination against two, which is exactly what separates these two prototypes
            // — and unlike "(Fano)" and "(2-ended)" they tell a user which one they want. The
            // eponyms move into the tooltips, where a credit belongs. Display only: the enum members
            // and the serialized spelling are untouched.
            new(ResponseShape.ChebyshevFano, "Chebyshev — single-match (optimum)",
                "Best return loss at this order; may add a surplus element at the far end. Levy's "
                + "recursion with Dawson's optimum root."),
            new(ResponseShape.ChebyshevTwoEnded, "Chebyshev — double-match (exact)",
                "Absorbs both terminations exactly and never adds an element; slightly lower return "
                + "loss. Levy 1964."),
            new(ResponseShape.Butterworth, "Butterworth",
                "Maximally-flat magnitude, through the numerical route. Roughly half the "
                + "group-delay variation of the Chebyshev design."),
            new(ResponseShape.Bessel, "Bessel",
                "Maximally-flat group delay. Feasible as a prototype and usually refused by the far "
                + "end, which is why the refusal names its numbers."),
        ];

        // After ResponseOptions: the filter's family lines are one per entry, in the same order.
        BuildFilter();
    }

    // ── Binding ───────────────────────────────────────────────────────────────

    /// <summary>Binds this Designer to one placed <c>Match</c>.</summary>
    public void SetTarget(SchematicViewModel schematicVm, EditableComponent comp)
    {
        ArgumentNullException.ThrowIfNull(schematicVm);
        ArgumentNullException.ThrowIfNull(comp);

        Detach();

        _schematicVm = schematicVm;
        _target = comp;
        _schematicVm.EditModel.Changed += OnModelChanged;
        HookStack(schematicVm);

        LoadFromComponent();
        _openingDesign = _design.Clone();
        Refresh(specChanged: true);
        RefreshProbeAvailability();
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(InstanceName));
    }

    /// <summary>
    /// Binds this Designer to <b>nothing</b> — the Tools ▸ Match Designer entry point.
    /// </summary>
    /// <remarks>
    /// <b>Owner, 2026-08-20:</b> <i>"add a Match Designer to the circuitRF Tools menu. When selected,
    /// an 'orphaned' Designer window appears that still allows user to author a design and Flatten to
    /// Cell."</i>
    ///
    /// <para><b>This is NOT <see cref="IsOrphaned"/>, and conflating the two would be the bug.</b>
    /// An orphaned Designer is one whose component was DELETED: it is deliberately frozen and
    /// read-only, because writing to a component that is no longer in the drawing is the thing that
    /// must not happen. A standalone one has no component to write to in the first place, so there is
    /// nothing to protect — every setter runs, and <see cref="Commit"/> is simply a no-op, which it
    /// already was for a null target. The design lives in this window and leaves it as a CELL.</para>
    ///
    /// <para>Three things a standalone Designer does not have, each shut off where it is decided
    /// rather than at every call site: <b>undo/redo</b> (there is no schematic stack — Revert still
    /// restores the design this window opened with), <b>Probe</b> (<c>MatchProbeAvailability</c>
    /// answers <c>NoSchematic</c> from a null model, which is exactly what this is), and
    /// <b>Replace-in-place</b> on flatten (there is no instance to replace).</para>
    /// </remarks>
    /// <param name="workspaceRoot">
    /// The open workspace's root, or null. Only ever a STARTING FOLDER for the flatten prompt: a
    /// standalone Designer writes wherever the user points it, because the cell it produces is not
    /// referenced from any schematic and so has nothing to be relative to.
    /// </param>
    public void SetStandalone(string? workspaceRoot)
    {
        Detach();

        _schematicVm = null;
        _target = null;
        _isStandalone = true;
        _standaloneRoot = workspaceRoot;
        HookStack(null);

        _design = MatchEmbedding.DefaultDesign();
        PayloadError = "";
        _openingDesign = _design.Clone();
        Refresh(specChanged: true);
        RefreshProbeAvailability();
        OnPropertyChanged(nameof(IsStandalone));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(InstanceName));
    }

    private bool _isStandalone;
    private string? _standaloneRoot;

    /// <summary>True for a Designer opened from Tools ▸ Match Designer, with no component behind it.</summary>
    public bool IsStandalone => _isStandalone;

    /// <summary>The component this Designer edits — one window per instance keys on it.</summary>
    public EditableComponent? Target => _target;

    /// <summary>The instance name, "MN1".</summary>
    /// <remarks>
    /// A standalone Designer has no instance, and "MN1" is what it calls itself: the name seeds the
    /// flattened cell's default name and the labels the ladder draws, both of which need a word.
    /// </remarks>
    public string InstanceName => _target?.InstanceName ?? (_isStandalone ? "MN1" : "");

    /// <summary>The schematic this Designer's component lives on — "matchedRFTest.csch", or "".</summary>
    public string DocumentName => _schematicVm?.DocumentName ?? "";

    /// <summary>
    /// "Match — MN1 — matchedRFTest.csch", or "Match Designer" when there is no instance to name.
    /// </summary>
    /// <remarks>
    /// <b>Owner, 2026-08-20:</b> <i>"add the name of the schematic (in addition to the MN instance
    /// name) to the window title and the text in the top left of the window."</i> One string serves
    /// all three surfaces — the OS window title, the pane's own title bar, and the application's
    /// Window menu entry (<c>MatchDesignerWindow.WindowMenuHeader</c>) — which is why it is stated
    /// once here and not formatted at any of them. A Designer opened on a schematic with no file yet
    /// (a scratch tab) names the tab; one opened with no schematic at all names neither.
    /// </remarks>
    public string Title => _isStandalone
        ? "Match Designer"
        : DocumentName.Length == 0
            ? $"Match — {InstanceName}"
            : $"Match — {InstanceName} — {DocumentName}";

    /// <summary>The working design. <b>Read it; write it only through this class's setters</b>, which
    /// rebuild and commit.</summary>
    public MatchDesign Design => _design;

    /// <summary>Display and search settings (§9.9). Not part of the design.</summary>
    public MatchDesignerSettings Settings { get; }

    // ── Pane expansion (owner, 2026-08-20) ────────────────────────────────────

    /// <summary>
    /// True while the network + transforms column has been given the response pane's width.
    /// </summary>
    /// <remarks>
    /// The two expanders are <b>mutually exclusive</b> rather than independent, and they have to be:
    /// each one takes the OTHER pane's column, so both on at once is a state with no width left to
    /// describe. Setting either turns the other off here, once, instead of in the two toggles that
    /// bind to them.
    /// </remarks>
    [ObservableProperty] private bool _networkExpanded;

    /// <inheritdoc cref="NetworkExpanded"/>
    [ObservableProperty] private bool _responseExpanded;

    partial void OnNetworkExpandedChanged(bool value)
    {
        if (value) ResponseExpanded = false;
        OnPropertyChanged(nameof(NetworkExpandIcon));
        OnPropertyChanged(nameof(NetworkExpandTooltip));
    }

    partial void OnResponseExpandedChanged(bool value)
    {
        if (value) NetworkExpanded = false;
        OnPropertyChanged(nameof(ResponseExpandIcon));
        OnPropertyChanged(nameof(ResponseExpandTooltip));
    }

    /// <summary>
    /// The network expander's glyph — <b>the arrow points where the pane is about to go</b>
    /// (owner, 2026-08-20: "the button icon shows state").
    /// </summary>
    /// <remarks>
    /// Typed rather than a string the binding would have to parse into the enum: a misspelt glyph
    /// name is then a build error instead of a blank square nobody notices until the screenshot.
    /// </remarks>
    public MaterialIconKind NetworkExpandIcon =>
        NetworkExpanded ? MaterialIconKind.ArrowTopLeft : MaterialIconKind.ArrowBottomRight;

    /// <inheritdoc cref="NetworkExpandIcon"/>
    public MaterialIconKind ResponseExpandIcon =>
        ResponseExpanded ? MaterialIconKind.ArrowTopRight : MaterialIconKind.ArrowBottomLeft;

    /// <summary>What the network expander offers, in the state it is in.</summary>
    public string NetworkExpandTooltip => NetworkExpanded
        ? "Give the response pane its width back"
        : "Expand the schematic and transforms over the response pane";

    /// <inheritdoc cref="NetworkExpandTooltip"/>
    public string ResponseExpandTooltip => ResponseExpanded
        ? "Give the schematic and transforms their width back"
        : "Expand the response over the schematic and transforms";

    /// <summary>The result of the last rebuild — the source of every number on screen.</summary>
    public MatchRebuildResult? Rebuild => _rebuild;

    private void LoadFromComponent()
    {
        string payload = _target?.Parameters
            .FirstOrDefault(p => p.Name == MatchEmbedding.DesignParameter)?.Expression ?? "";

        if (MatchEmbedding.TryDecode(payload, out var decoded) && decoded is not null)
        {
            _design = decoded;
            PayloadError = "";
            CheckEchoParameters();
        }
        else
        {
            // An unreadable payload is a reported, repairable state — never a crash and never a
            // silently-substituted design that would overwrite the user's on the next commit.
            _design = MatchEmbedding.DefaultDesign();
            PayloadError = payload.Length == 0
                ? "This Match carries no design; the default 1–2 GHz, order 3, 50 Ω network is shown."
                : "This Match's Design parameter could not be decoded. The default network is shown; "
                  + "nothing has been written back, so the stored payload is still there to repair.";
        }
    }

    /// <summary>
    /// Reports an ECHO parameter that no longer agrees with the design — <b>never rewrites one</b>.
    /// </summary>
    /// <remarks>
    /// <b>Owner, 2026-08-20:</b> <i>"why is there an 'F1' and 'F2' parameter for a Match component,
    /// but also a nested 'F1' and 'F2'? Why are we carrying two versions of these 2 variables?"</i>
    ///
    /// <para>The six beside <c>Design</c> are ECHOES and nothing reads them back
    /// (<c>MatchComponentTests.TheEchoParameters_AreNeverReadBack</c> pins that). They exist because
    /// an instance has to CARRY a value to be able to show one: F1, F2 and Order are drawn beside the
    /// symbol, and all six make the <c>.cnl</c> line legible, where the payload is still a base64
    /// token. The design stays authoritative (match.md §7.2), so an echo can never become a second
    /// input.</para>
    ///
    /// <para><b>What changed on 2026-08-20 is that the payload became readable and therefore
    /// EDITABLE by hand</b>, and a hand-edited band leaves the echoes behind — so the schematic would
    /// go on drawing "F1 = 1.8 GHz" beside a design that says 2.4. That was unreachable while the
    /// payload was base64 and it is reachable now, so it is stated. It is deliberately not fixed by
    /// writing the echoes here: a commit on load would put an edit on the schematic's undo stack that
    /// nobody made and mark the document dirty the moment it is opened. The next edit refreshes them,
    /// and this line says so.</para>
    /// </remarks>
    private void CheckEchoParameters()
    {
        EchoNote = "";
        if (_target is null) return;

        var stale = new List<string>(3);
        Check("F1", _design.F1 / 1e9);
        Check("F2", _design.F2 / 1e9);
        Check("Order", _design.Order);

        if (stale.Count > 0)
            EchoNote =
                $"The {string.Join(" and ", stale)} shown beside this component on the schematic "
                + $"{(stale.Count == 1 ? "does" : "do")} not match the design — the design is what "
                + "counts, and the label will catch up on the next edit here.";

        void Check(string name, double expected)
        {
            string? text = _target!.Parameters.FirstOrDefault(p => p.Name == name)?.Expression;
            if (text is null) return;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                return;                                   // an expression, not a number — not ours to judge
            if (Math.Abs(v - expected) > 1e-6 * Math.Max(1.0, Math.Abs(expected))) stale.Add(name);
        }
    }

    /// <summary>An echo parameter that has fallen behind the design, or empty.</summary>
    [ObservableProperty] private string _echoNote = "";

    private void Detach()
    {
        if (_schematicVm is not null) _schematicVm.EditModel.Changed -= OnModelChanged;
        if (_hookedStack is not null) _hookedStack.PropertyChanged -= OnStackChanged;
        _hookedStack = null;
    }

    private void HookStack(SchematicViewModel? vm)
    {
        if (_hookedStack is not null) _hookedStack.PropertyChanged -= OnStackChanged;
        _hookedStack = vm?.UndoRedo;
        if (_hookedStack is not null) _hookedStack.PropertyChanged += OnStackChanged;
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        RaiseUndoState();
    }

    private void OnStackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UndoRedoStack.CanUndo)) UndoCommand.NotifyCanExecuteChanged();
        if (e.PropertyName is nameof(UndoRedoStack.CanRedo)) RedoCommand.NotifyCanExecuteChanged();
        RaiseUndoState();
    }

    /// <summary>True when there is something on the owning schematic's stack to undo.</summary>
    /// <remarks>
    /// <b>The title strip's two buttons bind their <c>IsEnabled</c> to this, not to their command's
    /// <c>CanExecute</c></b> (owner-reported, 2026-08-28: they were always disabled). Binding
    /// <c>Command</c> alone leaves a Button's enablement to reach it through
    /// <c>ICommand.CanExecuteChanged</c>, and the first evaluation happens when the binding attaches —
    /// which here is after <c>SetTarget</c> has already run, with an empty stack and therefore a
    /// false answer. Every other gated button in this window states its enablement outright for the
    /// same reason (Probe reads <c>CanProbe</c>, the scroll-to-applied button reads
    /// <c>HasAppliedSolution</c>), and this is that pattern rather than a second mechanism beside it.
    ///
    /// <para>The commands are still notified, so the keyboard path and any menu binding stay right.
    /// This is the half the strip renders from.</para>
    /// </remarks>
    public bool CanUndo => _hookedStack?.CanUndo ?? false;

    /// <inheritdoc cref="CanUndo"/>
    public bool CanRedo => _hookedStack?.CanRedo ?? false;

    private void RaiseUndoState()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoTooltip));
        OnPropertyChanged(nameof(RedoTooltip));
    }

    /// <summary>
    /// What the title strip's Undo button offers, <b>named</b> — the schematic stack's own
    /// <see cref="UndoRedoStack.UndoDescription"/>, so the Designer and the application's Edit menu
    /// say the same words about the same entry.
    /// </summary>
    /// <remarks>
    /// The buttons are new (owner, 2026-08-28); the commands behind them are not, and neither is
    /// where they go. A Designer edit IS a schematic edit — <see cref="Commit"/> writes one
    /// <c>SetParametersCommand</c> onto the owning schematic's stack — so this window has never had a
    /// history of its own and must not grow one: two stacks would be two answers to "what did I just
    /// do", and the schematic's is the one the drawing obeys.
    ///
    /// <para>A STANDALONE Designer has no schematic and therefore no stack, which is why both
    /// buttons are simply unavailable there rather than wired to something local. Revert is what that
    /// window has instead, and it restores the design the window opened with.</para>
    /// </remarks>
    public string UndoTooltip =>
        _hookedStack is { CanUndo: true } s ? s.UndoDescription : "Nothing to undo";

    /// <inheritdoc cref="UndoTooltip"/>
    public string RedoTooltip =>
        _hookedStack is { CanRedo: true } s ? s.RedoDescription : "Nothing to redo";

    /// <summary>
    /// The design changed underneath us — an undo, a redo, or another editor. Re-read it rather than
    /// keep showing the one we last wrote, which is the specific failure
    /// <c>ParameterRowStaleBindingTests</c> pins for the generic editor.
    /// </summary>
    private void OnModelChanged(object? sender, EventArgs e)
    {
        if (_isCommitting) return;
        if (CheckOrphaned()) return;
        LoadFromComponent();
        Refresh(specChanged: true);
        // A Save As renames the document under a live Designer. Nothing raises this at the moment the
        // rename happens — the window is not a document tab — so it is re-read on the next model
        // change, which is the first point anything in here could be looking at the new file anyway.
        OnPropertyChanged(nameof(DocumentName));
        OnPropertyChanged(nameof(Title));
        // Availability depends on the SCHEMATIC and on nothing in the design, so it is recomputed
        // here and not in Refresh: a band edit or a slider cannot change what is wired to a pin, and
        // an extraction per keystroke on a hierarchical schematic reads every referenced cell off disk.
        RefreshProbeAvailability();
    }

    /// <summary>
    /// True once the component this Designer edits has been deleted from its schematic.
    /// </summary>
    /// <remarks>
    /// <b>The window is left OPEN and readable</b> (owner, 2026-08-20: "what happens if user goes back
    /// to schematic and deletes its instance? Perhaps the window becomes orphaned? I am ok with that
    /// — just need to handle it gracefully"). Closing it out from under the user would discard a
    /// design they may still want to read the numbers off, and an undo of the delete would then have
    /// no window to come back to. So the design freezes exactly as it stands and every path that
    /// WRITES is shut off at its one choke point — <see cref="Commit"/> — rather than at each of the
    /// two dozen setters that call it. Nothing is written to a component that is no longer in the
    /// drawing, and nothing pretends to have been.
    ///
    /// <para>It is not permanent: an undo puts the component back, <see cref="CheckOrphaned"/> sees it
    /// again on the next model change, and the Designer picks up where it left off.</para>
    /// </remarks>
    [ObservableProperty] private bool _isOrphaned;

    /// <summary>The one line an orphaned Designer says, or empty.</summary>
    public string OrphanNote => IsOrphaned
        ? $"{InstanceName} has been deleted from its schematic. This window is now read-only — "
          + "nothing it shows will be written back. Undo the deletion to make it live again."
        : "";

    partial void OnIsOrphanedChanged(bool value)
    {
        OnPropertyChanged(nameof(OrphanNote));
        OnPropertyChanged(nameof(CanFlatten));
        Term1.RefreshProbeState();
        Term2.RefreshProbeState();
    }

    /// <summary>
    /// Re-tests whether the target component is still in the schematic, and flips
    /// <see cref="IsOrphaned"/> when the answer changed. Returns true when the Designer is orphaned
    /// and the caller should stop.
    /// </summary>
    private bool CheckOrphaned()
    {
        if (_schematicVm is null || _target is null) return IsOrphaned;

        bool gone = _schematicVm.EditModel.FindComponent(_target.Id) is null;
        if (gone != IsOrphaned)
        {
            IsOrphaned = gone;
            RefreshFlatten();
        }
        return gone;
    }

    /// <summary>Unhooks everything. Safe to call twice.</summary>
    public void Dispose()
    {
        _probeCts?.Cancel();
        _probeCts?.Dispose();
        _probeCts = null;
        CancelAnalysis();
        Detach();
        // The plot host subscribes to a PROCESS-WIDE settings singleton; a Designer is opened and
        // closed per component, so not dropping it would strand one display per open.
        PlotHost.Dispose();
        Settings.PropertyChanged -= OnSettingsChanged;
        _schematicVm = null;
        _target = null;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Units and digits are display only — re-render, do not rebuild and do not commit. Qmin is
        // part of MatchSpecKey and so changes what the SEARCH finds, which re-runs it.
        //
        // "Offer Q-adjusted solutions" used to be here beside it and is gone (owner, 2026-08-28:
        // "remove Offer Q-adjusted solutions from the Settings button menu"). It was a SEARCH input
        // that decided whether §4.6's candidates were ever computed; the solutions filter's own
        // Q-adjusted toggle is a view over an answer that always includes them, which is the same
        // choice made where the user is looking at its result and at no cost when it is flipped.
        bool searchAffecting = e.PropertyName is nameof(MatchDesignerSettings.QMin);
        Refresh(specChanged: searchAffecting);
    }

    // ── The specification ─────────────────────────────────────────────────────

    /// <summary>Termination 1 — the port-1 end.</summary>
    public MatchTerminationViewModel Term1 { get; }

    /// <summary>Termination 2 — the port-2 end.</summary>
    public MatchTerminationViewModel Term2 { get; }

    /// <summary>An unreadable or absent <c>Design</c> payload, stated rather than hidden.</summary>
    [ObservableProperty] private string _payloadError = "";

    /// <summary>
    /// Replaces one termination, adjusting the order when the new parity demands it.
    /// </summary>
    /// <param name="fromProbe">
    /// True only when MN-4's probe supplied this. <b>Every other path clears the probed badge</b>,
    /// which is match.md §10.5's rule stated once in the one place every edit goes through: the
    /// user's override always wins, and a hand-edited value must never keep claiming a provenance it
    /// no longer has.
    /// </param>
    internal void SetTermination(int end, Termination replacement, bool fromProbe = false)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (!fromProbe && replacement.Probed)
            replacement = replacement with { Probed = false, ProbedAtUtc = null };
        if (end == 1) _design.Term1 = replacement; else _design.Term2 = replacement;
        AdjustOrderForParity();
        AdjustQAdjustForAnalysisEnd();

        // Re-declaring an end moves the ratio the rack has to reach, and the stored N's are what they
        // were. RelinkAfterSpecChange absorbs that whenever it can; when it cannot, this asks the
        // solution search for a rack that does reach it. See AutoApplyAReachingSolution.
        //
        // The flag lives for the DURATION OF THIS EDIT and no longer. Every analysis pass this edit
        // queues copies it — there can be two, because RelinkAfterSpecChange refreshes again — and
        // whichever of them lands acts on it. A pass queued by any LATER edit copies nothing, so an
        // edit whose own pass was superseded before it landed is dropped rather than carried forward
        // to fire under an unrelated change. (Round 6's fixture is exactly that: two terminations set,
        // then three transforms added, with no wait in between.)
        // ── ONE UNDO ENTRY FOR THE WHOLE GESTURE (owner, 2026-08-28) ──
        //
        // A termination edit can write the design TWICE: once for the edit itself, and once more when
        // the auto-solve moves it onto a rack that reaches the new target. Which of the two lands
        // first — and whether the second happens at all — depends on whether the background solution
        // search has finished, so the naive shape put a nondeterministic NUMBER of entries on the
        // schematic's undo stack for one gesture. Both halves are covered, because the search can
        // land on either side of this method returning:
        //
        //  * SYNCHRONOUSLY, from inside CommitSpecChange's own Refresh — a cached or fast search
        //    lands its batch there and the auto-solve runs before this method's commit. Deferring
        //    every commit for the duration and making exactly one at the end collapses that, and it
        //    also absorbs RelinkAfterSpecChange's own commit on the way.
        //  * LATER, on the dispatcher, after this returns. There is nothing to defer by then, so the
        //    entry made here is remembered by its stamp and the auto-solve's commit AMENDS it. See
        //    CommitCore.
        //
        // UNDER THE EDIT GATE, which is what makes the two cases a CHOICE rather than a race — see
        // AsOneEdit. The auto-solve either runs inside this block, where the suppression catches it,
        // or after it, by which time the entry it should amend exists and has been recorded.
        AsOneEdit(() =>
        {
            _autoSolveEnd = end;
            _commitSuppressed++;
            try { CommitSpecChange(); }
            finally { _autoSolveEnd = 0; _commitSuppressed--; }

            if (_commitDeferred)
            {
                _commitDeferred = false;
                Commit();
            }
        });
    }

    /// <summary>
    /// Runs one user edit <b>with the analysis landings held off</b> — the read-modify-write of the
    /// design, its rebuild and its commit, as one indivisible step.
    /// </summary>
    /// <remarks>
    /// <b>Every landing already takes <see cref="RefreshGate"/></b> before it can touch anything, and
    /// <see cref="Refresh"/> takes it too — but an edit is a READ, a MUTATION and then a refresh, and
    /// only the last of the three was inside it. That was survivable while the only landing that
    /// WRITES the design, the termination auto-solve, could fire in a narrow window right after the
    /// edit that asked for it. It stopped being survivable when that request was allowed to wait for
    /// later cells of the search (2026-08-28, so that a design whose own family refuses still ends
    /// matched): the auto-solve can now land seconds later, in the middle of an unrelated edit.
    ///
    /// <para><b>Measured, not theorised.</b> Switching a transform from π to T immediately after a
    /// termination edit came back as π — the auto-solve had replaced <c>_design.Transforms</c>
    /// wholesale between this edit reading the record and its refresh rebuilding from it. In the
    /// application both run on the UI thread and cannot interleave; in a host whose result scheduler
    /// falls back to the thread pool they do, which is where it was caught.</para>
    ///
    /// <para>The lock is reentrant, so an edit that reaches another edit — the linkage re-driving a
    /// transform, the auto-solve applying a solution — costs nothing extra.</para>
    /// </remarks>
    private void AsOneEdit(Action edit)
    {
        lock (RefreshGate) edit();
    }

    /// <summary>Non-zero while a gesture is collecting its writes into ONE commit.</summary>
    /// <remarks>A depth counter rather than a flag, because the calls nest: a termination edit can
    /// reach <c>SetTransformN</c> through <c>RelinkAfterSpecChange</c>, and each of those commits.</remarks>
    private int _commitSuppressed;

    /// <summary>True when a suppressed commit was asked for and still owes the stack an entry.</summary>
    private bool _commitDeferred;

    /// <summary>
    /// Re-solves the transform rack against the ratio the SPECIFICATION now requires, when Link is on.
    /// Returns true when it did, in which case it has already refreshed and committed.
    /// </summary>
    /// <remarks>
    /// <b>Owner-reported, 2026-08-20:</b> <i>"I changed the TermG and it updated in the Termination 1
    /// specification, but did not update in the schematic (the old value was retained)."</i>
    ///
    /// <para>Re-declaring a termination changes <c>MatchSynthesisResult.RequiredTransformRatio</c>,
    /// but the stored N's are what they were, so <c>Π N²</c> is now off target and the FAR end's
    /// reference — which is the analysis end's scaled by that product (§4.8) — does not move. Measured
    /// on the shipped default: setting termination 2 to 25 Ω left the ladder presenting 15 Ω, so the
    /// glyph went on reading the old number while the specification pane read the new one.</para>
    ///
    /// <para>With Link on, that is precisely the case the linkage exists to absorb, and this is the
    /// same move <see cref="LinkTransforms"/>'s own setter already makes the moment it is switched on:
    /// re-drive one unlocked transform at its CURRENT N and let <c>MatchLinkage.Redistribute</c> put
    /// the others where the new product needs them. With Link off — or with every transform locked, or
    /// the target outside their ranges — nothing is written, and the disagreement is then stated on
    /// the glyph itself by <c>OhmsTextFor</c> rather than left to be noticed.</para>
    ///
    /// <para><b>Only user edits call this</b>, never <see cref="SetTarget"/> or
    /// <see cref="OnModelChanged"/>: a design must not rewrite itself just by being opened, and a
    /// commit on load would put an edit on the schematic's undo stack that nobody made.</para>
    /// </remarks>
    private bool RelinkAfterSpecChange()
    {
        if (!_design.LinkTransforms) return false;

        // Nothing to absorb, and saying so cheaply is what lets EVERY specification edit call this
        // rather than the one that was known to need it. A spec change that does not move
        // RequiredTransformRatio — AllowNegativeComponents, and any edit whose new target happens to
        // equal the old — leaves Π N² already on target, and re-driving a transform there would put a
        // no-op on the schematic's undo stack for a value nobody changed.
        if (Status.OnTarget) return false;

        // A refused design has no ladder to link. The transforms are still stored and still shown; the
        // rack simply cannot be solved against a basis that does not exist.
        if (_rebuild?.Basis is not { Ok: true }) return false;

        int index = -1;
        for (int i = 0; i < _design.Transforms.Count; i++)
        {
            if (_design.Transforms[i].Locked) continue;
            if (i < Transforms.Count && Transforms[i].IsDropped) continue;
            index = i;
            break;
        }
        if (index < 0) return false;

        SetTransformN(index, _design.Transforms[index].N);   // refreshes and commits
        return true;
    }

    /// <summary>
    /// <b>The one way a specification edit lands</b>: rebuild, re-solve the transform rack against the
    /// ratio the specification now requires, and commit.
    /// </summary>
    /// <remarks>
    /// <b>Owner-reported, 2026-08-20:</b> <i>"when I change the Filter Response, sometimes the
    /// Termination indicates unsatisfied, but if I tweak a slider transformer, the termination becomes
    /// satisfied — even when I slide it past the transform value it was before."</i>
    ///
    /// <para><see cref="RelinkAfterSpecChange"/> had exactly ONE caller — <see cref="SetTermination"/>
    /// — because that is the edit the owner reported first. Every other specification edit wrote
    /// <c>Refresh(specChanged: true); Commit();</c> and left the rack where it was, so a design that
    /// was matched a moment ago read "not reached" until the user happened to touch a slider, at which
    /// point <see cref="SetTransformN"/>'s own linkage silently did the work the spec edit should have
    /// done. Measured on 50 Ω into 5 Ω ∥ 1 pF at order 4 with one applied transform: Fano requires
    /// Π N² = 10.134 and two-ended requires 13.554, so switching the family alone left the far
    /// termination presenting 37.4 Ω against a declared 50 Ω, and a no-op slider nudge — setting N to
    /// the value it ALREADY had — restored it. The ORDER and the BAND edges do the same thing (order 6
    /// wants 10.054, moving f2 to 2.6 GHz wants 10.875), which is why the fix is a shared entry point
    /// and not a third copy of the two lines.</para>
    ///
    /// <para><b>Still only user edits.</b> <see cref="SetTarget"/>, <see cref="SetStandalone"/>,
    /// <see cref="OnModelChanged"/> and <see cref="Revert"/> call <see cref="Refresh"/> directly and
    /// deliberately do not come through here: a design must not rewrite itself just by being opened or
    /// restored, and a commit on load would put an edit on the schematic's undo stack that nobody
    /// made.</para>
    /// </remarks>
    private void CommitSpecChange() => AsOneEdit(() =>
    {
        Refresh(specChanged: true);
        if (RelinkAfterSpecChange()) return;   // it has already refreshed and committed
        Commit();
    });

    /// <summary>
    /// match.md §9.2: the Order picker offers only the parities §4.2 permits, and a topology change
    /// that invalidates the current order ADJUSTS it — <b>and says so in one line</b>, because a
    /// control that silently changes another control is worse than one that explains itself.
    /// </summary>
    private void AdjustOrderForParity()
    {
        var valid = MatchOrders.ValidOrders(_design.Term1, _design.Term2, _design.Form);
        if (valid.Contains(_design.Order)) { OrderNote = ""; return; }

        // A like-topology pair has NO valid order in lowpass or highpass form (match.md §16.4 item
        // 2), so there is nothing to move the order to. The order is left where it is — MN-1's own
        // refusal names the reason, and the Solutions panel still lists the bandpass rows, which is
        // where the user is going to go.
        if (valid.Count == 0)
        {
            OrderNote =
                $"Both terminations are {(_design.Term1.Topology == TerminationTopology.Parallel ? "parallel" : "series")}, "
                + $"and a {MatchSolutionRowViewModel.FormName(_design.Form)} network needs an odd "
                + "element count to absorb two ends of the same topology. Bandpass form absorbs both.";
            return;
        }

        int old = _design.Order;
        int chosen = valid.OrderBy(o => Math.Abs(o - old)).ThenBy(o => o).First();
        _design.Order = chosen;
        bool like = _design.Term1.Topology == _design.Term2.Topology;
        OrderNote =
            $"Order {old} cannot absorb both ends now: with a {(like ? "like" : "mixed")} termination "
            + $"pair the arms alternate, so only {string.Join(", ", valid)} fit. The order moved to "
            + $"{chosen}.";
    }

    /// <summary>The one line explaining an automatic order change, or empty.</summary>
    [ObservableProperty] private string _orderNote = "";

    /// <summary>
    /// Clears a stored <c>QAdjust</c> that the terminations have overtaken — <b>and says so in one
    /// line</b>, exactly as <see cref="AdjustOrderForParity"/> does for the order.
    /// </summary>
    /// <remarks>
    /// <b>Owner-reported, 2026-08-20:</b> <i>"I press the probe button on Terminal 2 and the parasitic
    /// updates to 1000 pH, but the schematic keeps rendering as 2000 pH."</i>
    ///
    /// <para>A Q-adjust INFLATES the analysis end's Q (match.md §4.6) and cannot reduce one, but it is
    /// stored as an absolute number — so an edit that RAISES that end's own Q leaves the stored value
    /// underneath it, which is a design <c>MatchSynthesis</c> now refuses outright
    /// (<c>AnalysisEndNotAbsorbable</c>; before that refusal existed it silently drew an absorbed
    /// element the termination does not supply). The probe is the sharpest way in: it writes a
    /// measured reactance where there may have been none, and a Q of 4 arriving under a Q-adjust of 2
    /// is a refusal caused by a button whose whole job is to make the specification correct.</para>
    ///
    /// <para>So it is cleared, not clamped. Zero means "no adjustment", which is always legal and is
    /// what the design would have carried had the user probed first; clamping to the new Q would
    /// invent a number nobody chose. <b>Only the two paths that can invalidate it without being ABOUT
    /// it call this</b> — a termination change and an analysis-end change. A user who types a low
    /// Q-adjust directly still gets the refusal, which names the number to use, because silently
    /// undoing what someone just typed is worse than refusing it.</para>
    /// </remarks>
    private void AdjustQAdjustForAnalysisEnd()
    {
        if (!(_design.QAdjust > 0)) { QAdjustNote = ""; return; }

        bool anaIsTerm1 = MatchSynthesis.AnalysisIsTerm1(_design);
        double qActual = (anaIsTerm1 ? _design.Term1 : _design.Term2).QAt(_design.Omega0);
        if (!(qActual > 0) || _design.QAdjust >= qActual * (1.0 - 1e-9)) { QAdjustNote = ""; return; }

        double old = _design.QAdjust;
        _design.QAdjust = 0.0;
        QAdjustNote =
            $"Termination {(anaIsTerm1 ? 1 : 2)}'s own Q is now {qActual:0.###}, above the Q-adjust of "
            + $"{old:0.###} — and a Q-adjust inflates an end's Q, it cannot reduce one. It has been "
            + "cleared.";
    }

    /// <summary>The one line explaining an automatic Q-adjust change, or empty.</summary>
    [ObservableProperty] private string _qAdjustNote = "";

    /// <summary>The orders <c>MatchOrders.ValidOrders</c> permits for the current pair AND form.</summary>
    public IReadOnlyList<int> OrderOptions =>
        MatchOrders.ValidOrders(_design.Term1, _design.Term2, _design.Form);

    /// <summary>
    /// Every order that any form permits for the current pair — what the solutions FILTER offers.
    /// </summary>
    /// <remarks>
    /// <b>The union, not the current form's list.</b> A like-topology pair has orders 3 and 5 in
    /// bandpass form and none in the other two; a filter built from a lowpass design's own options
    /// would then have no order lines at all while the panel was listing bandpass rows at orders 3
    /// and 5, and <c>Accepts</c> shows a row whose order has no line — so every one of them would be
    /// unhideable. The filter is a view over what was FOUND, and what was found spans the forms.
    /// </remarks>
    public IReadOnlyList<int> FilterOrderOptions =>
        [.. new[] { NetworkForm.Bandpass, NetworkForm.Lowpass, NetworkForm.Highpass }
             .SelectMany(f => MatchOrders.ValidOrders(_design.Term1, _design.Term2, f))
             .Distinct()
             .Order()];

    /// <summary>Network order.</summary>
    public int Order
    {
        get => _design.Order;
        set
        {
            if (value == _design.Order) return;
            _design.Order = value;
            OrderNote = "";
            CommitSpecChange();
        }
    }

    /// <summary>The four response families, each with its enablement and its reason.</summary>
    public IReadOnlyList<MatchResponseOptionViewModel> ResponseOptions { get; }

    /// <summary>The prototype family.</summary>
    public ResponseShape Response
    {
        get => _design.Response;
        set
        {
            if (value == _design.Response) return;
            _design.Response = value;
            CommitSpecChange();
        }
    }

    /// <summary>
    /// The Response ComboBox's selection (owner, 2026-08-19 — it was four radio buttons).
    /// </summary>
    /// <remarks>
    /// <b>Picking an infeasible family is REFUSED, and the refusal is what the pane then shows.</b>
    /// A ComboBox's drop-down will hand back a disabled item on some platforms where a disabled radio
    /// button simply cannot be clicked, so the guard has to live here rather than in the view — and
    /// silently reverting with nothing said would be the worst of the three options, which is why
    /// <see cref="ResponseRefusal"/> exists.
    ///
    /// <para><b>What this guard reads is the last COMPLETED feasibility pass</b>, which since the
    /// analysis moved to a worker can be a fraction of a second behind a specification the user has
    /// just changed. A family picked inside that window is accepted here and then refused by the
    /// rebuild instead — the status strip carries the refusal with its numbers, the affected
    /// termination flags, and the option disables itself when the pass lands. Two refusals for one
    /// mistake would be worse than one arriving from a different place, and blanking the enablement
    /// while a pass runs would disable every family for the duration of a search whose whole purpose
    /// is to say which ones are fine.</para>
    /// </remarks>
    public MatchResponseOptionViewModel? SelectedResponseOption
    {
        get => ResponseOptions.FirstOrDefault(o => o.Shape == _design.Response);
        set
        {
            if (value is null || value.Shape == _design.Response) return;
            if (!value.IsEnabled)
            {
                ResponseRefusal = value.Refusal?.Message
                    ?? $"{value.Display} cannot absorb both ends at order {_design.Order}.";
                OnPropertyChanged();
                return;
            }
            ResponseRefusal = "";
            Response = value.Shape;
            OnPropertyChanged();
        }
    }

    /// <summary>Why the last attempted response change was refused, or empty.</summary>
    [ObservableProperty] private string _responseRefusal = "";

    /// <summary>Equal-ripple level, dB — the real-to-real prototype only.</summary>
    public double RippleDb
    {
        get => _design.RippleDb;
        set
        {
            if (value == _design.RippleDb || !(value > 0)) return;
            _design.RippleDb = value;
            CommitSpecChange();
        }
    }

    /// <summary>
    /// True only when neither end has a reactance to prescribe (§6.6) — the equal-ripple prototype is
    /// what runs then, and the ripple is the only thing left to set.
    /// </summary>
    public bool RippleEnabled => !_design.Term1.HasReactance && !_design.Term2.HasReactance;

    /// <summary>
    /// Why the ripple field is not settable right now, or empty when it is.
    /// </summary>
    /// <remarks>
    /// <b>Owner, 2026-08-20:</b> <i>"Is Ripple, dB supposed to be an input? If so, the inline text
    /// editor does not show when I double click on its value."</i> It is an input, and it was
    /// disabled — but an <c>InlineEditText</c> at rest is a bare <c>TextBlock</c>, and Avalonia does
    /// not dim one for being disabled, so the row looked live and swallowed the gesture. The row
    /// dims (an <c>InlineEditText:disabled</c> style in the window, since nothing in the stack did)
    /// and this line says which end is the reason.
    /// </remarks>
    /// <remarks>
    /// <b>ONE SHORT LINE, with the paragraph moved to <see cref="RippleTooltip"/></b> (owner,
    /// 2026-08-20: <i>"reduce the text below the Filter Response, or else delete it and add it back
    /// as a tooltip addition in the combobox — there is not enough height space for the current
    /// amount of text"</i>). This was three sentences, and in a 300-pixel specification column three
    /// sentences is five wrapped lines that are present WHENEVER either termination carries a
    /// reactance, which is most designs. Deleting it outright would put back the round-6 bug it was
    /// written for — a disabled <c>InlineEditText</c> rests as a bare <c>TextBlock</c> and Avalonia
    /// does not dim one, so the row read as live and swallowed the double-click — so what stays is the
    /// half that answers "why can I not type here", naming the end, and the rest is one hover away on
    /// the row it is about.
    /// </remarks>
    /// <remarks>
    /// <b>Nothing at all when BOTH ends carry a reactance</b> (owner-reported, 2026-08-28: the line
    /// pushed the specification column past its own height and put a scroll bar on it). That is the
    /// DEFAULT design and most real ones, so the line the column could least afford was the one it
    /// showed almost always — and it is the one carrying the least, since with both ends reactive
    /// there is no end to name. The single-end spelling stays: it says WHICH end, which is the half
    /// that cannot be guessed from looking at the row.
    ///
    /// <para>Nothing is lost. The row still dims — which is what the round-6 bug this note was
    /// written for actually needed — and <see cref="RippleTooltip"/> still carries the whole of §6.6
    /// on the row it is about.</para>
    /// </remarks>
    public string RippleNote =>
        RippleEnabled || BothEndsReactive ? ""
        : $"{WhichEnd(lower: false)}'s reactance sets this.";

    /// <summary>The whole of §6.6's explanation, on the Ripple row itself.</summary>
    /// <remarks>
    /// <b>It opens with the sentence the note used to show</b> (owner, 2026-08-28), so the line that
    /// came off the specification column for want of height is still the first thing said on the row
    /// it was about — and it is said in the both-ends case, which is the one that no longer has a
    /// visible note at all. See <see cref="RippleNote"/>.
    /// </remarks>
    public string RippleTooltip => RippleEnabled
        ? "Equal-ripple level in dB — the real-to-real prototype only. Double-click to edit."
        : (BothEndsReactive ? "The terminations' reactances set this. " : "")
          + $"{WhichEnd(lower: false)} carr{(BothEndsReactive ? "y" : "ies")} a reactance, so the "
          + "prototype is the singly- or doubly-prescribed one and the ripple is set by the "
          + "terminations rather than by hand (match.md §6.6). Clear the reactance to – to set it here.";

    private bool BothEndsReactive => _design.Term1.HasReactance && _design.Term2.HasReactance;

    private string WhichEnd(bool lower)
    {
        bool one = _design.Term1.HasReactance;
        string s = BothEndsReactive ? "Both terminations" : one ? "Termination 1" : "Termination 2";
        return lower ? char.ToLowerInvariant(s[0]) + s[1..] : s;
    }

    /// <summary>
    /// What the CLOSED Response combo says on hover — the selected family's own line, or the refusal
    /// that disabled it.
    /// </summary>
    /// <remarks>
    /// The owner's own second option ("add it back as a tooltip addition in the combobox"). Each ITEM
    /// already carries its description in the drop-down; the closed control carried nothing at all, so
    /// the one place a user looks after picking a family had no explanation on it.
    /// </remarks>
    public string ResponseTooltip =>
        SelectedResponseOption?.Tooltip is { Length: > 0 } t
            ? t
            : "Which prototype family the synthesis draws from. An infeasible family stays in the list, "
              + "disabled, carrying the reason it cannot absorb both ends.";


    /// <summary>Deliberately inflated analysis-end Q (match.md §4.6), or 0 for none.</summary>
    public double QAdjust
    {
        get => _design.QAdjust;
        set
        {
            if (value == _design.QAdjust) return;
            _design.QAdjust = value;
            CommitSpecChange();
        }
    }

    /// <summary>The Q-adjust checkbox. Turning it on seeds the smallest Q that completes, when one does.</summary>
    public bool QAdjustEnabled
    {
        get => _design.QAdjust > 0;
        set
        {
            if (value == QAdjustEnabled) return;
            _design.QAdjust = value
                ? MatchSolutionSearch.FindQAdjust(_design, Settings.QMin) ?? Settings.QMin
                : 0.0;
            CommitSpecChange();
        }
    }

    /// <summary>Widens every transform range past its positivity threshold.</summary>
    public bool AllowNegativeComponents
    {
        get => _design.AllowNegativeComponents;
        set
        {
            if (value == _design.AllowNegativeComponents) return;
            _design.AllowNegativeComponents = value;
            CommitSpecChange();
        }
    }

    /// <summary>Which end pins g1 (match.md §4.2). Defaults to the higher-Q end.</summary>
    public AnalysisEndChoice AnalysisEnd
    {
        get => _design.AnalysisEnd;
        set
        {
            if (value == _design.AnalysisEnd) return;
            _design.AnalysisEnd = value;
            // Swapping which end pins g1 can put a legal Q-adjust under a different, higher Q.
            AdjustQAdjustForAnalysisEnd();
            CommitSpecChange();
        }
    }

    /// <summary>Lower band edge, Hz.</summary>
    public double F1
    {
        get => _design.F1;
        set
        {
            if (value == _design.F1 || !(value > 0)) return;
            _design.F1 = value;
            CommitSpecChange();
        }
    }

    /// <summary>Upper band edge, Hz.</summary>
    public double F2
    {
        get => _design.F2;
        set
        {
            if (value == _design.F2 || !(value > 0)) return;
            _design.F2 = value;
            CommitSpecChange();
        }
    }

    /// <summary>The band's display unit.</summary>
    public string BandUnit
    {
        get => Settings.FrequencyUnit;
        set => Settings.FrequencyUnit = value;
    }

    /// <summary>F1 as typed.</summary>
    public string F1Text
    {
        get => _f1Staged ?? MatchValueFormat.Format(
            F1, MatchQuantity.Frequency, BandUnit, MatchDesignerSettings.EntryDigits).Text;
        set { _f1Staged = value; OnPropertyChanged(); }
    }
    private string? _f1Staged;

    /// <summary>F2 as typed.</summary>
    public string F2Text
    {
        get => _f2Staged ?? MatchValueFormat.Format(
            F2, MatchQuantity.Frequency, BandUnit, MatchDesignerSettings.EntryDigits).Text;
        set { _f2Staged = value; OnPropertyChanged(); }
    }
    private string? _f2Staged;

    /// <summary>Parses and commits the staged band edges.</summary>
    public void CommitBand()
    {
        string? f1 = _f1Staged, f2 = _f2Staged;
        _f1Staged = _f2Staged = null;
        if (f1 is not null && MatchValueFormat.TryParse(f1, BandUnit, out double v1)) F1 = v1;
        if (f2 is not null && MatchValueFormat.TryParse(f2, BandUnit, out double v2)) F2 = v2;
        OnPropertyChanged(nameof(F1Text));
        OnPropertyChanged(nameof(F2Text));
    }

    // ── Inline-editor entry text (owner, 2026-08-19) ──────────────────────────
    //
    // Same shape as MatchTerminationViewModel's own pair: one string carrying value AND unit, which
    // is what an InlineEditText shows and seeds from. The parse and the write both still go through
    // the properties above — these only compose and decompose the unit.

    /// <summary>The lower band edge as "1 GHz".</summary>
    public string F1Entry
    {
        get => $"{F1Text} {BandUnit}";
        set => SetBandEntry(value, isF1: true);
    }

    /// <summary>The upper band edge as "2 GHz".</summary>
    public string F2Entry
    {
        get => $"{F2Text} {BandUnit}";
        set => SetBandEntry(value, isF1: false);
    }

    private void SetBandEntry(string? text, bool isF1)
    {
        if (!MatchValueFormat.TryParseWithUnit(
                text, MatchQuantity.Frequency, BandUnit, out double f, out string unit))
        {
            OnPropertyChanged(isF1 ? nameof(F1Entry) : nameof(F2Entry));
            return;
        }
        // Both edges share ONE display unit, so a unit typed into either moves both.
        if (unit != BandUnit) BandUnit = unit;
        if (isF1) F1 = f; else F2 = f;
        OnPropertyChanged(nameof(F1Text));
        OnPropertyChanged(nameof(F2Text));
        OnPropertyChanged(nameof(F1Entry));
        OnPropertyChanged(nameof(F2Entry));
    }

    /// <summary>The equal-ripple level as "0.1 dB".</summary>
    public string RippleEntry
    {
        get => RippleDb.ToString("0.###", CultureInfo.InvariantCulture) + " dB";
        set
        {
            string t = (value ?? "").Trim();
            if (t.EndsWith("dB", StringComparison.OrdinalIgnoreCase)) t = t[..^2].Trim();
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0)
                RippleDb = v;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// The orders this termination pair permits, as the selector's items.
    /// </summary>
    /// <remarks>
    /// <b>Owner, 2026-08-20:</b> <i>"the Order needs to be that custom icon selector UI element whose
    /// options change depending on the two terminations."</i>
    ///
    /// <para><see cref="OrderOptions"/> is not a preference list — with a like or mixed termination
    /// pair the arms alternate and only one parity fits at all (match.md §4.2). A selector that offers
    /// exactly those is therefore the honest control: the refusal the old typed field had to SAY
    /// ("order must be one of 3, 5") the selector makes by not offering it, and there is nothing left
    /// to refuse.</para>
    ///
    /// <para><b>A stable collection whose CONTENTS change</b>, not a fresh list per read: this
    /// repository has already been bitten by a selector losing its selection when its item list was
    /// replaced underneath it (see src/Ui/CLAUDE.md on ComboBox notification order). It is also only
    /// rewritten when the permitted set actually differs, so an ordinary edit does not churn it.</para>
    /// </remarks>
    public ObservableCollection<string> OrderChoices { get; } = [];

    /// <summary>The order as one of <see cref="OrderChoices"/>.</summary>
    public string OrderChoice
    {
        get => Order.ToString(CultureInfo.InvariantCulture);
        set
        {
            if (int.TryParse((value ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                             out int n)
                && OrderOptions.Contains(n))
                Order = n;
            else
                OnPropertyChanged();
        }
    }

    private void RefreshOrderChoices()
    {
        var wanted = OrderOptions.Select(o => o.ToString(CultureInfo.InvariantCulture)).ToList();
        if (OrderChoices.SequenceEqual(wanted, StringComparer.Ordinal)) return;

        OrderChoices.Clear();
        foreach (var w in wanted) OrderChoices.Add(w);
        // The item list just changed underneath the selector, so the selection has to be re-pushed.
        OnPropertyChanged(nameof(OrderChoice));
    }

    /// <summary>What the order field's tooltip states — the orders this pair actually permits.</summary>
    public string OrderTooltip =>
        $"Only the parities the two terminations permit: {string.Join(", ", OrderOptions)}. "
        + "An order that cannot absorb both ends is refused.";

    // ── Revert ────────────────────────────────────────────────────────────────
    //
    // THERE IS NO APPLY, and there is nothing for one to do (owner-reported, 2026-08-20: "the Apply
    // button is always disabled, even after I make changes. What does apply do?").
    //
    // It never sent the design anywhere: every edit in this window already writes the component's
    // Design parameter as it is made, as one SetParametersCommand on the schematic's own undo stack.
    // What Apply flushed was a number half-typed in a box that had not lost focus yet — and once the
    // last of those boxes became an InlineEditText (2026-08-19/20), that state stopped existing:
    // the control's three-key contract commits on Return AND on LostFocus, and every *Entry setter
    // parses and writes in the same breath. So `HasPendingEdits` was structurally false, the button
    // could never light, and it has been removed along with the plumbing that fed it rather than
    // left on the footer as a control the user is entitled to think does something.

    /// <summary>
    /// Restores the design this window opened with, as ONE undoable command. Not a discard: the
    /// intervening edits were real commits, so undoing them has to be a commit too.
    /// </summary>
    public void Revert() => AsOneEdit(() =>
    {
        _design = _openingDesign.Clone();
        Refresh(specChanged: true);
        Commit();
    });

    // ── Rebuild ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Serializes a refresh against the analysis landing that can now start one.
    /// </summary>
    /// <remarks>
    /// <b>In the application this is uncontended and does nothing</b>: the analysis lands on
    /// <c>TaskScheduler.FromCurrentSynchronizationContext</c>, which is the UI thread, and every edit
    /// is on the UI thread too, so the two were already serialized by the message loop.
    ///
    /// <para>It exists for the host that has NO dispatcher — a test, or a headless export — where
    /// that scheduler falls back to the pool and the landing genuinely runs beside the caller. That
    /// was harmless while the landing only filled two collections. It stopped being harmless when
    /// <c>AutoApplyAReachingSolution</c> gave it a <see cref="Refresh"/> of its own: two overlapping
    /// refreshes both rebuild the plots, and <c>Plot.Autoscale</c> enumerates <c>Traces</c> on every
    /// add, so one thread's add lands inside the other's enumeration and throws "collection was
    /// modified". Observed, not theorised.</para>
    ///
    /// <para>A <c>Monitor</c> lock, and deliberately: the landing takes it and then calls
    /// <see cref="Refresh"/>, which takes it again on the same thread. Nothing inside it waits on a
    /// task, so there is no side to deadlock against.</para>
    /// </remarks>
    internal readonly object RefreshGate = new();

    /// <summary>Bumped by every rebuild. See <see cref="RefreshCore"/>.</summary>
    private int _designEpoch;

    /// <summary>Re-derives everything from the design. <paramref name="specChanged"/> also re-runs the
    /// two expensive searches.</summary>
    public void Refresh(bool specChanged)
    {
        lock (RefreshGate) RefreshCore(specChanged);
    }

    private void RefreshCore(bool specChanged)
    {
        // Every rebuild is a new epoch. An analysis pass records the one it was queued for, and the
        // auto-solve is honoured only when the design has not moved since — see
        // AutoApplyAReachingSolution. A pass is NOT superseded by an edit that refreshes without
        // queuing one (adding a transform, toggling Link), so "the newest pass" is not on its own
        // evidence that the design is still the one that pass was about.
        _designEpoch++;

        // ── The inline-edit note is about the LAST inline edit, and only that ──
        //
        // Owner-reported, 2026-08-20: "sometimes the error messages in the grid area (below
        // schematic) don't go away. For example, I will change a termination value and the error is
        // still there. Is it being updated?"
        //
        // It was not. InlineEditNote was cleared only on the way INTO the next inline edit
        // (CommitInlineEdit's first statement), so "L1 cannot reach 725 pH with the transforms this
        // ladder has" outlived the ladder it was about: change the termination, the order, the band or
        // a transform's N and the sentence stayed on screen naming a value the network no longer has.
        // Clearing it here — at the one place every change to the design funnels through — makes the
        // note last exactly as long as the state it describes.
        //
        // The failure paths that SET it do not come through here (a parse refusal, a locked rack, an
        // unreachable value: none of them changes the design and none of them rebuilds), and the one
        // path that does — SetElementValue's successful solve — sets the note AFTER its Refresh.
        InlineEditNote = "";

        _rebuild = MatchRebuild.Rebuild(_design);

        RefreshTransformRows();
        RefreshLadderAndGrid();
        RefreshStatus();
        RefreshFlatten();

        if (specChanged)
        {
            // The two expensive answers go to a worker and land when they land — see
            // MatchDesignerViewModel.Analysis.cs for why they are the only two that do.
            QueueAnalysis();
            OnPropertyChanged(nameof(OrderOptions));
            RefreshOrderChoices();
            OnPropertyChanged(nameof(RippleEnabled));
            OnPropertyChanged(nameof(RippleNote));
        }

        Term1.Refresh();
        Term2.Refresh();

        if (_isDragging) _plotsStaleFromDrag = true;
        else UpdatePlots();

        OnPropertyChanged(nameof(Design));
        OnPropertyChanged(nameof(Rebuild));
        OnPropertyChanged(nameof(Order));
        OnPropertyChanged(nameof(Response));
        OnPropertyChanged(nameof(SelectedResponseOption));
        OnPropertyChanged(nameof(ResponseTooltip));
        OnPropertyChanged(nameof(RippleTooltip));
        OnPropertyChanged(nameof(RippleDb));
        OnPropertyChanged(nameof(QAdjust));
        OnPropertyChanged(nameof(QAdjustEnabled));
        OnPropertyChanged(nameof(AllowNegativeComponents));
        OnPropertyChanged(nameof(AnalysisEnd));
        OnPropertyChanged(nameof(F1));
        OnPropertyChanged(nameof(F2));
        OnPropertyChanged(nameof(F1Text));
        OnPropertyChanged(nameof(F2Text));
        OnPropertyChanged(nameof(F1Entry));
        OnPropertyChanged(nameof(F2Entry));
        OnPropertyChanged(nameof(RippleEntry));
        OnPropertyChanged(nameof(OrderChoice));
        OnPropertyChanged(nameof(OrderTooltip));
        OnPropertyChanged(nameof(LinkTransforms));
    }

    // ── Commit ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the whole design back to the component's <c>Design</c> parameter, and refreshes the six
    /// ECHO parameters beside it, as ONE <c>SetParametersCommand</c> — one undo entry per edit,
    /// undoable from the schematic's own stack (brief §1).
    /// </summary>
    public void Commit() => CommitCore(0);

    /// <summary>
    /// <inheritdoc cref="Commit"/>
    /// </summary>
    /// <param name="amendStamp">
    /// The <see cref="UndoRedoStack.TopUndoStamp"/> of an earlier commit made <b>in this same user
    /// gesture</b>, which this one should absorb rather than stack on top of; 0 for an ordinary
    /// commit.
    /// </param>
    /// <remarks>
    /// <b>One gesture, one undo entry — owner, 2026-08-28.</b> A termination edit commits once
    /// immediately and then, seconds later, a SECOND time when the background solution search lands
    /// and the auto-solve moves the design onto a rack that reaches the new target (see
    /// <c>AutoApplyAReachingSolution</c>). Two commits is two <c>SetParametersCommand</c>s, so one
    /// Ctrl+Z put the transforms back and left the termination where the user had typed it —
    /// halfway through an edit they made once.
    ///
    /// <para><b>Worse, it was nondeterministic:</b> whether the second commit happened at all
    /// depended on whether the search had finished, so the same gesture was one undo entry on a slow
    /// machine and two on a fast one. That is how it was found —
    /// <c>MatchRound5Tests.ATermGEdit_WritesTheSpecificationsOwnR_AndRefusesAComplexValue</c> failed
    /// in ISOLATION and passed under full-suite load.</para>
    ///
    /// <para><b>The amend is an undo-then-replace</b>, which needs no new stack API and is exactly
    /// right: undoing the first command puts the component's parameters back to what they were
    /// before the gesture, so the <c>SetParametersCommand</c> built below captures THAT as its
    /// "before" and spans the whole gesture. <c>Execute</c> clears the redo stack, so the entry that
    /// was undone does not linger there. The undo is inside the <c>_isCommitting</c> guard, so the
    /// model change it raises does not send this Designer back to re-read a design it is in the
    /// middle of writing.</para>
    ///
    /// <para><b>Guarded on the stamp</b>, because this window is not modal and the schematic behind
    /// it is live: between the two commits the user may have edited the drawing, and amending would
    /// then undo THEIR edit. The stamp identifies the entry rather than merely counting it, so a top
    /// that is no longer ours is left alone and the auto-solve simply commits normally — two entries
    /// in the one case where two entries are the truth.</para>
    /// </remarks>
    private void CommitCore(long amendStamp)
    {
        // Inside a gesture that is collecting its writes: the design is already correct in memory and
        // the caller commits it once when the gesture ends. See SetTermination.
        if (_commitSuppressed > 0) { _commitDeferred = true; return; }

        if (_target is null || _schematicVm is null) return;
        // A deleted component is not written to. See IsOrphaned — this is the single choke point
        // every setter in this class already funnels through, which is why the guard is only here.
        if (IsOrphaned) return;

        // Keep the stored fingerprint in step with the basis we just synthesised, so a reload
        // compares this session's synthesis against itself and only reports a mismatch when the
        // synthesis has genuinely changed underneath the design (match.md §7.3).
        if (_rebuild?.Basis.Ok == true) _design.BasisFingerprint = _rebuild.Basis.BasisFingerprint;

        _isCommitting = true;
        try
        {
            // Take the earlier half of this same gesture off the stack FIRST, so the component's
            // parameters are back at the state the gesture started from and the command built below
            // spans all of it.
            if (amendStamp != 0 && _schematicVm.UndoRedo.TopUndoStamp == amendStamp)
                _schematicVm.UndoRedo.Undo();

            var updated = _target.Parameters.Select(p => p.Clone()).ToList();
            Set(updated, MatchEmbedding.DesignParameter, MatchEmbedding.Encode(_design), "", UnitDimension.None, false);
            Set(updated, "F1", Echo(_design.F1 / 1e9), "GHz", UnitDimension.Frequency, true);
            Set(updated, "F2", Echo(_design.F2 / 1e9), "GHz", UnitDimension.Frequency, true);
            Set(updated, "Order", _design.Order.ToString(CultureInfo.InvariantCulture), "", UnitDimension.None, true);
            Set(updated, "Response", _design.Response.ToString(), "", UnitDimension.None, false);
            Set(updated, "Form", _design.Form.ToString(), "", UnitDimension.None, false);
            Set(updated, "R1", Echo(_design.Term1.R), "Ω", UnitDimension.Resistance, false);
            Set(updated, "R2", Echo(_design.Term2.R), "Ω", UnitDimension.Resistance, false);

            _schematicVm.Execute(new SetParametersCommand(_schematicVm.EditModel, _target, updated));

            // While a termination edit is still waiting on its auto-solve, remember the entry just
            // pushed so that auto-solve can amend it instead of adding one beside it. Recorded HERE
            // rather than at the end of the gesture because the gesture does not know which of its
            // writes will end up being the one on the stack — the relink's, or its own — and
            // recording it at the point of the push is the only spelling that cannot be wrong.
            if (_pendingAutoSolve is not null)
                _autoSolveCommitStamp = _schematicVm.UndoRedo.TopUndoStamp;
        }
        finally { _isCommitting = false; }

        // The echoes were just rewritten from the design, so they agree by construction — and this is
        // the "the label will catch up on the next edit" half of CheckEchoParameters' own promise.
        // Cleared here rather than re-checked: the commit is what made them true.
        EchoNote = "";

        return;

        static string Echo(double v) => v.ToString("G6", CultureInfo.InvariantCulture);

        static void Set(List<EditableParameter> list, string name, string expression, string unit,
                        UnitDimension dim, bool showOnSchematic)
        {
            var existing = list.FirstOrDefault(p => p.Name == name);
            if (existing is not null)
            {
                existing.Expression = expression;
                if (existing.Unit.Length == 0) existing.Unit = unit;
                return;
            }
            list.Add(new EditableParameter
            {
                Name = name, Expression = expression, Unit = unit,
                Dimension = dim, ShowOnSchematic = showOnSchematic,
            });
        }
    }
}
