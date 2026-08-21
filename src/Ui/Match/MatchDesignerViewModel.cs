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
            new(ResponseShape.ChebyshevFano, "Chebyshev — Fano optimum",
                "The singly-prescribed closed form with the Fano root. The default, and the best "
                + "in-band match a network of this order can reach for this termination."),
            new(ResponseShape.ChebyshevTwoEnded, "Chebyshev — both ends prescribed",
                "Both end Q's are inputs, so the far end absorbs exactly and no surplus element is "
                + "ever needed. What it gives up is Fano optimality."),
            new(ResponseShape.Butterworth, "Butterworth",
                "Maximally-flat magnitude, through the numerical route. Roughly half the "
                + "group-delay variation of the Chebyshev design."),
            new(ResponseShape.Bessel, "Bessel",
                "Maximally-flat group delay. Feasible as a prototype and usually refused by the far "
                + "end, which is why the refusal names its numbers."),
        ];
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

    /// <summary>"Match — MN1", or "Match Designer" when there is no instance to name.</summary>
    public string Title => _isStandalone ? "Match Designer" : $"Match — {InstanceName}";

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
    }

    private void OnStackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UndoRedoStack.CanUndo)) UndoCommand.NotifyCanExecuteChanged();
        if (e.PropertyName is nameof(UndoRedoStack.CanRedo)) RedoCommand.NotifyCanExecuteChanged();
    }

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
        // Units and digits are display only — re-render, do not rebuild and do not commit. Qmin and
        // the Q-adjust toggle change what the SEARCH offers, so they re-run it.
        bool searchAffecting = e.PropertyName is nameof(MatchDesignerSettings.QMin)
                                              or nameof(MatchDesignerSettings.OfferQAdjustedSolutions);
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
        Refresh(specChanged: true);
        if (RelinkAfterSpecChange()) return;
        Commit();
    }

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
    /// match.md §9.2: the Order picker offers only the parities §4.2 permits, and a topology change
    /// that invalidates the current order ADJUSTS it — <b>and says so in one line</b>, because a
    /// control that silently changes another control is worse than one that explains itself.
    /// </summary>
    private void AdjustOrderForParity()
    {
        var valid = MatchOrders.ValidOrders(_design.Term1, _design.Term2);
        if (valid.Contains(_design.Order)) { OrderNote = ""; return; }

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

    /// <summary>The orders <c>MatchOrders.ValidOrders</c> permits for the current pair.</summary>
    public IReadOnlyList<int> OrderOptions => MatchOrders.ValidOrders(_design.Term1, _design.Term2);

    /// <summary>Network order.</summary>
    public int Order
    {
        get => _design.Order;
        set
        {
            if (value == _design.Order) return;
            _design.Order = value;
            OrderNote = "";
            Refresh(specChanged: true);
            Commit();
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
            Refresh(specChanged: true);
            Commit();
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
            Refresh(specChanged: true);
            Commit();
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
    public string RippleNote
    {
        get
        {
            if (RippleEnabled) return "";
            bool one = _design.Term1.HasReactance, two = _design.Term2.HasReactance;
            string which = one && two ? "Both terminations carry" : one ? "Termination 1 carries" : "Termination 2 carries";
            return $"{which} a reactance, so the prototype is the singly- or doubly-prescribed one "
                   + "and the ripple is set by the terminations rather than by hand (match.md §6.6). "
                   + "Clear the reactance to – to set it here.";
        }
    }


    /// <summary>Deliberately inflated analysis-end Q (match.md §4.6), or 0 for none.</summary>
    public double QAdjust
    {
        get => _design.QAdjust;
        set
        {
            if (value == _design.QAdjust) return;
            _design.QAdjust = value;
            Refresh(specChanged: true);
            Commit();
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
            Refresh(specChanged: true);
            Commit();
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
            Refresh(specChanged: true);
            Commit();
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
            Refresh(specChanged: true);
            Commit();
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
            Refresh(specChanged: true);
            Commit();
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
            Refresh(specChanged: true);
            Commit();
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
    public void Revert()
    {
        _design = _openingDesign.Clone();
        Refresh(specChanged: true);
        Commit();
    }

    // ── Rebuild ───────────────────────────────────────────────────────────────

    /// <summary>Re-derives everything from the design. <paramref name="specChanged"/> also re-runs the
    /// two expensive searches.</summary>
    public void Refresh(bool specChanged)
    {
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
    public void Commit()
    {
        if (_target is null || _schematicVm is null) return;
        // A deleted component is not written to. See IsOrphaned — this is the single choke point
        // every setter in this class already funnels through, which is why the guard is only here.
        if (IsOrphaned) return;

        // Keep the stored fingerprint in step with the basis we just synthesised, so a reload
        // compares this session's synthesis against itself and only reports a mismatch when the
        // synthesis has genuinely changed underneath the design (match.md §7.3).
        if (_rebuild?.Basis.Ok == true) _design.BasisFingerprint = _rebuild.Basis.BasisFingerprint;

        var updated = _target.Parameters.Select(p => p.Clone()).ToList();
        Set(updated, MatchEmbedding.DesignParameter, MatchEmbedding.Encode(_design), "", UnitDimension.None, false);
        Set(updated, "F1", Echo(_design.F1 / 1e9), "GHz", UnitDimension.Frequency, true);
        Set(updated, "F2", Echo(_design.F2 / 1e9), "GHz", UnitDimension.Frequency, true);
        Set(updated, "Order", _design.Order.ToString(CultureInfo.InvariantCulture), "", UnitDimension.None, true);
        Set(updated, "Response", _design.Response.ToString(), "", UnitDimension.None, false);
        Set(updated, "R1", Echo(_design.Term1.R), "Ω", UnitDimension.Resistance, false);
        Set(updated, "R2", Echo(_design.Term2.R), "Ω", UnitDimension.Resistance, false);

        _isCommitting = true;
        try { _schematicVm.Execute(new SetParametersCommand(_schematicVm.EditModel, _target, updated)); }
        finally { _isCommitting = false; }

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
