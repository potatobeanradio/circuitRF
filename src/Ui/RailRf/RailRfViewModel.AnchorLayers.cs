// Which copper a coordinate anchor means (brief-railrf-34 R-rail34-2).
//
// ── WHAT WAS WRONG ──────────────────────────────────────────────────────────────────────────────
//
// A coordinate is a place on the board, not a place on a layer, and the region walk seeded every
// copper layer at it but the reference. A load dropped on a VDD pad with a 3v3 pour under it on the
// other side therefore made one rail of two supplies, and the answer was plausible and wrong.
//
// ── THE WINDOW'S TWO HALVES ─────────────────────────────────────────────────────────────────────
//
// 1. Every coordinate this window PLACES records its layer: the topmost copper under the click among
//    the layers the board view is SHOWING (brief 20's per-layer visibility), not the topmost in the
//    stackup. Hiding a layer is how a user says "not that one", and the click already means "the
//    copper I can see here".
// 2. A document that states no layer where the point stands on more than one net is refused by the
//    extraction, which names each candidate. The refusal carries them as data, and this offers each as
//    one click — the choice is the user's, and a sentence alone would send them to edit the `.crail`.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.RailRf;

/// <summary>
/// One copper a coordinate anchor could mean, offered as one click (R-rail34-2).
/// </summary>
/// <param name="RailName">The rail.</param>
/// <param name="IsSource">A source row; false is a load row.</param>
/// <param name="Index">0-based, in that list.</param>
/// <param name="X">The anchor's coordinate, DBU — checked again on the click, so an offer outlived by
/// an edit to the row does nothing.</param>
/// <param name="Y">DBU.</param>
/// <param name="Layer">The layer the click writes onto the anchor.</param>
/// <param name="Text">What the offer says: the row, then the copper.</param>
public sealed record RailAnchorLayerOffer(
    string RailName, bool IsSource, int Index, long X, long Y, LayerKey Layer, string Text);

public sealed partial class RailRfViewModel
{
    /// <summary>Every copper each ambiguous anchor of the last refused run could mean.</summary>
    public ObservableCollection<RailAnchorLayerOffer> AnchorLayerOffers { get; } = [];

    /// <summary>True while there is a choice to offer.</summary>
    public bool HasAnchorLayerOffers => AnchorLayerOffers.Count > 0;

    /// <summary>
    /// The topmost copper layer at the point among the layers the board view is showing, or null
    /// where none has copper there (or there is no board).
    /// </summary>
    /// <remarks>
    /// "Topmost" is the view's own drawing order — what the user sees on top at that place — and the
    /// stackup's order only breaks a tie. The candidate set is <see cref="Regions.CopperLayersAt"/>,
    /// the walk's own geometry, so a layer recorded here is one the walk then finds copper on.
    /// <para>The rail's REFERENCE layer is never the answer: the walk seeds nothing there (a conductor
    /// cannot be its own return), so recording it made an anchor over an inner plane seed nothing at
    /// all, where the same click with no layer seeded the copper under the plane.</para>
    /// </remarks>
    internal LayerKey? ShownCopperLayerAt(long xDbu, long yDbu, LayerKey? reference = null)
    {
        if (Board is not { } board) return null;

        var tech = board.Technology;
        var hidden = BoardOverlayLayer.HiddenLayers;
        var defs = tech.Layers.ToDictionary(l => l.Key);

        int StackRank(LayerKey k)
        {
            for (int i = 0; i < tech.Stackup.Layers.Count; i++)
                if (tech.Stackup.Layers[i].DrawingLayers.Contains(k)) return i;
            return int.MaxValue;
        }

        return Regions.CopperLayersAt(board.Shapes, tech, xDbu, yDbu)
            .Where(k => k != reference)
            .Where(k => !hidden.Contains(k) && !(defs.TryGetValue(k, out var d) && !d.Visible))
            .OrderByDescending(k => defs.TryGetValue(k, out var d) ? d.ZOrder : int.MinValue)
            .ThenBy(StackRank)
            .Select(k => (LayerKey?)k)
            .FirstOrDefault();
    }

    /// <summary>A coordinate anchor with the layer <see cref="ShownCopperLayerAt"/> reads under it;
    /// any other anchor, and a point on no shown copper, unchanged.</summary>
    private RailPortAnchor WithShownLayer(RailPortAnchor anchor, LayerKey? reference = null) =>
        anchor is { Refdes: not { Length: > 0 }, Point: { } p, Layer: null }
        && ShownCopperLayerAt(p.X, p.Y, reference) is { } layer
            ? anchor with { Layer = layer }
            : anchor;

    /// <summary>Replaces the offers with the ones <paramref name="ambiguous"/> makes — empty after
    /// any run that did not refuse for this reason.</summary>
    private void OfferAnchorLayers(IReadOnlyList<PdnAnchorAmbiguity> ambiguous)
    {
        if (AnchorLayerOffers.Count == 0 && ambiguous.Count == 0) return;

        AnchorLayerOffers.Clear();
        var fmt = BoardLengthFormat();
        double dbuPerMm = (Board?.DbuPerMicron ?? LayoutUnits.DefaultDbuPerMicron) * 1000.0;

        foreach (var a in ambiguous)
        {
            string row = $"{(a.IsSource ? "Source" : "Load")} {a.Index + 1} at {fmt.Point(a.X, a.Y)}";
            foreach (var c in a.Candidates)
                AnchorLayerOffers.Add(new RailAnchorLayerOffer(
                    a.RailName, a.IsSource, a.Index, a.X, a.Y, c.Layer,
                    $"{row}: {c.LayerName}, " +
                    $"{(c.Net is { Length: > 0 } net ? $"net '{net}'" : "no net named")}, " +
                    $"{c.AreaSquareDbu / (dbuPerMm * dbuPerMm):0.###} mm²"));
        }

        OnPropertyChanged(nameof(HasAnchorLayerOffers));
    }

    /// <summary>Writes the offered layer onto the anchor it was offered for, and re-solves.</summary>
    [RelayCommand]
    private void ChooseAnchorLayer(RailAnchorLayerOffer? offer)
    {
        if (offer is null || _document.Rail(offer.RailName) is not { } rail) return;

        // The row the offer was made for, still anchored at that point with no layer — anything else
        // is an edit the offer did not see, and the next run will say what it means.
        static bool Same(RailPortAnchor a, RailAnchorLayerOffer o) =>
            a.Refdes is not { Length: > 0 } && a.Layer is null && a.Point == (o.X, o.Y);

        if (offer.IsSource)
        {
            if (offer.Index >= rail.Sources.Count || !Same(rail.Sources[offer.Index].Anchor, offer)) return;
            rail.Sources[offer.Index] = rail.Sources[offer.Index] with
            {
                Anchor = rail.Sources[offer.Index].Anchor with { Layer = offer.Layer },
            };
        }
        else
        {
            if (offer.Index >= rail.Loads.Count || !Same(rail.Loads[offer.Index].Anchor, offer)) return;
            rail.Loads[offer.Index] = rail.Loads[offer.Index] with
            {
                Anchor = rail.Loads[offer.Index].Anchor with { Layer = offer.Layer },
            };
        }

        // The other candidates of this anchor are answered; the rest wait for the re-run.
        foreach (var answered in AnchorLayerOffers
                     .Where(o => o.RailName == offer.RailName && o.IsSource == offer.IsSource && o.Index == offer.Index)
                     .ToList())
            AnchorLayerOffers.Remove(answered);
        OnPropertyChanged(nameof(HasAnchorLayerOffers));

        if (ReferenceEquals(SelectedRail, rail)) RebuildForSelectedRail();
        QueueResolve();
    }
}
