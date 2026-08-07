using System.Globalization;
using System.Text;

namespace CircuitRF.Harmonica;

/// <summary>
/// Turns a <see cref="CircuitModel"/> into the netlist harmonicaRF solves (harmonicarf.md §4.1).
///
/// <para><b>The RF terminations are deliberately NOT in it.</b> The netlist is the OPEN-port form:
/// the DUT, the embedding stack and the ideal bias, with the two termination planes left as ordinary
/// nodes that nothing else attaches to. The terminations are closed algebraically, per harmonic, by
/// <see cref="InterfaceNetwork"/> (§6.2, R-hrf-6) — which is what makes a marker move cost no MNA
/// solve and no refactorisation. A second netlist WITH the terminations stamped exists only as the
/// oracle Tier 2 compares against, and it is built by the test rather than by the product.</para>
///
/// <para><b>Why the output is <c>.cnl</c> text.</b> Every device kind harmonicaRF can hold — the five
/// native FETs, an SDD carrying user equations, a compiled external model, a Touchstone block — has a
/// well-exercised <c>.cnl</c> path already, including the SDD line parser's depth-aware equation
/// boundaries. Building design-layer objects by hand would be a second, thinner copy of that. It also
/// makes §7.8's <i>Export testbench</i> literally this string plus an analysis line.</para>
///
/// <para><b>The one trap it must not fall into:</b> circuitRF's generic instance-line parser splits
/// on whitespace and reads bare words as nets, so an unquoted parameter value MUST contain no spaces
/// (see <c>src/Core/CLAUDE.md</c>). Every number here is written with <see cref="Num"/>, which emits
/// round-trip form in the invariant culture and never a space.</para>
/// </summary>
public sealed class HarmonicaNetlist
{
    /// <summary>The source termination plane — where the source marker's impedance attaches.</summary>
    public const string SourcePlane = "n_srcterm";

    /// <summary>The load termination plane — where the load marker's impedance attaches.</summary>
    public const string LoadPlane = "n_ldterm";

    /// <summary>The DUT's own gate, drain and source terminals (the PACKAGE plane).</summary>
    public const string GateTerminal   = "n_g";
    public const string DrainTerminal  = "n_d";
    public const string SourceTerminal = "n_s";

    public const string GateBiasNode  = "n_gbias";
    public const string DrainBiasNode = "n_dbias";

    /// <summary>Instance name of the gate bias supply — the handle bias mutation reaches for.</summary>
    public const string GateSupply  = "VGG";
    public const string DrainSupply = "VDD";

    /// <summary>Instance name of the DUT. There is exactly one.</summary>
    public const string Dut = "DUT";

    /// <summary>
    /// The ideal bias choke and DC block (§4.4). One henry is open at RF and a short at DC; one
    /// farad is the dual. Both are the values <c>TunerModel</c> already uses for the same job, so
    /// harmonicaRF's bias tee and the loadpull engine's are the same circuit.
    /// </summary>
    public const double IdealChokeH = 1.0;
    public const double IdealBlockF = 1.0;

    private HarmonicaNetlist(string text, CircuitModel model)
    {
        Text  = text;
        Model = model;
    }

    public string       Text  { get; }
    public CircuitModel Model { get; }

    public static string Num(double v) => v.ToString("G17", CultureInfo.InvariantCulture);

    /// <summary>
    /// Builds the open-port netlist. <paramref name="terminationText"/> is appended verbatim and is
    /// how a caller stamps the terminations for a comparison run — the product never passes one.
    /// </summary>
    public static HarmonicaNetlist Build(CircuitModel model, string? terminationText = null)
    {
        var sb = new StringBuilder();

        sb.AppendLine("; harmonicaRF — generated, open-port form. Terminations are closed algebraically.");
        sb.AppendLine();

        // ── ideal bias (§4.4): a perfect choke to each termination plane ──────
        sb.AppendLine($"Vdc:{GateSupply}  {GateBiasNode} 0  Vdc={Num(model.Bias.Vgs ?? 0.0)}");
        sb.AppendLine($"L:LCHG  {GateBiasNode} {SourcePlane}  L={Num(model.Settings.BiasChokeHenries)}");
        sb.AppendLine($"Vdc:{DrainSupply}  {DrainBiasNode} 0  Vdc={Num(model.Bias.Vds)}");
        sb.AppendLine($"L:LCHD  {DrainBiasNode} {LoadPlane}  L={Num(model.Settings.BiasChokeHenries)}");
        sb.AppendLine();

        // ── embedding, outside in: s2p → s4p → lumped → DUT (§4.1) ────────────
        var e = model.Embedding;
        string inNode  = SourcePlane;
        string outNode = LoadPlane;

        if (e.S2pInFile is not null)
        {
            sb.AppendLine($"SnP:S2PIN  {inNode} n_s2in  NumPorts=2 File=\"{e.S2pInFile}\" Type=\"touchstone\"");
            inNode = "n_s2in";
        }
        if (e.S2pOutFile is not null)
        {
            sb.AppendLine($"SnP:S2POUT  {outNode} n_s2out  NumPorts=2 File=\"{e.S2pOutFile}\" Type=\"touchstone\"");
            outNode = "n_s2out";
        }
        if (e.S4pFile is not null)
        {
            // Ports 1,2 face outward (toward s2p / the tuner); 3,4 face the DUT (§4.1).
            sb.AppendLine($"SnP:S4P  {inNode} {outNode} n_s4g n_s4d  NumPorts=4 File=\"{e.S4pFile}\" Type=\"touchstone\"");
            inNode  = "n_s4g";
            outNode = "n_s4d";
        }
        sb.AppendLine();

        // ── the lumped package (§4.1) ────────────────────────────────────────
        var pkg = e.Package;
        inNode  = Shunt(sb, "CPG", inNode,  pkg.Cpg);
        outNode = Shunt(sb, "CPD", outNode, pkg.Cpd);

        // Series() returns its FAR end when it emitted anything and its near end when it did not, so
        // a package that states no gate lead leaves the tuner plane and the gate terminal as one
        // node — exactly the "no extrinsic network" case R-hrf-1 is worded against.
        string gate  = Series(sb, "RG", "LG", inNode,  GateTerminal,  pkg.Rg, pkg.Lg);
        string drain = Series(sb, "RD", "LD", outNode, DrainTerminal, pkg.Rd, pkg.Ld);

        // The source lead is what makes Z_S,intr depend on gm (§4.5.3(a)): grounding the source at
        // the PACKAGE plane leaves Rs/Ls carrying the DRAIN current as well as the gate's. With no
        // lead the source terminal IS ground — not a node of its own, which would float.
        string sourceTerminal = "0";
        if (pkg.Rs != 0 || pkg.Ls != 0)
        {
            Series(sb, "RS", "LS", SourceTerminal, "0", pkg.Rs, pkg.Ls);
            sourceTerminal = SourceTerminal;
        }

        if (pkg.CgdExt != 0)
            sb.AppendLine($"C:CGDX  {gate} {drain}  C={Num(pkg.CgdExt)}");

        sb.AppendLine();
        sb.AppendLine(DutLine(model.Dut, gate, drain, sourceTerminal));

        if (terminationText is not null)
        {
            sb.AppendLine();
            sb.AppendLine(terminationText);
        }

        return new HarmonicaNetlist(sb.ToString(), model);
    }

    /// <summary>The DUT instance line. Exactly one DUT, source always grounded (§4.3).</summary>
    private static string DutLine(DutSpec dut, string gate, string drain, string source)
    {
        // The multiplier goes BEFORE the model's own parameters, not after. An SDD line's equations
        // are delimited by the next `I[p,w]=`-style header at bracket depth zero, so a trailing
        // `m=2` is swallowed into the last equation's text and fails to parse — found by the test,
        // not by review.
        string head = dut.Multiplicity != 1.0 ? $"  m={Num(dut.Multiplicity)}" : "";
        string tail = head + string.Concat(dut.Parameters.Select(p => $"  {p.Key}={p.Value}"));

        return dut.Kind switch
        {
            // Three nets in schematic pin order; the elaborator expands them into the two
            // (gate,source) / (drain,source) port pairs every published FET equation is written in.
            DutKind.NativeFet => $"{dut.TypeName}:{Dut}  {gate} {drain} {source}{tail}",

            // 2N nets as ± pairs, so _v1 is Vgs and _v2 is Vds even with a lifted source.
            DutKind.Sdd => $"SDD:{Dut}  {gate} {source}  {drain} {source}{tail}",

            // Every node its own ground-referenced port — the external-device convention.
            DutKind.External =>
                $"ExtDevice:{Dut}  {gate} {drain} {source}  Provider={dut.Provider} Type={dut.TypeName}{tail}",

            DutKind.Diode => $"Diode:{Dut}  {gate} {drain}{tail}",

            _ => throw new NotSupportedException($"DUT kind {dut.Kind}"),
        };
    }

    /// <summary>A shunt capacitance to ground. Zero emits nothing and returns the node unchanged.</summary>
    private static string Shunt(StringBuilder sb, string name, string node, double c)
    {
        if (c != 0) sb.AppendLine($"C:{name}  {node} 0  C={Num(c)}");
        return node;
    }

    /// <summary>
    /// A series R then L between two named nodes, minting an intermediate only when both are
    /// present. With both zero nothing is emitted and <paramref name="from"/> is returned — so a
    /// package that states no gate lead leaves the tuner plane and the gate terminal as ONE node,
    /// which is exactly the "no extrinsic network" case R-hrf-1 is worded against.
    /// </summary>
    private static string Series(StringBuilder sb, string rName, string lName,
                                 string from, string to, double r, double l)
    {
        if (r == 0 && l == 0) return from;

        if (r != 0 && l != 0)
        {
            string mid = $"n_{rName.ToLowerInvariant()}_mid";
            sb.AppendLine($"R:{rName}  {from} {mid}  R={Num(r)}");
            sb.AppendLine($"L:{lName}  {mid} {to}  L={Num(l)}");
        }
        else if (r != 0)
            sb.AppendLine($"R:{rName}  {from} {to}  R={Num(r)}");
        else
            sb.AppendLine($"L:{lName}  {from} {to}  L={Num(l)}");

        return to;
    }
}
