using CircuitRF.WBond;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Builds the wBond component's <b>dynamic</b> symbol: two pins per wire array, input left and
/// output right, plus a <c>REF</c> pin (wbond.md §5.1, brief-wbond-wbb R-wbb-5 / D3).
///
/// <h3>Why the symbol is generated rather than drawn</h3>
/// <para>A wBond's pin count is a property of its <i>design</i> — how the user grouped their wires
/// into arrays — so there is no fixed artwork to draw. Arrays are the packaging convention (G1, G2,
/// D1, MT), each carrying its own current, and the schematic has to show one pin pair per array or
/// there is nowhere to wire them.</para>
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
    /// </summary>
    internal const int ContentVersion = 1;

    /// <summary>Vertical spacing between array pin pairs, in symbol units (one connection grid).</summary>
    private const double RowPitch = DsnSymbolReader.PinGrid * 2;

    /// <summary>Half-width of the body.</summary>
    private const double HalfWidth = DsnSymbolReader.PinGrid * 3;

    /// <summary>Lead length from the body edge out to a pin.</summary>
    private const double LeadLength = DsnSymbolReader.PinGrid * 2;

    /// <summary>
    /// The cache key: everything that changes the symbol's shape or its pin identities. Two designs
    /// with the same array names in the same order share one symbol; anything else gets its own.
    /// </summary>
    internal static string ContentKey(WBondDesign design)
    {
        ArgumentNullException.ThrowIfNull(design);
        return $"wbond-v{ContentVersion}:" + string.Join('|', design.Arrays.Select(a => a.Name));
    }

    /// <summary>
    /// Builds the symbol for a design. Returns null when the design declares no arrays — there are no
    /// pins, so there is nothing placeable.
    /// </summary>
    internal static Symbol? Build(WBondDesign design)
    {
        ArgumentNullException.ThrowIfNull(design);
        return Build([.. design.Arrays.Select(a => a.Name)]);
    }

    /// <summary>
    /// Builds the symbol from the ordered array names alone — everything the artwork depends on.
    ///
    /// <para>This is the primary form: <see cref="WBondSymbolProvider"/> caches on exactly this list,
    /// so the symbol never depends on decoding a whole design on a render pass.</para>
    /// </summary>
    internal static Symbol? Build(IReadOnlyList<string> arrayNames)
    {
        ArgumentNullException.ThrowIfNull(arrayNames);
        if (arrayNames.Count == 0) return null;

        int m = arrayNames.Count;

        // Rows are centred vertically, so a one-array wBond is not lopsided and an eight-array one
        // grows symmetrically about its origin.
        double firstRowY = -(m - 1) * RowPitch / 2.0;

        // The body has to clear the array rows AND leave room for REF below them.
        double halfHeight = Math.Max((m - 1) * RowPitch / 2.0 + RowPitch, RowPitch);

        var pins = new List<SymbolPin>(2 * m + 1);
        var primitives = new List<SymbolPrimitive>
        {
            new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal, -HalfWidth, -halfHeight,  HalfWidth, -halfHeight),
            new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal,  HalfWidth, -halfHeight,  HalfWidth,  halfHeight),
            new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal,  HalfWidth,  halfHeight, -HalfWidth,  halfHeight),
            new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal, -HalfWidth,  halfHeight, -HalfWidth, -halfHeight),
        };

        for (int k = 0; k < m; k++)
        {
            string name = arrayNames[k];
            double y = DsnSymbolReader.SnapToPinGrid(firstRowY + k * RowPitch);

            double inX = DsnSymbolReader.SnapToPinGrid(-HalfWidth - LeadLength);
            double outX = DsnSymbolReader.SnapToPinGrid(HalfWidth + LeadLength);

            // Pin NUMBERS are 1-based and follow the model's terminal order exactly — the stamp reads
            // Nodes[2k] and Nodes[2k+1], so this ordering is not presentation, it is the wiring.
            pins.Add(new SymbolPin(inX, y, 2 * k + 1, $"{name}.i"));
            pins.Add(new SymbolPin(outX, y, 2 * k + 2, $"{name}.o"));

            primitives.Add(new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Thin,
                                            inX, y, -HalfWidth, y));
            primitives.Add(new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Thin,
                                            HalfWidth, y, outX, y));
        }

        // REF hangs below the body. It is a declaration rather than a stamped connection — the model
        // refuses to solve when the return path is undeclared — but it must be wirable, because the
        // user has to be able to SAY which net is the reference plane.
        double refY = DsnSymbolReader.SnapToPinGrid(halfHeight + LeadLength);
        pins.Add(new SymbolPin(0, refY, 2 * m + 1, "REF"));
        primitives.Add(new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Thin,
                                        0, halfHeight, 0, refY));

        return new Symbol(primitives, pins);
    }

    /// <summary>
    /// The one-line body annotation: what the component is, at a glance, without opening it
    /// (wbond.md §5.1).
    /// </summary>
    internal static string Describe(WBondDesign design)
    {
        ArgumentNullException.ThrowIfNull(design);

        int wires = design.WireCount;
        double totalMm = design.AllWires().Sum(w => w.PathLengthMetres()) * 1e3;

        string arrays = design.Arrays.Count == 1 ? "1 array" : $"{design.Arrays.Count} arrays";
        string wireText = wires == 1 ? "1 wire" : $"{wires} wires";

        return $"{arrays} · {wireText} · {totalMm:F1} mm total";
    }
}
