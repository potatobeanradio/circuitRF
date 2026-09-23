// Picking a net SHOWS you the net — the preview highlight and the measured reference return
// (docs/sonnet-briefs/brief-railrf-19-unreachable-states.md R-rail19-1c, R-rail19-1d, R-rail19-2).
//
// ── THERE IS NO SECOND CONNECTIVITY WALK HERE ──────────────────────────────────────────────────
//
// R-rail19-4's scope rule, and Regions' own header says it first: DrcConnectivity partitions
// the copper, Regions joins that to a net name, and the extraction the numbers come from
// calls the same function. Everything in this file goes through Regions.Walk and
// Regions.ReferenceNetOn. A preview drawn from a walk of its own would be a picture that
// could disagree with the solve about what the rail IS, which is the one thing a preview must never
// do.
//
// ── AND IT IS NOT PAID FOR TWICE ───────────────────────────────────────────────────────────────
//
// R-rail19-2c. The walk is not free on a large board and a pick list can hold two hundred nets, so
// arrowing down it must not walk two hundred times more than once each. Two caches, both cleared by
// the one method the board already calls when the artwork moves (NotifyArtworkChanged):
//
//   _layerRegions   the flattened copper, which every walk takes as its input
//   _netPreviews    net name → the preview, so re-selecting a row is free
//
// The flatten is shared with nothing else on purpose: PdnMeshExtractor builds its own inside the
// extraction, at solve time, from the same function. Sharing THAT would tie a repaint to a solve.
//
// ── AND NONE OF IT RUNS ON THE UI THREAD ───────────────────────────────────────────────────────
//
// Reported from outside on a real six-layer board, 2026-09-21: confirming the reference layer made
// the window stop responding. It was exact, and the path is short — ConfirmReference ->
// RefreshNetMarks -> the ReferenceReturnNet getter, which did the whole of the following inline:
//
//   LayerRegions.Build   a Clipper union of every shape on every copper layer
//   DrcConnectivity.Extract              (inside ReferenceNetOn) the galvanic partition of ALL of it
//
// On the shipped example that is a handful of milliseconds and nobody ever saw it; on a production
// board it is the same two operations the solve itself goes off-thread to do. A property getter was
// the last place anyone would look for them, which is most of why it survived — R-rail19-2c made the
// walk cheap to REPEAT and nothing ever asked what one of them cost.
//
// So both answers are DEFERRED: the getter and the preview publish null, start the work, and are
// re-asked when it lands. Two consequences worth stating rather than discovering:
//
//   * NULL IS TRANSIENT NOW. `ReferenceReturnNet` reads null while a measurement is in flight, and
//     RefreshNetMarks runs again on arrival — so a row's mark appears a moment after the
//     confirmation rather than with it. That is the same contract the solve already has and the
//     status strip says which is happening.
//   * A SUPERSEDED JOB IS DROPPED, NOT STOPPED. Neither Clipper function takes a cancellation
//     token, so cancelling abandons the RESULT and the work runs to completion in the background.
//     That is enough for the requirement, which is that the window stays alive — and it is why
//     starting a second job while one is running is cheap only because the flatten is shared.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Render;
using Clipper2Lib;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.RailRf;

public sealed partial class RailRfViewModel
{
    private Dictionary<LayerKey, Paths64>? _layerRegions;
    private readonly Dictionary<string, RailNetPreview> _netPreviews =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The layer <see cref="ReferenceReturnNet"/> was measured against, or null for
    /// "not measured yet". Keyed by layer because confirming a DIFFERENT reference asks a different
    /// question of the same board.</summary>
    private LayerKey? _referenceNetMeasuredOn;
    private string? _referenceReturnNet;

    /// <summary>The whole answer <see cref="_referenceReturnNet"/> is the net of — with where it came
    /// from, which the reference row prints (R-rail31-1).</summary>
    private PdnReturnNet? _referenceReturn;

    /// <summary>The NAMED return the measurement was made under. Keyed like the layer, because naming
    /// a net in the reference row — or undoing that — asks a different question of the same board.</summary>
    private string? _referenceMeasuredNamed;

    /// <summary>The return the document names, or null — what <c>Regions.ResolveReturnNet</c> takes
    /// before it measures anything. The live document ONLY, never the board's snapshot of it: the
    /// reference row edits it after the board was read, and choosing "measured" there clears it —
    /// falling back to the snapshot put the old name back under a row that says "measured".</summary>
    private string? NamedReturnNet =>
        _document.ReferenceNet is { Length: > 0 } named ? named : null;

    /// <summary>How many PREVIEW walks this board has paid for — <b>counted so R-rail19-2c is
    /// testable without timing anything</b>, which is the same reason <c>SolvesStarted</c> exists.
    /// The reference-return measurement is not one of these; it is counted by
    /// <see cref="ReferenceMeasurements"/>, because the two answer different questions and a single
    /// counter would make each one's test depend on the other having run.</summary>
    internal int NetWalksPerformed { get; private set; }

    /// <summary>How many times the reference return has been measured off this board.</summary>
    internal int ReferenceMeasurements { get; private set; }

    // ── The copper jobs ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// How a copper read leaves the UI thread. <c>Task.Run</c> in the application.
    /// </summary>
    /// <remarks>
    /// The same seam, and for the same reason, as <see cref="RunOffThread"/> next door — a test that
    /// wants the answer in hand replaces it with an inline call, and <see cref="CopperRead"/> is what
    /// one that wants the real threading awaits.
    /// </remarks>
    internal Func<Action, Task> ReadCopperOffThread { get; set; } =
        static work => Task.Run(work);

    /// <summary>The copper read in flight, or null. <b>Awaitable</b>, so a test can wait for the
    /// deferred answer rather than sleeping for it.</summary>
    internal Task? CopperRead { get; private set; }

    /// <summary>What a copper job is for — carried so the completion knows what to publish.</summary>
    private sealed record CopperJob(LayerKey? MeasureReference, string? PreviewNet);

    private CopperJob? _copperJob;
    private CancellationTokenSource? _copperCts;

    /// <summary>
    /// True while the board's copper is being read — <b>what the strip says</b>, because the two
    /// operations behind it are the same ones the solve announces and a window that went quiet for
    /// tens of seconds with nothing on it is the report this file exists for.
    /// </summary>
    [ObservableProperty]
    private bool _isReadingCopper;

    partial void OnIsReadingCopperChanged(bool value) => OnPropertyChanged(nameof(StatusLine));

    /// <summary>
    /// The net highlighted in the pick list, outlined on the board — or null for none.
    /// </summary>
    /// <remarks>
    /// <b>A PREVIEW, and the picture says so</b> (R-rail19-2b). It is drawn in its own colour and
    /// dashed, distinct from the committed rail's solid outline on the copper tab, because a preview
    /// that looks identical to a committed rail is a preview that makes a user think they already
    /// pressed the button.
    /// </remarks>
    [ObservableProperty]
    private RailNetPreview? _netPreview;

    partial void OnNetPreviewChanged(RailNetPreview? value) => BoardOverlayLayer.NetPreview = value;

    /// <summary>
    /// What the highlighted net's copper ALSO carries, or empty (field report, 2026-09-22).
    /// </summary>
    /// <remarks>
    /// Picking a supply net outlined the whole board and said nothing else, so the only thing a
    /// designer could conclude was that railRF was wrong. It was the board's own names that were: the
    /// copper that net reaches also held the pins of a dozen others. Saying WHICH is what turns a
    /// wrong-looking picture into something a user can go and fix.
    /// </remarks>
    [ObservableProperty]
    private string _netPreviewNote = "";

    /// <summary>True while <see cref="NetPreviewNote"/> has something to say.</summary>
    public bool HasNetPreviewNote => NetPreviewNote.Length > 0;

    partial void OnNetPreviewNoteChanged(string value) => OnPropertyChanged(nameof(HasNetPreviewNote));

    private readonly Dictionary<string, string> _netPreviewNotes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The sentence, for a net whose copper also holds <paramref name="others"/>' pins.</summary>
    internal static string NetPreviewShortNote(string net, IReadOnlyList<(string Net, int Pins)> others)
    {
        if (others.Count == 0) return "";

        string list = string.Join(", ", others.Take(4).Select(o => $"'{o.Net}' ({o.Pins} pin{(o.Pins == 1 ? "" : "s")})"))
                    + (others.Count > 4 ? $" and {others.Count - 4} more" : "");

        return $"The copper '{net}' reaches also holds pins the schematic puts on {list}, so on this " +
               "board those nets are one piece of metal. Either the artwork joins them, or a part " +
               "between them is placed the wrong way round and railRF could not tell which way from " +
               "the copper.";
    }

    /// <summary>
    /// Which net the confirmed reference layer's copper belongs to, measured from the artwork — or
    /// null before the reference is confirmed, <b>while the measurement is in flight</b>, and where
    /// the measurement does not resolve.
    /// </summary>
    /// <remarks>
    /// <b>Null before the confirmation is the requirement, not a default</b> (R-rail19-1d). railRF
    /// does not know which net the return is until somebody has said which layer it is on, and a
    /// pick list that marked a row as though it did would be making exactly the name-shaped guess
    /// R-rail19-1c refuses.
    ///
    /// <para><b>Reading it ASKS for the measurement</b> and does not perform one — see this file's
    /// header. The answer arrives on a later turn of the UI thread and <see cref="RefreshNetMarks"/>
    /// runs again then.</para>
    /// </remarks>
    public string? ReferenceReturnNet
    {
        get
        {
            if (!IsReferenceConfirmed || SelectedRail?.ReferenceLayer is not { } layer) return null;
            if (_referenceNetMeasuredOn == layer
                && string.Equals(_referenceMeasuredNamed, NamedReturnNet, StringComparison.OrdinalIgnoreCase))
                return _referenceReturnNet;

            BeginCopperJob(new CopperJob(layer, null));
            return null;
        }
    }

    /// <summary>
    /// Why picking the reference return is refused, and what to do instead — <b>one sentence, used
    /// by the refusal and by the row's tooltip</b>, so the list and the status strip cannot come to
    /// say different things.
    /// </summary>
    public static string ReferenceReturnRefusal(string net, string layer) =>
        $"'{net}' is the reference return — railRF measured it from the copper on layer {layer}, " +
        "which is where every rail on this board returns its current. It is what the rails are " +
        "measured AGAINST, so it cannot also be one of them. Pick the power net instead, or change " +
        "the reference layer if this board's return is somewhere else.";

    /// <summary>
    /// Publishes the preview for <paramref name="net"/>, or clears it for null.
    /// </summary>
    /// <remarks>
    /// <b>The walk is cached by NET NAME for the lifetime of the loaded board</b> and cleared by
    /// <see cref="InvalidateNetWalks"/> when the artwork changes, which is R-rail19-2c. Selecting the
    /// same row twice therefore costs nothing at all, and arrowing down a two-hundred-net list costs
    /// one walk per net rather than one per keystroke.
    ///
    /// <para>A net not in the cache is walked OFF the UI thread and the preview appears when it
    /// lands — the header's reason. Arrowing quickly down a list therefore starts and abandons jobs,
    /// which is correct: the last row is the one the user is on.</para>
    /// </remarks>
    private void ShowNetPreview(string? net)
    {
        if (net is not { Length: > 0 }) { NetPreview = null; NetPreviewNote = ""; return; }
        if (_netPreviews.TryGetValue(net, out var cached))
        {
            NetPreview = cached;
            NetPreviewNote = _netPreviewNotes.GetValueOrDefault(net, "");
            return;
        }

        // Cleared rather than left standing: the outline on the board belongs to the row that WAS
        // selected, and leaving it there while a different row is highlighted is the preview saying
        // the wrong thing rather than saying nothing.
        NetPreview = null;
        NetPreviewNote = "";
        BeginCopperJob(new CopperJob(null, net));
    }

    /// <summary>
    /// Starts one copper job, replacing whatever was in flight.
    /// </summary>
    /// <remarks>
    /// <b>One at a time, and the newest wins.</b> Two of these racing would run two galvanic
    /// partitions of the same board for no gain — and the newest is the one the user is waiting for,
    /// exactly as the Fast solve loop cancels and replaces rather than queueing.
    /// </remarks>
    private void BeginCopperJob(CopperJob job)
    {
        if (Board is not { } board || board.Shapes.Count == 0) return;
        if (_copperJob == job) return;      // already asking this very question

        _copperCts?.Cancel();
        _copperCts?.Dispose();

        var cts = new CancellationTokenSource();
        _copperCts = cts;
        _copperJob = job;
        IsReadingCopper = true;

        // Everything the job reads is captured HERE, on the UI thread, for BuildRequest's own reason:
        // the rows and the board move under a user who keeps working while this runs.
        var shapes = board.Shapes;
        var tech = board.Technology;
        var netPoints = board.NetPoints;
        string? namedReturn = NamedReturnNet;
        var railReference = SelectedRail?.ReferenceLayer;
        var regions = _layerRegions;

        // The resolved return, where there is one for this reference — so the schematic's ground
        // (`0`) is not reported as a short against the net it IS, and so the preview walks the rail
        // the run will: a rail seed never claims the return net (R-rail31-2).
        string? measuredReturn = railReference is { } r && _referenceNetMeasuredOn == r ? _referenceReturnNet : null;

        CopperRead = ReadCopperOffThread(() =>
        {
            regions ??= LayerRegions.Build(shapes, tech);

            PdnReturnNet? measured = null;
            RailNetPreview? preview = null;
            string note = "";

            // R-rail31-1: THE SAME CALL THE EXTRACTION MAKES. The run and this row used to reach the
            // return by two routes, and on a Gerber board only this one found it.
            if (job.MeasureReference is { } layer)
                measured = Regions.ResolveReturnNet(namedReturn, regions, tech, netPoints, layer);

            if (job.PreviewNet is { } net)
            {
                // The reference layer is EXCLUDED from the rail's own seeding by Walk — a pad is a
                // coordinate and a reference plane is usually under all of them (that method's own
                // note). Before the reference is confirmed there is no layer to exclude, and a
                // LayerKey this board does not have is how you say "exclude nothing" to a parameter
                // that is not nullable.
                var walked = Regions.Walk(
                    regions, tech, netPoints, net,
                    railReference ?? AbsentLayer(regions), measuredReturn ?? namedReturn, extraRailSeeds: []);

                var copper = new List<(LayerKey Layer, Paths64 Paths)>();
                var bounds = Bbox.Empty;

                // The reference net is the one case where the rail's own islands are empty and the
                // REFERENCE's are the answer — Walk puts the copper on the confirmed reference layer
                // there, and a user who picks the return still has to be shown what they picked.
                var islands = walked.Power.Count > 0 ? walked.Power : walked.Reference;

                foreach (var island in islands)
                    foreach (var (islandLayer, paths) in island.Copper)
                    {
                        if (paths.Count == 0) continue;
                        copper.Add((islandLayer, paths));
                        bounds = bounds.Union(island.Bounds);
                    }

                preview = new RailNetPreview(net, copper, bounds);

                var sameNet = new List<string> { net };
                if (measuredReturn is { Length: > 0 } ret
                    && (IsSchematicGround(net) || string.Equals(net, ret, StringComparison.OrdinalIgnoreCase)))
                    sameNet.AddRange([ret, SchematicGround]);
                note = NetPreviewShortNote(net, Regions.OtherNetsOn(islands, netPoints, sameNet));
            }

            var finished = regions;
            PostToUi(() => FinishCopperJob(cts, job, finished, namedReturn, measured, preview, note));
        });
    }

    /// <summary>Publishes one copper job's answers, unless it has been superseded.</summary>
    private void FinishCopperJob(
        CancellationTokenSource cts, CopperJob job,
        Dictionary<LayerKey, Paths64> regions, string? namedReturn, PdnReturnNet? measured,
        RailNetPreview? preview, string note)
    {
        // A job from a board that has since changed is DROPPED — not merely stale: it describes
        // copper nobody can see any more, which is the same rule Finish() applies to a solve.
        if (!ReferenceEquals(_copperCts, cts)) { cts.Dispose(); return; }

        _copperCts = null;
        _copperJob = null;
        IsReadingCopper = false;
        cts.Dispose();

        // CopperRead is deliberately LEFT holding the finished task rather than nulled. It exists to
        // be awaited, and a job that finishes before BeginCopperJob has even assigned it would
        // otherwise null the field first and have the assignment put it back — a race whose only
        // victim is a caller trying to wait for the thing that already happened.

        // The FLATTEN is kept whatever the job was for — it is the expensive half and it is the same
        // answer for every question asked of this board.
        _layerRegions = regions;

        if (job.MeasureReference is { } layer)
        {
            // A DIFFERENT return changes what every cached preview walked: a rail seed never claims
            // the return net, so the same pick outlines different copper under a different return.
            if (!string.Equals(_referenceReturnNet, measured?.Net, StringComparison.OrdinalIgnoreCase))
            {
                _netPreviews.Clear();
                _netPreviewNotes.Clear();
            }

            _referenceNetMeasuredOn = layer;
            _referenceMeasuredNamed = namedReturn;
            _referenceReturn = measured;
            _referenceReturnNet = measured?.Net;
            ReferenceMeasurements++;
            RefreshNetMarks();
        }

        if (job.PreviewNet is { } net && preview is not null)
        {
            NetWalksPerformed++;
            _netPreviews[net] = preview;
            _netPreviewNotes[net] = note;

            // Only where that row is still the selected one. A user who arrowed past it while this
            // ran is looking at a different net, and publishing this one would outline the row they
            // left.
            if (string.Equals(SelectedNet?.Name, net, StringComparison.OrdinalIgnoreCase))
            {
                NetPreview = preview;
                NetPreviewNote = note;
            }
        }
    }

    // ── THE SCHEMATIC'S GROUND IS THE MEASURED RETURN (owner, 2026-09-22) ──────────────────────
    //
    // A ground symbol names its net `0` — node 0 is ground, which is an invariant of this code base
    // and not a naming convention — and a board's return is usually stamped or netlisted under a name
    // of its own. The pick list then offered both, `0` and `GND`, as two nets: one of them a name no
    // board designer would recognise, and nothing saying the two were the same metal. Once the
    // return has been MEASURED off the confirmed reference layer, `0` is read as that net: one row,
    // marked, and the row says it is both. Before the measurement nothing is merged — railRF does not
    // know which net the return is yet, and R-rail19-1c forbids acting as though it did.

    /// <summary>The name a schematic's ground symbol gives its net.</summary>
    internal const string SchematicGround = "0";

    internal static bool IsSchematicGround(string? net) =>
        string.Equals(net, SchematicGround, StringComparison.Ordinal);

    /// <summary>The <c>0</c> row taken out of the list while it is merged into the return, so it can
    /// come back if the return moves.</summary>
    private RailNetRowViewModel? _mergedGroundRow;

    private void MergeSchematicGround(string? reference)
    {
        // Undo the last merge first: the return it was merged into may no longer be the return.
        if (_mergedGroundRow is { } merged)
        {
            foreach (var row in AvailableNets) row.AlsoNamed = null;
            if (!AvailableNets.Any(r => IsSchematicGround(r.Name)))
            {
                int at = 0;
                while (at < AvailableNets.Count && string.CompareOrdinal(AvailableNets[at].Name, merged.Name) < 0) at++;
                AvailableNets.Insert(at, merged);
            }
            _mergedGroundRow = null;
        }

        if (reference is not { Length: > 0 } || IsSchematicGround(reference)) return;
        var returnRow = AvailableNets.FirstOrDefault(r => string.Equals(r.Name, reference, StringComparison.OrdinalIgnoreCase));
        var ground = AvailableNets.FirstOrDefault(r => IsSchematicGround(r.Name));
        if (returnRow is null || ground is null) return;

        if (ReferenceEquals(SelectedNet, ground)) SelectedNet = returnRow;
        AvailableNets.Remove(ground);
        _mergedGroundRow = ground;
        returnRow.AlsoNamed = SchematicGround;
    }

    /// <summary>A drawing layer this artwork does not use — see <see cref="ShowNetPreview"/>.</summary>
    private static LayerKey AbsentLayer(Dictionary<LayerKey, Paths64> regions)
    {
        int highest = 0;
        foreach (var key in regions.Keys) highest = Math.Max(highest, key.Layer);
        return new LayerKey(highest + 1, 0);
    }

    /// <summary>
    /// Drops everything measured off this board's copper — <b>called wherever the artwork or the
    /// technology moves under us</b>, which is the one place that already exists for this
    /// (<see cref="NotifyArtworkChanged"/>, <see cref="AdoptTechnology"/>, a new board).
    /// </summary>
    internal void InvalidateNetWalks()
    {
        // A read in flight is of the board that has just gone, so its answer may not be published.
        // It cannot be STOPPED (this file's header says why), only abandoned.
        _copperCts?.Cancel();
        _copperCts?.Dispose();
        _copperCts = null;
        _copperJob = null;
        CopperRead = null;
        IsReadingCopper = false;

        _layerRegions = null;
        _netPreviews.Clear();
        _netPreviewNotes.Clear();
        _referenceNetMeasuredOn = null;
        _referenceMeasuredNamed = null;
        _referenceReturn = null;
        _referenceReturnNet = null;

        // The SELECTION goes with the preview, and that is the point rather than tidiness: a row
        // left highlighted over a board that has stopped outlining it says the pick did nothing.
        // Clearing it also means the next pick walks the copper that is actually loaded.
        SelectedNet = null;
        NetPreview = null;
        NetPreviewNote = "";

        // The marks are CLEARED here and not re-measured, which is the difference between an
        // invalidation and a recomputation. This runs on every live artwork edit in the layout
        // window next door (NotifyArtworkChanged), and a flatten plus a galvanic walk per keystroke
        // is exactly the shape R-rail19-2c exists to forbid. The next thing that actually needs the
        // answer asks for it — RefreshNetMarks, off the pick list or the reference confirmation.
        foreach (var row in AvailableNets) row.IsReferenceReturn = false;
        OnPropertyChanged(nameof(ReferenceReturnNet));
    }

    /// <summary>
    /// Re-states which row of the pick list is the reference return.
    /// </summary>
    /// <remarks>
    /// Called on every confirmation and every rail change, because the answer is about the SELECTED
    /// rail's reference layer and both move it. Nothing is filtered — R-rail19-1d's whole point is
    /// that the row stays, selectable, and says what it is.
    ///
    /// <para>It is also what <see cref="FinishCopperJob"/> calls when a deferred measurement lands,
    /// which is the whole of the re-asking the deferral needs.</para>
    /// </remarks>
    internal void RefreshNetMarks()
    {
        string? reference = ReferenceReturnNet;
        MergeSchematicGround(reference);
        foreach (var row in AvailableNets)
            row.IsReferenceReturn = reference is { Length: > 0 }
                                 && string.Equals(row.Name, reference, StringComparison.OrdinalIgnoreCase);

        OnPropertyChanged(nameof(ReferenceReturnNet));
        OnPropertyChanged(nameof(ReturnNetNote));
        OnPropertyChanged(nameof(HasReturnNetNote));
        OnPropertyChanged(nameof(ReturnNetOptions));
        OnPropertyChanged(nameof(SelectedReturnNetOption));
        OnPropertyChanged(nameof(PickRailButtonText));
        OnPropertyChanged(nameof(WillShowExistingRail));
        PickSelectedNetCommand.NotifyCanExecuteChanged();
    }

    // ── THE REFERENCE ROW NAMES THE RETURN NET (brief-railrf-31 R-rail31-1, R-rail31-3) ─────────
    //
    // The return is a NET, and until this row said which one nothing on the window did: the pick list
    // marked it, and the run took "every piece on the reference layer" because the document named
    // none. Both now read Regions.ResolveReturnNet, and this row shows its answer and where it came
    // from — and is where a user answers the refusal for a return that cannot be resolved.

    /// <summary>The pick list's first row: name no net and let the copper say.</summary>
    public const string MeasuredReturnOption = "measured from the copper";

    /// <summary>What the return-net combo offers: <see cref="MeasuredReturnOption"/>, then every net
    /// on the board — and the named net even where the board does not carry it, so the combo can show
    /// what the document says.</summary>
    public IReadOnlyList<string> ReturnNetOptions
    {
        get
        {
            var options = new List<string> { MeasuredReturnOption };
            if (_document.ReferenceNet is { Length: > 0 } named
                && !AvailableNets.Any(r => string.Equals(r.Name, named, StringComparison.OrdinalIgnoreCase)))
                options.Add(named);
            options.AddRange(AvailableNets.Select(r => r.Name));
            return options;
        }
    }

    /// <summary>
    /// The document's <c>ReferenceNet</c>, as the combo shows it. Picking a net NAMES the return;
    /// picking <see cref="MeasuredReturnOption"/> clears the name and the copper decides.
    /// </summary>
    public string SelectedReturnNetOption
    {
        get => _document.ReferenceNet is { Length: > 0 } named
            ? ReturnNetOptions.FirstOrDefault(o => string.Equals(o, named, StringComparison.OrdinalIgnoreCase)) ?? named
            : MeasuredReturnOption;
        set
        {
            string? named = value is { Length: > 0 } && value != MeasuredReturnOption ? value : null;
            if (string.Equals(_document.ReferenceNet ?? "", named ?? "", StringComparison.OrdinalIgnoreCase)) return;

            _document.ReferenceNet = named;
            OnPropertyChanged();

            // The measurement is keyed by the named net, so the next ask re-resolves; the marks and
            // the note follow when it lands.
            RefreshNetMarks();
            QueueResolve();
        }
    }

    /// <summary>
    /// Which net the return is and where that came from — "'GND', measured from the copper on 'GND'
    /// (layer 3/0)" — or empty before the reference is confirmed and while the answer is in flight.
    /// </summary>
    public string ReturnNetNote =>
        IsReferenceConfirmed && SelectedRail?.ReferenceLayer is { } layer && _referenceNetMeasuredOn == layer
        && string.Equals(_referenceMeasuredNamed, NamedReturnNet, StringComparison.OrdinalIgnoreCase)
        && _referenceReturn is { } ret
            ? "Return: " + ret.Describe()
            : "";

    /// <summary>True while <see cref="ReturnNetNote"/> has something to say.</summary>
    public bool HasReturnNetNote => ReturnNetNote.Length > 0;
}
