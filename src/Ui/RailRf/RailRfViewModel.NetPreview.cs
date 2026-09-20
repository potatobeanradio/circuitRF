// Picking a net SHOWS you the net — the preview highlight and the measured reference return
// (docs/sonnet-briefs/brief-railrf-19-unreachable-states.md R-rail19-1c, R-rail19-1d, R-rail19-2).
//
// ── THERE IS NO SECOND CONNECTIVITY WALK HERE ──────────────────────────────────────────────────
//
// R-rail19-4's scope rule, and PdnRailRegions' own header says it first: DrcConnectivity partitions
// the copper, PdnRailRegions joins that to a net name, and the extraction the numbers come from
// calls the same function. Everything in this file goes through PdnRailRegions.Walk and
// PdnRailRegions.ReferenceNetOn. A preview drawn from a walk of its own would be a picture that
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

using System;
using System.Collections.Generic;
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

    /// <summary>How many PREVIEW walks this board has paid for — <b>counted so R-rail19-2c is
    /// testable without timing anything</b>, which is the same reason <c>SolvesStarted</c> exists.
    /// The reference-return measurement is not one of these; it is counted by
    /// <see cref="ReferenceMeasurements"/>, because the two answer different questions and a single
    /// counter would make each one's test depend on the other having run.</summary>
    internal int NetWalksPerformed { get; private set; }

    /// <summary>How many times the reference return has been measured off this board.</summary>
    internal int ReferenceMeasurements { get; private set; }

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
    /// Which net the confirmed reference layer's copper belongs to, measured from the artwork — or
    /// null before the reference is confirmed, and null where the measurement does not resolve.
    /// </summary>
    /// <remarks>
    /// <b>Null before the confirmation is the requirement, not a default</b> (R-rail19-1d). railRF
    /// does not know which net the return is until somebody has said which layer it is on, and a
    /// pick list that marked a row as though it did would be making exactly the name-shaped guess
    /// R-rail19-1c refuses.
    /// </remarks>
    public string? ReferenceReturnNet
    {
        get
        {
            if (!IsReferenceConfirmed || SelectedRail?.ReferenceLayer is not { } layer) return null;
            if (_referenceNetMeasuredOn == layer) return _referenceReturnNet;

            if (LayerRegions() is not { } regions || Board is not { } board) return null;

            _referenceNetMeasuredOn = layer;
            ReferenceMeasurements++;
            _referenceReturnNet =
                PdnRailRegions.ReferenceNetOn(regions, board.Technology, board.NetPoints, layer);

            return _referenceReturnNet;
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
    /// </remarks>
    private void ShowNetPreview(string? net)
    {
        if (net is not { Length: > 0 }) { NetPreview = null; return; }
        if (_netPreviews.TryGetValue(net, out var cached)) { NetPreview = cached; return; }
        if (LayerRegions() is not { } regions || Board is not { } board) { NetPreview = null; return; }

        // The reference layer is EXCLUDED from the rail's own seeding by Walk — a pad is a coordinate
        // and a reference plane is usually under all of them (that method's own note). Before the
        // reference is confirmed there is no layer to exclude, and a LayerKey this board does not
        // have is how you say "exclude nothing" to a parameter that is not nullable.
        var referenceLayer = SelectedRail?.ReferenceLayer ?? AbsentLayer(regions);

        NetWalksPerformed++;
        var walked = PdnRailRegions.Walk(
            regions, board.Technology, board.NetPoints, net,
            referenceLayer, board.ReferenceNet, extraRailSeeds: []);

        var copper = new List<(LayerKey Layer, Paths64 Paths)>();
        var bounds = Bbox.Empty;

        // The reference net is the one case where the rail's own islands are empty and the
        // REFERENCE's are the answer — Walk puts the copper on the confirmed reference layer there,
        // and a user who picks the return still has to be shown what they picked.
        var islands = walked.Power.Count > 0 ? walked.Power : walked.Reference;

        foreach (var island in islands)
            foreach (var (layer, paths) in island.Copper)
            {
                if (paths.Count == 0) continue;
                copper.Add((layer, paths));
                bounds = bounds.Union(island.Bounds);
            }

        var preview = new RailNetPreview(net, copper, bounds);
        _netPreviews[net] = preview;
        NetPreview = preview;
    }

    /// <summary>The flattened copper every walk takes as its input, built once per board.</summary>
    private Dictionary<LayerKey, Paths64>? LayerRegions()
    {
        if (_layerRegions is not null) return _layerRegions;
        if (Board is not { } board || board.Shapes.Count == 0) return null;

        // The extraction's own flatten, so a preview cannot outline copper the solve does not see.
        return _layerRegions = PdnMeshExtractor.BuildLayerRegions(board.Shapes, board.Technology);
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
        _layerRegions = null;
        _netPreviews.Clear();
        _referenceNetMeasuredOn = null;
        _referenceReturnNet = null;

        // The SELECTION goes with the preview, and that is the point rather than tidiness: a row
        // left highlighted over a board that has stopped outlining it says the pick did nothing.
        // Clearing it also means the next pick walks the copper that is actually loaded.
        SelectedNet = null;
        NetPreview = null;

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
    /// </remarks>
    internal void RefreshNetMarks()
    {
        string? reference = ReferenceReturnNet;
        foreach (var row in AvailableNets)
            row.IsReferenceReturn = reference is { Length: > 0 }
                                 && string.Equals(row.Name, reference, StringComparison.OrdinalIgnoreCase);

        OnPropertyChanged(nameof(ReferenceReturnNet));
        OnPropertyChanged(nameof(PickRailButtonText));
        PickSelectedNetCommand.NotifyCanExecuteChanged();
    }
}
