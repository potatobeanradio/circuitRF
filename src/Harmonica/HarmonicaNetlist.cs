using System.Globalization;
using System.Linq;
using System.Text;
using CircuitRF.Core.Devices;

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

        // R7B §3.5 — an SDD's Parameters carry both its equations and its scope variables, keyed
        // identically (name → expression text). Equations stay on the instance line as before;
        // variables become top-level GLOBAL lines instead, because CnlReader's instance-line parser
        // splits on whitespace (an expression with a space in it would be silently truncated into
        // phantom net names) and because a variable that references another variable needs the
        // enclosing scope Elaborator.InjectSddScopeVars already resolves through — an instance-line
        // parameter is evaluated in the PARENT scope, where a sibling variable does not exist.
        IReadOnlyDictionary<string, string> instanceParams = model.Dut.Parameters;
        if (model.Dut.Kind == DutKind.Sdd)
        {
            var (equations, globals) = SplitSddParameters(model.Dut.Parameters);
            foreach (var (name, expr) in globals)
                sb.AppendLine($"{name} = {expr}");
            if (globals.Count > 0) sb.AppendLine();

            // §3.7 — whitespace is insignificant in an equation and the instance-line parser reads a
            // space as a net separator, so it is stripped here, on emission. Global lines keep theirs.
            instanceParams = equations.ToDictionary(e => e.Name, e => StripWhitespace(e.Expression),
                                                     StringComparer.Ordinal);
        }

        sb.AppendLine(DutLine(model.Dut, gate, drain, sourceTerminal, instanceParams));

        // R7D §1 — the DUT's own Cgs/Cdg/Cds, SDD only: a native FET already carries gate charge via
        // its own CapModel and an external model carries its own parasitics, so emitting these too
        // would double-count. In parallel with the SDD's ports, so the SDD becomes the bare current
        // generator and the capacitors sit between it and the package plane — which is what makes
        // Z_intr/Γ_intr (read at the SDD's own ports) genuinely differ from the terminal impedance.
        if (model.Dut.Kind == DutKind.Sdd)
        {
            var caps = model.Dut.Capacitances;
            if (!caps.IsIdentity)
            {
                sb.AppendLine();

                // R8C §3.2 — rgs in SERIES with Cgs. Zero emits nothing and Cgs keeps its direct gate
                // connection, so a document that never set one is byte-identical (the same rule
                // Shunt() already follows). The intrinsic plane does not move: IntrinsicPortMap locates
                // the SDD's OWN ports, and n_rgs is an internal node of a branch that sits in PARALLEL
                // with the SDD gate port, not between the SDD and the gate.
                if (!caps.Cgs.IsAbsent && caps.RgsOhms != 0.0)
                {
                    sb.AppendLine($"R:RGS  {gate} n_rgs  R={Num(caps.RgsOhms)}");
                    AppendCapacitance(sb, "CGS", "n_rgs", sourceTerminal, caps.Cgs);
                }
                else
                {
                    AppendCapacitance(sb, "CGS", gate, sourceTerminal, caps.Cgs);
                }

                // R7D §1 — CDG's net order is LOAD-BEARING: {drain} {gate}, matching §3.3's own
                // linearization reference (V_Cdg = V_drain − V_gate). NonlinearCModel is a polynomial
                // in its own terminal voltage V(n+) − V(n−); the other order flips every odd
                // coefficient's sign.
                AppendCapacitance(sb, "CDG", drain, gate, caps.Cdg);
                AppendCapacitance(sb, "CDS", drain, sourceTerminal, caps.Cds);
            }
        }

        if (terminationText is not null)
        {
            sb.AppendLine();
            sb.AppendLine(terminationText);
        }

        return new HarmonicaNetlist(sb.ToString(), model);
    }

    /// <summary>
    /// The DUT instance line. Exactly one DUT, source always grounded (§4.3).
    /// <paramref name="instanceParams"/> is what actually lands on the line — the DUT's full
    /// <c>Parameters</c> map for every kind except SDD, whose scope variables have already been
    /// split out to global lines by the caller (R7B §3.5).
    /// </summary>
    private static string DutLine(DutSpec dut, string gate, string drain, string source,
                                  IReadOnlyDictionary<string, string> instanceParams)
    {
        // The multiplier goes BEFORE the model's own parameters, not after. An SDD line's equations
        // are delimited by the next `I[p,w]=`-style header at bracket depth zero, so a trailing
        // `m=2` is swallowed into the last equation's text and fails to parse — found by the test,
        // not by review.
        string head = dut.Multiplicity != 1.0 ? $"  m={Num(dut.Multiplicity)}" : "";
        string tail = head + string.Concat(instanceParams.Select(p => $"  {p.Key}={p.Value}"));

        return dut.Kind switch
        {
            // Three nets in schematic pin order; the elaborator expands them into the two
            // (gate,source) / (drain,source) port pairs every published FET equation is written in.
            DutKind.NativeFet => $"{dut.TypeName}:{Dut}  {gate} {drain} {source}{tail}",

            // 2N nets as ± pairs, so _v1 is Vgs and _v2 is Vds even with a lifted source.
            // R-h9c-11 — SDD3 adds a THIRD port pair, the source terminal against ground, so an
            // equation can reference _v3/I[3,w] directly. The gate/drain ports are unchanged either
            // way, which is what keeps IntrinsicPortMap.TwoPort correct for both.
            DutKind.Sdd => dut.SddPortCount >= 3
                ? $"SDD:{Dut}  {gate} {source}  {drain} {source}  {source} 0{tail}"
                : $"SDD:{Dut}  {gate} {source}  {drain} {source}{tail}",

            // Every node its own ground-referenced port — the external-device convention.
            DutKind.External =>
                $"ExtDevice:{Dut}  {gate} {drain} {source}  Provider={dut.Provider} Type={dut.TypeName}{tail}",

            DutKind.Diode => $"Diode:{Dut}  {gate} {drain}{tail}",

            _ => throw new NotSupportedException($"DUT kind {dut.Kind}"),
        };
    }

    /// <summary>
    /// R7B §3.5 — splits an SDD's <c>Parameters</c> into equations (stay on the instance line) and
    /// scope variables (become global lines), by the SAME name shapes
    /// <c>ComponentModelFactory.IsSddEquationName</c> classifies rather than a second, re-spelled set.
    /// </summary>
    private static (List<(string Name, string Expression)> Equations,
                    List<(string Name, string Expression)> Globals)
        SplitSddParameters(IReadOnlyDictionary<string, string> parameters)
    {
        var equations = new List<(string, string)>();
        var globals   = new List<(string, string)>();
        foreach (var (name, expr) in parameters)
            (ComponentModelFactory.IsSddEquationName(name) ? equations : globals).Add((name, expr));
        return (equations, globals);
    }

    /// <summary>R7B §3.7 — whitespace is insignificant in an SDD equation (two adjacent identifiers
    /// are never legal there) and the instance-line parser reads a space as a net separator, so every
    /// character of it is removed before the equation is written onto the instance line.</summary>
    private static string StripWhitespace(string s)
    {
        Span<char> buf = s.Length <= 256 ? stackalloc char[s.Length] : new char[s.Length];
        int n = 0;
        foreach (char c in s)
            if (!char.IsWhiteSpace(c)) buf[n++] = c;
        return new string(buf[..n]);
    }

    /// <summary>
    /// R7D §2.4 — one of the DUT's own capacitors, absent (nothing emitted — a <c>C=0</c> element is
    /// another node equation and another stamp on every solve of a tool whose whole claim is frame
    /// rate), linear (<c>C:</c>), or nonlinear (<c>NonlinearC:</c>, coefficients dense from index 0 —
    /// <c>ComponentModelFactory</c> reads <c>C0, C1, …</c> consecutively and stops at the first absent
    /// one, so trailing zeros may be omitted but nothing here needs to).
    /// </summary>
    private static void AppendCapacitance(StringBuilder sb, string name, string plus, string minus,
                                          DutCapacitance c)
    {
        if (c.IsAbsent) return;

        if (c.IsNonlinear)
        {
            string coeffs = string.Concat(c.Coefficients!.Select((v, k) => $"  C{k}={Num(v)}"));
            sb.AppendLine($"NonlinearC:{name}  {plus} {minus}{coeffs}");
        }
        else
        {
            sb.AppendLine($"C:{name}  {plus} {minus}  C={Num(c.Farads)}");
        }
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
