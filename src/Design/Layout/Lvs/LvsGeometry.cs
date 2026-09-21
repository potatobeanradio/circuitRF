// WHERE the layout's objects are — brief-lvs-8-findings.md R-lvs8-2c, docs/design/lvs.md §6.5.
//
// ── WHY THIS IS NOT ON LvsNetlist ─────────────────────────────────────────────────────────────
//
// R-lvs3-1a: one netlist type serves both sides and the comparator must not be able to tell them
// apart. A `.csch` has no DBU, no layers and no copper, so a coordinate field on LvsNetlist would
// be filled on one side and empty on the other — which is exactly the asymmetry that file's own
// header says will eventually be branched on. So the artwork's geometry rides beside the netlist
// instead, in the one index space the netlist already uses: device index, terminal index, net
// index.
//
// ── IT IS FOR THE REPORT, AND THE COMPARISON NEVER SEES IT ────────────────────────────────────
//
// Nothing in brief 7 takes one of these. A marker, a short's path and an island's outline are what
// a user is shown AFTER the answer is known; letting geometry reach the matching would make a
// comparison that two identical netlists could answer differently because one was drawn somewhere
// else.

using System.Linq;
using CircuitRF.Design.Layout.Drc;
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Design.RailRf;

namespace CircuitRF.Design.Layout.Lvs;

/// <summary>Where one terminal's pad sits — one entry per PAD, so a terminal that is several pads
/// has several.</summary>
/// <param name="Device">Index into <see cref="LvsNetlist.Devices"/>.</param>
/// <param name="Terminal">Index into that device's <see cref="LvsDevice.Terminals"/>.</param>
/// <param name="X">DBU.</param>
/// <param name="Y">DBU.</param>
/// <param name="Layer">The pad's own drawing layer — the one its net was read on (R-ab2-2d).</param>
/// <param name="WidthDbu">The land pattern's stated pin width, or 0.</param>
/// <param name="Piece">The partition PIECE under it, or -1 where the pad landed on no copper.</param>
public readonly record struct LvsPadGeometry(
    int Device, int Terminal, long X, long Y, LayerKey Layer, long WidthDbu, int Piece);

/// <summary>
/// The artwork's own geometry, in the netlist's index space — <b>a side channel, filled by
/// <see cref="LayoutRead"/> and read only by the report</b>.
/// </summary>
public sealed class LvsGeometry
{
    /// <summary>Nothing was drawn, or nothing was read — every marker comes back empty and every
    /// finding is a run-level line.</summary>
    public static readonly LvsGeometry None =
        new(CopperPieces.Empty, [], [], RailLengthFormat.Dbu, new Dictionary<LayerKey, string>());

    private readonly IReadOnlyList<int> _netOfPiece;
    private readonly Dictionary<int, List<int>> _piecesOfNet = [];
    private readonly Dictionary<int, List<int>> _padsOfDevice = [];

    private readonly IReadOnlyDictionary<LayerKey, string> _layerNames;

    internal LvsGeometry(
        CopperPieces pieces,
        IReadOnlyList<LvsPadGeometry> pads,
        IReadOnlyList<int> netOfPiece,
        RailLengthFormat format,
        IReadOnlyDictionary<LayerKey, string> layerNames)
    {
        Pieces = pieces;
        Pads = pads;
        Format = format;
        _netOfPiece = netOfPiece;
        _layerNames = layerNames;

        for (int p = 0; p < netOfPiece.Count; p++)
        {
            if (netOfPiece[p] < 0) continue;
            (_piecesOfNet.TryGetValue(netOfPiece[p], out var list) ? list : _piecesOfNet[netOfPiece[p]] = []).Add(p);
        }

        for (int i = 0; i < pads.Count; i++)
            (_padsOfDevice.TryGetValue(pads[i].Device, out var list)
                ? list
                : _padsOfDevice[pads[i].Device] = []).Add(i);
    }

    /// <summary>The partition the pads were located in — and the spanning forest a short's path
    /// walks.</summary>
    internal CopperPieces Pieces { get; }

    /// <summary>Every pad of every device, in the netlist's own index space.</summary>
    public IReadOnlyList<LvsPadGeometry> Pads { get; }

    /// <summary>The layout's own display unit, so a coordinate in a sentence carries the unit the
    /// designer draws in rather than a number in a unit nobody stated.</summary>
    public RailLengthFormat Format { get; }

    /// <summary>Whether anything at all was located.</summary>
    public bool Any => Pieces.Any;

    /// <summary>The <see cref="LvsNetlist.Nets"/> index a partition piece belongs to, or -1 where
    /// no terminal put it on one — which is R-lvs8-5c's floating copper.</summary>
    public int NetOfPiece(int piece)
        => piece >= 0 && piece < _netOfPiece.Count ? _netOfPiece[piece] : -1;

    /// <summary>Every partition piece on one netlist net, ascending.</summary>
    public IReadOnlyList<int> PiecesOfNet(int net)
        => _piecesOfNet.TryGetValue(net, out var list) ? list : [];

    /// <summary>Every pad of one device, ascending.</summary>
    public IReadOnlyList<int> PadsOfDevice(int device)
        => _padsOfDevice.TryGetValue(device, out var list) ? list : [];

    /// <summary>The pads of one terminal — <b>a list</b>, because a bonded ground is one terminal
    /// and several pads.</summary>
    public IReadOnlyList<LvsPadGeometry> PadsOf(int device, int terminal)
        => [.. PadsOfDevice(device).Select(i => Pads[i]).Where(p => p.Terminal == terminal)];

    /// <summary>A partition piece's outline, as the DRC marker convention spells one.</summary>
    internal long[][] RingsOfPiece(int piece) => DrcRegions.ToRings(Pieces.PathsOfPiece(piece));

    /// <summary>A partition piece's bounding box.</summary>
    public Bbox BoundsOfPiece(int piece) => Pieces.BoundsOfPiece(piece);

    /// <summary>What the technology calls a drawing layer — so a short's sentence says "a neck of
    /// Metal1" rather than "a neck of 1/0".</summary>
    public string NameOfLayer(LayerKey layer)
        => _layerNames.TryGetValue(layer, out string? name) && name.Length > 0
            ? name
            : $"layer {layer.Layer}/{layer.Datatype}";
}
