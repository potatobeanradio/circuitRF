using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.WBond;

namespace CircuitRF.Core.Devices;

/// <summary>
/// A wirebond component: M coupled arrays of 3D bond wires, stamped as M coupled series branches
/// (<c>docs/design/wbond.md</c> §5, brief-wbond-wbb R-wbb-1/2/4).
///
/// <h3>What it stamps</h3>
/// <para>One branch-current unknown per array, running from that array's input pin to its output pin,
/// with the full mutual coupling between arrays:</para>
/// <code>
/// V_{in_k} − V_{out_k} − Σ_j Z_arr[k,j]·I_j = 0
/// </code>
/// <para>This is <see cref="SnpModel"/>'s branch-current expansion with a <b>series</b> rather than a
/// shunt topology — the same mechanism the linear engine already uses, so nothing in the
/// linear/nonlinear partition changes.</para>
///
/// <h3>Z_arr is the exact complex reduction</h3>
/// <para><c>Z_arr(ω) = (AᵀZ(ω)⁻¹A)⁻¹</c> with <c>Z = R(ω) + jω(L + L_int(ω))</c> — owner decision
/// 2026-08-07, <b>not</b> R and L reduced independently. Both cost one factorisation per frequency,
/// but reducing them separately does so on inconsistent current distributions.</para>
///
/// <h3>The REF pin declares; it does not stamp</h3>
/// <para><c>L_arr</c> is a <i>loop</i> inductance whose return is the image plane at z = 0, so the
/// circuit element is a plain series branch and the return is implicit in the schematic's own ground.
/// A reader will expect a 2M+1-terminal stamp and there is not one. <c>REF</c> exists so that
/// assumption is <b>stated</b>, and so the model can refuse the configuration in which it is false —
/// see <see cref="Stamp"/>'s refusal. That is RW13's "a port carries an explicit reference conductor"
/// applied to the array basis.</para>
/// </summary>
public sealed class WBondModel : ComponentModel
{
    private readonly WBondDesign _design;
    private readonly string _sourceDescription;
    private ImpedanceReduction? _reduction;

    /// <summary>Branch index per array, set during each <see cref="Stamp"/> call. −1 before the first.</summary>
    public int[] ArrayBranchIndices { get; }

    /// <param name="referencePin">
    /// Whether the component exposes the floating <c>REF</c> terminal.
    ///
    /// <para><b>Off by default</b> (owner, 2026-08-16), matching SnP's own <c>RefNode</c> — which is
    /// what this is modelled on, and off there too. §5.4/WB20 wrote the pin as mandatory; what WB20 is
    /// actually protecting is the REFUSAL in <see cref="RefuseIfReturnPathUndeclared"/>, and that
    /// keys off <c>GroundPlane.Enabled</c>, not off the pin. So an undeclared return path is still
    /// refused by name whether or not the pin is there, and nothing about the physics changes with
    /// this flag: <c>REF</c> never stamped. It is a place to SAY which net is the reference plane,
    /// for the designs where that is not simply ground.</para>
    /// </param>
    public WBondModel(WBondDesign design, string sourceDescription = "<inline>",
                      bool referencePin = false)
    {
        ArgumentNullException.ThrowIfNull(design);
        design.Validate();

        // A DESIGN may hold no wires (a document not drawn in yet, or one just cleared); a placed
        // COMPONENT may not. Its pins are its array names, so a design with no arrays gives a part
        // with nothing to connect and no branch to stamp — refused here, where the word "component"
        // is available to say it with, rather than left to surface as a zero-port netlist.
        if (design.Arrays.Count == 0)
            throw new InvalidOperationException(
                $"The wBond design '{sourceDescription}' has no wires, so the component has no pins. " +
                "Add at least one wire to the design before placing it.");

        _design = design;
        _sourceDescription = sourceDescription;
        HasReferencePin = referencePin;

        ArrayBranchIndices = new int[design.Arrays.Count];
        for (int k = 0; k < ArrayBranchIndices.Length; k++) ArrayBranchIndices[k] = -1;
    }

    /// <summary>The design this component models. Exposed for the coupling audit and for measurements.</summary>
    public WBondDesign Design => _design;

    /// <summary>Where the design came from — a file path, or <c>&lt;inline&gt;</c>.</summary>
    public string SourceDescription => _sourceDescription;

    /// <summary>The number of wire arrays, and so the number of coupled branches.</summary>
    public int ArrayCount => _design.Arrays.Count;

    /// <summary>
    /// Whether the floating <c>REF</c> terminal is exposed. See the constructor's own parameter note —
    /// it is a declaration, never a stamped connection, and the return-path refusal does not depend
    /// on it.
    /// </summary>
    public bool HasReferencePin { get; }

    /// <summary>2M signal pins, plus one <c>REF</c> when <see cref="HasReferencePin"/>.</summary>
    public override int PortCount => 2 * ArrayCount + (HasReferencePin ? 1 : 0);

    public override ModelKind Kind => ModelKind.Linear;

    /// <summary>
    /// <c>G1.i, G1.o, G2.i, G2.o, …</c> — input left, output right, in array order (D3) — followed by
    /// <c>REF</c> when the reference pin is exposed.
    ///
    /// <para><b>The signal terminals are unchanged either way</b>, and that is what makes the pin
    /// optional at all: <c>REF</c> is the LAST terminal, so removing it renumbers nothing. The
    /// schematic symbol generator relies on exactly this — it appends its own <c>REF</c> pin last, and
    /// the two lists have a test asserting they agree.</para>
    /// </summary>
    public override string[] TerminalNames
    {
        get
        {
            var names = new string[PortCount];
            for (int k = 0; k < ArrayCount; k++)
            {
                names[2 * k] = $"{_design.Arrays[k].Name}.i";
                names[2 * k + 1] = $"{_design.Arrays[k].Name}.o";
            }
            if (HasReferencePin) names[^1] = "REF";
            return names;
        }
    }

    /// <summary>
    /// The reduction, built lazily so <b>L</b> is filled once and reused across every frequency of a
    /// sweep (R-wbb-3). Refilling per point would cost ~0.16 s × the sweep length — measured at 32.9 s
    /// for a 201-point sweep at 600 wires, against 11.2 s for the whole sweep done properly.
    /// </summary>
    private ImpedanceReduction Reduction => _reduction ??= ImpedanceReduction.Create(_design);

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        ArgumentNullException.ThrowIfNull(mna);
        ArgumentNullException.ThrowIfNull(c);

        RefuseIfReturnPathUndeclared(c);

        int m = ArrayCount;
        double hz = omega / (2.0 * Math.PI);
        var zArr = Reduction.ArrayImpedance(hz);

        var branches = new int[m];
        for (int k = 0; k < m; k++)
        {
            branches[k] = mna.AddBranch();
            ArrayBranchIndices[k] = branches[k];
        }

        for (int k = 0; k < m; k++)
        {
            int inNode = c.Nodes[2 * k];
            int outNode = c.Nodes[2 * k + 1];

            // KCL: the array's current enters at its input pin and leaves at its output pin.
            mna.AddBranchCurrent(branches[k], inNode, outNode);

            // Constraint row k: V_in(+1) + V_out(-1) + sum_j I_j(-Z_arr[k,j]) = 0.
            mna.AddConstraint(branches[k], inNode, Complex.One);
            mna.AddConstraint(branches[k], outNode, -Complex.One);

            for (int j = 0; j < m; j++)
                mna.AddBranchConstraint(branches[k], branches[j], -zArr[k * m + j]);
        }
    }

    /// <summary>
    /// R-wbb-4 / WB20 — the model refuses rather than reporting an optimistically low inductance
    /// against a return path that does not exist.
    ///
    /// <para>Modelling only the signal wires while assuming a perfect plane return is the single most
    /// common way a bondwire model goes wrong, and it is wrong in the <b>optimistic</b> direction,
    /// which is the worst kind. So the two legitimate configurations are enumerated and anything else
    /// is refused by name.</para>
    /// </summary>
    private void RefuseIfReturnPathUndeclared(ElaboratedComponent c)
    {
        if (_design.GroundPlane.Enabled) return;

        // With the plane disabled the return must come from wires the user nominated as the
        // reference — downbonds (RW14). Until WB-C offers that nomination there is nothing to
        // nominate, so this is unconditionally a refusal, and it says so.
        throw new InvalidOperationException(
            $"wBond '{c.InstancePath}' ({_sourceDescription}) has its ground plane disabled and no array " +
            "nominated as the return conductor, so its inductance has no defined return path and would " +
            "be reported optimistically low.\n" +
            "Either re-enable the ground plane (the image plane at z = 0 then IS the return), or add " +
            "ground bond wires and nominate their array as the reference — they are ordinary wires in " +
            "the model and get their own inductance and coupling.");
    }

    /// <summary>
    /// The array-basis impedance at one frequency, exposed for measurements and tests without going
    /// through a solve.
    /// </summary>
    public Complex[] ArrayImpedance(double frequencyHz) => Reduction.ArrayImpedance(frequencyHz);

    /// <summary>The frequency-independent array-basis inductance — the editor's readout (WB19b).</summary>
    public ArrayReduction InductanceOnly() => Reduction.InductanceOnlyReduction();
}
