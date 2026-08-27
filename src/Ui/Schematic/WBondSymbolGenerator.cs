using System;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.WBond;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Builds the wBond component's <b>dynamic</b> symbol: one PORT per wire array — its <c>+</c> terminal
/// on the left and its <c>−</c> terminal on the right — plus a <c>REF</c> pin (wbond.md §5.1,
/// brief-wbond-wbb R-wbb-5 / D3).
///
/// <h3>Why the symbol is generated rather than drawn</h3>
/// <para>A wBond's pin count is a property of its <i>design</i> — how the user grouped their wires
/// into arrays — so there is no fixed artwork to draw. Arrays are the packaging convention (G1, G2,
/// D1, MT), each carrying its own current, and the schematic has to show one port per array or there
/// is nowhere to wire them.</para>
///
/// <h3>What a port LOOKS like (owner, 2026-08-16)</h3>
/// <para>Each array is a two-terminal port, so it is drawn as one: a lead on the left and a lead on
/// the right at the same row, and inside the body that row reads <c>+ &lt;name&gt; −</c>. Before
/// this the body was an empty box and the only thing naming a lead was its pin name, which nothing
/// drew — so a four-array wBond was eight identical stubs and no way to tell which pair was which.</para>
///
/// <para><b>The polarity is not decoration.</b> <c>+</c> is the array's INPUT end, and which end that
/// is fixes the sign of every mutual inductance this array has (WB3) — the same fact the editor draws
/// with <c>wBond.WireStart</c>. Terminal ORDER and terminal NAMES are both unchanged
/// (<c>Nodes[2k]</c> = <c>&lt;name&gt;.i</c>, <c>Nodes[2k+1]</c> = <c>&lt;name&gt;.o</c>) — see
/// <see cref="PlusPin"/> for why the drawn label and the pin name are deliberately different things.</para>
///
/// <h3>Tight and Loose, exactly as SnP means them</h3>
/// <para><see cref="WBondSymbolPitch.Loose"/> spaces the port rows two connection grids apart and is
/// the default (and the shipped geometry — a wBond placed before this existed is unmoved);
/// <see cref="WBondSymbolPitch.Tight"/> uses one, so an eight-array wBond fits where a four-array one
/// did. The two values, and the row spacings behind them, are SnP's own
/// (<c>SymbolPortDefs.GenerateSnpPorts</c>): a user who has learned one has learned both, and a
/// second vocabulary for the same idea would be a second thing to explain.</para>
///
/// <h3>The content version is load-bearing</h3>
/// <para>Generated symbols are content-addressed and cached on disk. <b>A generator change that does
/// not bump <see cref="ContentVersion"/> leaves stale symbols in place</b> — and for wBond the
/// specific failure is worse than a cosmetic one: reordering arrays produces a symbol with
/// correctly-named pins wired to the wrong nets. Silent, and electrically wrong. This is exactly the
/// MTee failure recorded in <c>project-brief-L5-followups</c>, where a generator fix was invisible
/// because stale on-disk cells survived it.</para>
///
/// <para><b>Bump <see cref="ContentVersion"/> whenever anything here moves a pin, renames one, or
/// changes their order.</b> Cosmetic body changes do not need it; anything a wire attaches to does.</para>
/// </summary>
internal static class WBondSymbolGenerator
{
    /// <summary>
    /// Bump when a change here could move, rename or reorder a pin. See the class remarks — the
    /// failure this prevents is a correctly-labelled pin wired to the wrong net.
    ///
    /// <para>2: the body became a rounded rect carrying per-row <c>+ &lt;name&gt; −</c> labels, and
    /// its WIDTH is now derived from the longest array name — so a pin's x moves on any design whose
    /// names do not fit the old fixed box. Pin NAMES and pin ORDER are unchanged.</para>
    /// </summary>
    internal const int ContentVersion = 2;

    /// <summary>Vertical spacing between port rows, in symbol units, per pitch.</summary>
    private static double RowPitch(WBondSymbolPitch pitch) =>
        pitch == WBondSymbolPitch.Tight ? DsnSymbolReader.PinGrid : DsnSymbolReader.PinGrid * 2;

    /// <summary>Half-width of the body, before it is widened to fit the longest array name.</summary>
    private const double MinHalfWidth = DsnSymbolReader.PinGrid * 3;

    /// <summary>Lead length from the body edge out to a pin.</summary>
    private const double LeadLength = DsnSymbolReader.PinGrid * 2;

    /// <summary>
    /// Font size for the in-body labels.
    ///
    /// <para>Larger than the SDD's own <see cref="BuiltInSymbols.SddPortLabelFontSize"/> (18) because
    /// this body is larger: a wBond's is 600 units wide against an SDD's 200, and its rows are 200
    /// apart at Loose pitch. What is printed here is also longer and more load-bearing — an array's
    /// NAME rather than a port number — so it has to read at the zoom a whole schematic is looked
    /// at.</para>
    /// </summary>
    private const double LabelFontSize = 24.0;

    /// <summary>
    /// The cache key: everything that changes the symbol's shape or its pin identities. Two designs
    /// with the same array names in the same order, at the same pitch, share one symbol; anything
    /// else gets its own.
    /// </summary>
    internal static string ContentKey(WBondDesign design, WBondSymbolPitch pitch = WBondSymbolPitch.Loose,
                                      bool referencePin = false)
    {
        ArgumentNullException.ThrowIfNull(design);
        return $"wbond-v{ContentVersion}:{pitch}:{referencePin}:"
             + string.Join('|', design.Arrays.Select(a => a.Name));
    }

    /// <summary>
    /// Builds the symbol for a design. Returns null when the design declares no arrays — there are no
    /// ports, so there is nothing placeable.
    /// </summary>
    internal static Symbol? Build(WBondDesign design, WBondSymbolPitch pitch = WBondSymbolPitch.Loose,
                                  bool referencePin = false)
    {
        ArgumentNullException.ThrowIfNull(design);
        return Build([.. design.Arrays.Select(a => a.Name)], pitch, referencePin);
    }

    /// <summary>
    /// Builds the symbol from the ordered array names alone — everything the artwork depends on
    /// besides the pitch.
    ///
    /// <para>This is the primary form: <see cref="WBondSymbolProvider"/> caches on exactly this list
    /// plus the pitch, so the symbol never depends on decoding a whole design on a render pass.</para>
    /// </summary>
    /// <param name="referencePin">
    /// Whether to append the floating <c>REF</c> terminal. Off by default, matching SnP's own
    /// <c>RefNode</c> and <c>WBondModel</c>'s. It is always the LAST pin, so turning it off renumbers
    /// nothing — which is what lets the two lists stay in step.
    /// </param>
    internal static Symbol? Build(IReadOnlyList<string> arrayNames,
                                  WBondSymbolPitch pitch = WBondSymbolPitch.Loose,
                                  bool referencePin = false)
    {
        ArgumentNullException.ThrowIfNull(arrayNames);
        if (arrayNames.Count == 0) return null;

        int m = arrayNames.Count;
        double rowPitch = RowPitch(pitch);

        // Rows are centred vertically, so a one-array wBond is not lopsided and an eight-array one
        // grows symmetrically about its origin.
        double firstRowY = -(m - 1) * rowPitch / 2.0;

        // The body has to clear the port rows AND leave room for REF below them.
        double halfHeight = Math.Max((m - 1) * rowPitch / 2.0 + rowPitch, rowPitch);

        // ...and it has to be wide enough for the widest array NAME plus the two polarity marks, or
        // the labels the ports are identified by run out over their own leads. Measured from the
        // character count rather than from a font metric: nothing here has a typeface in hand, and
        // over-estimating merely makes the box a little roomy.
        double halfWidth = HalfWidthFor(arrayNames);

        var pins = new List<SymbolPin>(2 * m + 1);
        var primitives = new List<SymbolPrimitive>
        {
            new RoundedRectPrimitive
            {
                ColorRole = SymbolColorRole.SymbolLine,
                StrokeTier = SymbolStrokeTier.Normal,
                Cx = 0, Cy = 0, W = halfWidth * 2, H = halfHeight * 2, Radius = 12,
            },
        };

        for (int k = 0; k < m; k++)
        {
            string name = arrayNames[k];
            double y = DsnSymbolReader.SnapToPinGrid(firstRowY + k * rowPitch);

            double inX = DsnSymbolReader.SnapToPinGrid(-halfWidth - LeadLength);
            double outX = DsnSymbolReader.SnapToPinGrid(halfWidth + LeadLength);

            // Pin NUMBERS are 1-based and follow the model's terminal order exactly — the stamp reads
            // Nodes[2k] and Nodes[2k+1], so this ordering is not presentation, it is the wiring.
            pins.Add(new SymbolPin(inX, y, 2 * k + 1, PlusPin(name)));
            pins.Add(new SymbolPin(outX, y, 2 * k + 2, MinusPin(name)));

            primitives.Add(new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Thin,
                                            inX, y, -halfWidth, y));
            primitives.Add(new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Thin,
                                            halfWidth, y, outX, y));

            // The polarity marks sit at the two inner edges and the array's own name between them, so
            // one row reads left-to-right as "+ G1 −". The plus takes SymbolPlus, matching the SDD's
            // own convention for a polarity mark.
            primitives.Add(Text("+", -halfWidth + PolarityInset, y,
                                SymbolTextAlign.Left, SymbolColorRole.SymbolPlus));
            primitives.Add(Text("−", halfWidth - PolarityInset, y, SymbolTextAlign.Right));
            primitives.Add(Text(name, 0, y, SymbolTextAlign.Center));
        }

        // REF hangs below the body, when it is asked for. It is a declaration rather than a stamped
        // connection — the return-path refusal keys off the ground-plane setting, not off this pin —
        // but it must be wirable, because the user has to be able to SAY which net is the reference
        // plane on a design where that is not simply ground.
        if (referencePin)
        {
            double refY = DsnSymbolReader.SnapToPinGrid(halfHeight + LeadLength);
            pins.Add(new SymbolPin(0, refY, 2 * m + 1, "REF"));
            primitives.Add(new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Thin,
                                            0, halfHeight, 0, refY));
            primitives.Add(Text("REF", 0, halfHeight - PolarityInset, SymbolTextAlign.Center));
        }

        return new Symbol(primitives, pins);
    }

    /// <summary>
    /// The name of an array's <c>+</c> terminal — its INPUT end (WB3).
    ///
    /// <h3>Why the NAME is still <c>.i</c> when the label says <c>+</c></h3>
    /// <para>A pin's name here must equal <c>WBondModel.TerminalNames[k]</c> exactly — that is a
    /// stated invariant with a test of its own (<c>Tier6_SymbolPinNames_MatchTheModelsTerminalNames</c>),
    /// and it is what keeps pin <i>k</i> and node <i>k</i> from drifting apart into a
    /// correctly-labelled pin wired to the wrong net. Those terminal names are also what a
    /// measurement path spells (<c>V(W1.G1.i)</c>).</para>
    ///
    /// <para>So the polarity the owner asked for is expressed where it is actually read — in the
    /// DRAWN label, <c>+ G1 −</c> across the row — and the identity underneath is left alone.
    /// Renaming the model's terminals to carry a Unicode minus would re-spell every existing
    /// measurement path for a change nobody would see on the schematic.</para>
    /// </summary>
    internal static string PlusPin(string arrayName) => arrayName + ".i";

    /// <summary>The name of an array's <c>−</c> terminal — its output end. See <see cref="PlusPin"/>.</summary>
    internal static string MinusPin(string arrayName) => arrayName + ".o";

    /// <summary>How far inside the body edge a polarity mark is drawn.</summary>
    private const double PolarityInset = 22.0;

    /// <summary>
    /// Half the body width: wide enough for the longest array name with both polarity marks beside
    /// it, and never narrower than <see cref="MinHalfWidth"/> so a design of one-character names still
    /// draws a box rather than a slab.
    ///
    /// <para>Rounded UP to the connection grid, because the pin leads start at this edge and every pin
    /// tip must land on a grid multiple (<see cref="DsnSymbolReader.SnapToPinGrid"/> would otherwise
    /// pull the tip off the edge the lead was drawn from, leaving a lead of a different length than
    /// the one specified).</para>
    /// </summary>
    private static double HalfWidthFor(IReadOnlyList<string> arrayNames)
    {
        int longest = 0;
        foreach (string name in arrayNames) longest = Math.Max(longest, name.Length);

        // ~0.6 em per character is the usual conservative estimate for a proportional face; the two
        // polarity marks and their insets are added whole.
        double needed = longest * LabelFontSize * 0.6 + 2 * (PolarityInset + LabelFontSize);

        double half = Math.Max(MinHalfWidth, needed / 2.0);
        return Math.Ceiling(half / DsnSymbolReader.PinGrid) * DsnSymbolReader.PinGrid;
    }

    private static TextPrimitive Text(string content, double x, double y, SymbolTextAlign align,
                                      SymbolColorRole role = SymbolColorRole.SymbolLine) =>
        new()
        {
            Content = content,
            AnchorX = x,
            AnchorY = y,
            FontSize = LabelFontSize,
            Align = align,
            VAlign = SymbolTextVAlign.Middle,
            ColorRole = role,
            // Every label this generator writes is a word or a terminal name, so a rotated instance
            // keeps it upright rather than spinning it (BuiltInSymbols.Txt carries the same default).
            ForceReadable = true,
        };

    /// <summary>
    /// The one-line body annotation: what the component is, at a glance, without opening it
    /// (wbond.md §5.1).
    /// </summary>
    /// <param name="unit">
    /// The unit the total length is reported in — the WORKSPACE technology's own
    /// (<c>.ctech</c>'s <c>DefaultDisplayUnit</c>), never a hard-coded one (owner, 2026-08-17: "units
    /// mentioned in the texts need to respect the units of the .ctech file for the workspace"). The
    /// default is mils, which is what a bonder works in and what the wBond editor opens on.
    /// </param>
    internal static string Describe(WBondDesign design, LayoutUnit unit = LayoutUnit.Mil)
    {
        ArgumentNullException.ThrowIfNull(design);

        int wires = design.WireCount;

        // Path length is metres; one DBU is one NANOMETRE at 1,000 DBU/µm, which is the resolution the
        // wire model itself stores in — so the conversion into the display unit is the layout's own
        // formatter with nothing invented here.
        long totalNm = (long)Math.Round(design.AllWires().Sum(w => w.PathLengthMetres()) * 1e9);

        string arrays = design.Arrays.Count == 1 ? "1 array" : $"{design.Arrays.Count} arrays";
        string wireText = wires == 1 ? "1 wire" : $"{wires} wires";
        string total = LayoutUnits.Format(totalNm, unit, LayoutUnits.DefaultDbuPerMicron, maxDecimals: 1);

        return $"{arrays} · {wireText} · {total} {LayoutUnits.Suffix(unit)} total";
    }
}
