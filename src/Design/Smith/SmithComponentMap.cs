// The one place the Smith Chart element vocabulary is tied to circuitRF's own components
// (docs/design/smith-chart.md §3.3; brief-smith-1-document.md R-smith1-2).
//
// WHY IT IS HERE AND WHY THERE IS ONLY ONE OF IT. Brief 2 needs it to build the oracle's netlist,
// brief 6 needs it to draw the network strip, brief 7 needs it to copy a real schematic out and to
// recognise one pasted in, and SmithDesign.Refusal needs it to know which of the flat Values bag a
// given kind actually reads. Three or four copies of a table like this drift, and the symptom of
// the drift is a picture that looks right.
//
// NOTHING IS TRANSCRIBED FROM ComponentTypeRegistry. This file names a SymbolKind and a port count
// and stops; the engine reference string, the parameter names, the defaults and the units all come
// from ComponentTypeRegistry itself at the call site. That is deliberate: the engine reference for
// ZPort is "Z_Port", not "ZPort", and a hand-copied table here would have been the place that was
// wrong.

using CircuitRF.Core.Devices;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.Smith;

/// <summary>What a <see cref="SmithElementKind"/> IS, in circuitRF's own component terms.</summary>
/// <param name="SymbolKind">The <c>ComponentTypeRegistry</c> entry this element places. Ask that
/// registry for the engine reference, the parameters and their defaults — never this type.</param>
/// <param name="NumPorts">The <c>NumPorts</c> parameter, for the two kinds that take one
/// (<c>ZPort</c> and <c>Snp</c>). Zero where the component has no such parameter.</param>
public readonly record struct SmithComponentBinding(SymbolKind SymbolKind, int NumPorts);

/// <summary>The §3.3 table, written down once.</summary>
public static class SmithComponentMap
{
    /// <summary>
    /// The component this element places.
    ///
    /// <para>The three line kinds all bind to the same <c>Tline</c>: an open stub and a shorted stub
    /// are not other components, they are the same ideal line with its far end left open or tied to
    /// ground, which is a wiring decision brief 6 makes and brief 7 copies out.</para>
    /// </summary>
    public static SmithComponentBinding Component(SmithElementKind kind) => kind switch
    {
        SmithElementKind.R           => new(SymbolKind.Resistor,  0),
        SmithElementKind.L           => new(SymbolKind.Inductor,  0),
        SmithElementKind.C           => new(SymbolKind.Capacitor, 0),
        SmithElementKind.Srlc        => new(SymbolKind.Srlc,      0),
        SmithElementKind.Prlc        => new(SymbolKind.Prlc,      0),
        SmithElementKind.Srl         => new(SymbolKind.Srl,       0),
        SmithElementKind.Src         => new(SymbolKind.Src,       0),
        SmithElementKind.Slc         => new(SymbolKind.Slc,       0),
        SmithElementKind.Prl         => new(SymbolKind.Prl,       0),
        SmithElementKind.Prc         => new(SymbolKind.Prc,       0),
        SmithElementKind.Plc         => new(SymbolKind.Plc,       0),
        SmithElementKind.Z1P         => new(SymbolKind.ZPort,     1),
        SmithElementKind.S1P         => new(SymbolKind.Snp,       1),
        SmithElementKind.S2P         => new(SymbolKind.Snp,       2),
        SmithElementKind.Tline       => new(SymbolKind.Tline,     0),
        SmithElementKind.StubOpen    => new(SymbolKind.Tline,     0),
        SmithElementKind.StubShorted => new(SymbolKind.Tline,     0),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a Smith element kind."),
    };

    /// <summary>
    /// The only placement this kind may take, or null when either is legal.
    ///
    /// <para><b>S2P is series only</b> (R-smith1-2): a 2-port with its second port grounded is a
    /// different component than the one the user placed, and a shunt one-port is what S1P and Z1P
    /// are for. The stubs are the shunt spelling of a line and <see cref="SmithElementKind.Tline"/>
    /// is the series one, which is the same rule seen from the other end — the three of them share
    /// one <c>SymbolKind</c> and are told apart by exactly this.</para>
    /// </summary>
    public static SmithPlacement? AllowedPlacement(SmithElementKind kind) => kind switch
    {
        SmithElementKind.S2P         => SmithPlacement.Series,
        SmithElementKind.Tline       => SmithPlacement.Series,
        SmithElementKind.StubOpen    => SmithPlacement.Shunt,
        SmithElementKind.StubShorted => SmithPlacement.Shunt,
        _                            => null,
    };

    /// <summary>
    /// The parameters this kind exposes — one slider each, in the order the window shows them.
    ///
    /// <para><b>A TLIN's reference frequency is deliberately absent.</b> It is edited by
    /// double-clicking its label on the network pane (<c>SmithChartViewModel.InlineEdit.cs</c>), never
    /// a slider and never a gripper's parameter: a line whose F_ref moved under a drag would be a
    /// different physical line at every sample (§3.3).</para>
    /// </summary>
    public static IReadOnlyList<SmithParameter> Parameters(SmithElementKind kind)
    {
        // The eight RLC-family kinds answer from their ELEMENT SET, not one arm each — four rows
        // shared by eight kinds, so there is nothing per-kind to forget and no way for a member's
        // parameters and its immittance to come to disagree.
        if (RlcElementsOf(kind) is { } rlc) return _rlcParameters[rlc];

        return kind switch
        {
            SmithElementKind.R    => [SmithParameter.R],
            SmithElementKind.L    => [SmithParameter.L],
            SmithElementKind.C    => [SmithParameter.C],
            SmithElementKind.Z1P  => [SmithParameter.ImpedanceReal, SmithParameter.ImpedanceImag],
            SmithElementKind.S1P  => [],
            SmithElementKind.S2P  => [],
            SmithElementKind.Tline or SmithElementKind.StubOpen or SmithElementKind.StubShorted
                                  => [SmithParameter.Z0, SmithParameter.ElectricalLength],
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a Smith element kind."),
        };
    }

    /// <summary>
    /// Which of R, L and C an RLC-family kind carries, in the ENGINE's own vocabulary — or null
    /// for a kind that is not one of the eight.
    /// </summary>
    /// <remarks>
    /// <b>It is <see cref="RlcElements"/> and not a set of this file's own</b>, because the eight
    /// Smith kinds and the eight engine components are the same eight parts: <c>SmithElementKind.Prl</c>
    /// binds <c>SymbolKind.Prl</c> binds engine <c>PRL</c>, whose model IS
    /// <c>ParallelRlcBranchModel(RlcElements.Rl)</c>. A second enum here would be a second place for
    /// "a PRL has no capacitor" to be written down, and the symptom of that drift is a trajectory
    /// that looks plausible.
    ///
    /// <para><see cref="SmithCascade"/>'s immittance, <see cref="SmithInverse"/>'s projection and
    /// <see cref="Parameters"/> all read this, which is why the eight kinds need one formula between
    /// them rather than eight.</para>
    /// </remarks>
    public static RlcElements? RlcElementsOf(SmithElementKind kind) => kind switch
    {
        SmithElementKind.Srlc or SmithElementKind.Prlc => RlcElements.Rlc,
        SmithElementKind.Srl  or SmithElementKind.Prl  => RlcElements.Rl,
        SmithElementKind.Src  or SmithElementKind.Prc  => RlcElements.Rc,
        SmithElementKind.Slc  or SmithElementKind.Plc  => RlcElements.Lc,
        _                                              => null,
    };

    /// <summary>True for the four kinds backed by a SERIES RLC branch — their immittance is an
    /// IMPEDANCE and their parameters are linear in it.</summary>
    public static bool IsSeriesRlc(SmithElementKind kind)
        => kind is SmithElementKind.Srlc or SmithElementKind.Srl
                or SmithElementKind.Src  or SmithElementKind.Slc;

    /// <summary>True for the four backed by a PARALLEL one — an ADMITTANCE, and the duals.</summary>
    public static bool IsParallelRlc(SmithElementKind kind)
        => kind is SmithElementKind.Prlc or SmithElementKind.Prl
                or SmithElementKind.Prc  or SmithElementKind.Plc;

    /// <summary>Sliders per element set, in R-L-C order. Four rows for eight kinds.</summary>
    private static readonly Dictionary<RlcElements, IReadOnlyList<SmithParameter>> _rlcParameters =
        new()
        {
            [RlcElements.Rlc] = [SmithParameter.R, SmithParameter.L, SmithParameter.C],
            [RlcElements.Rl]  = [SmithParameter.R, SmithParameter.L],
            [RlcElements.Rc]  = [SmithParameter.R, SmithParameter.C],
            [RlcElements.Lc]  = [SmithParameter.L, SmithParameter.C],
        };

    /// <summary>
    /// The parameter a new element's gripper drags, from §3.3's own column.
    ///
    /// <para>An SRLC's is L and a PRLC's is C rather than either one's R, because the reactive part
    /// is what moves the point around the chart — dragging the loss of a lossy part is a move along
    /// the trajectory nobody reaches for first.</para>
    /// </summary>
    public static SmithParameter DefaultParameter(SmithElementKind kind)
    {
        // The family's rule, applied rather than tabulated eight times: a SERIES member drags its
        // L and a PARALLEL member its C — and whichever of the two it actually HAS, which is the
        // only thing the two-element members add. An SRC has no inductor to drag and a PRL has no
        // capacitor, so each falls to its own other reactance; neither ever falls to R.
        if (RlcElementsOf(kind) is { } rlc)
        {
            bool hasL = rlc.HasFlag(RlcElements.L), hasC = rlc.HasFlag(RlcElements.C);
            return IsSeriesRlc(kind)
                       ? (hasL ? SmithParameter.L : SmithParameter.C)
                       : (hasC ? SmithParameter.C : SmithParameter.L);
        }

        return kind switch
        {
            SmithElementKind.R    => SmithParameter.R,
            SmithElementKind.L    => SmithParameter.L,
            SmithElementKind.C    => SmithParameter.C,
            SmithElementKind.Z1P  => SmithParameter.ImpedanceImag,
            SmithElementKind.S1P  => SmithParameter.None,
            SmithElementKind.S2P  => SmithParameter.None,
            SmithElementKind.Tline or SmithElementKind.StubOpen or SmithElementKind.StubShorted
                                  => SmithParameter.ElectricalLength,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a Smith element kind."),
        };
    }

    /// <summary>
    /// The parameter a gripper on <paramref name="element"/> drags.
    /// </summary>
    /// <remarks>
    /// The element's own <see cref="SmithElement.ActiveParameter"/> — the one whose slider was last
    /// touched — falling back to <see cref="DefaultParameter"/> for the kind. <b>The fallback is
    /// this table's own answer and not a second one</b>: brief 6's sliders are what set the field,
    /// and until an element has been selected once it carries <see cref="SmithParameter.None"/>,
    /// which is not "this element has no parameter" but "nobody has chosen yet".
    ///
    /// <para><b>It is here rather than on the view model</b> (<c>R-smith10-1</c>): the gripper RING
    /// is drawn wherever the chart is drawn, including headlessly, and a node with no parameter
    /// carries none. A rule that lived only in the window would put a ring on a file element in
    /// every exported picture and on none of the ones anybody looked at.</para>
    /// </remarks>
    public static SmithParameter ActiveParameterOf(SmithElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.ActiveParameter != SmithParameter.None
                   ? element.ActiveParameter
                   : DefaultParameter(element.Kind);
    }

    /// <summary>True for the two kinds whose value is a Touchstone file, and only those — the rule
    /// <c>SmithElement.Refusal</c> reads in both directions.</summary>
    public static bool UsesFile(SmithElementKind kind)
        => kind is SmithElementKind.S1P or SmithElementKind.S2P;

    /// <summary>True for the three kinds backed by <c>TLIN</c>, which are the ones that carry a Z₀,
    /// an electrical length and a reference frequency.</summary>
    public static bool IsLine(SmithElementKind kind)
        => kind is SmithElementKind.Tline or SmithElementKind.StubOpen or SmithElementKind.StubShorted;

    /// <summary>Every kind, in the order the Insert menu and the Add menu list them. One list, so
    /// the two menus cannot disagree.</summary>
    public static readonly IReadOnlyList<SmithElementKind> AllKinds =
    [
        SmithElementKind.R,
        SmithElementKind.L,
        SmithElementKind.C,
        SmithElementKind.Srlc,
        SmithElementKind.Prlc,
        SmithElementKind.Srl,
        SmithElementKind.Src,
        SmithElementKind.Slc,
        SmithElementKind.Prl,
        SmithElementKind.Prc,
        SmithElementKind.Plc,
        SmithElementKind.Z1P,
        SmithElementKind.S1P,
        SmithElementKind.S2P,
        SmithElementKind.Tline,
        SmithElementKind.StubOpen,
        SmithElementKind.StubShorted,
    ];
}
