using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CSparse.Complex.Factorization;
using NumFlat;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Engine;

/// <summary>
/// Runs an S-parameter sweep over a frequency grid (linear-engine §2, §6, §9).
///
/// Per frequency:
///   1. Stamp all components (independent sources zeroed) into MnaSystem.
///   2. Apply regularization per <see cref="AnalysisSettings"/> (gmin and/or inductance reg).
///   3. Factorize once (AMD permutation cached after first call).
///   4. For each port j: solve with 1 V drive at port j, 0 V at all others.
///      Read port branch currents → column j of the port Y-matrix.
///   5. Convert Y → S via RfCore (power-wave, per-port complex Z0).
///
/// Regularization tri-state (<see cref="RegularizationMode"/>):
///   IfNecessary: first attempt with no regularization; if singular, retry with both.
///   Always:      apply regularization before first factorization.
///   Never:       no regularization; throw <see cref="SingularMatrixException"/> on singular.
/// </summary>
public static class SParameterEngine
{
    /// <summary>gmin conductance added from every node to ground (§5).</summary>
    public const double DefaultGmin = 1e-12;

    public static DataSet Run(
        ElaboratedNetlist netlist,
        double[]          freqsHz,
        AnalysisSettings? settings = null)
    {
        settings ??= AnalysisSettings.Default;

        // ── Identify ports + build branch-label map ───────────────────────────
        int nonGroundNodes = netlist.Nodes.Count - 1;
        var (ports, branchLabels) = CollectPortsAndBranchLabels(netlist, nonGroundNodes);
        if (ports.Count == 0)
            throw new InvalidOperationException(
                "S-parameter analysis requires at least one Port or Term component.");
        int N = ports.Count;

        var mna          = new MnaSystem(nonGroundNodes);
        var freqCount    = freqsHz.Length;
        var sMatrices    = new Mat<Complex>[freqCount];
        var z0PerPort    = ports.Select(p => p.Z0).ToArray();

        // ── Build diagnostic namers (used when factorization fails) ───────────
        var nodeTouchers = new Dictionary<int, List<string>>(netlist.Nodes.Count);
        foreach (var ec in netlist.Components)
            foreach (var n in ec.Nodes)
            {
                if (n == 0) continue;
                if (!nodeTouchers.TryGetValue(n, out var lst))
                    nodeTouchers[n] = lst = [];
                lst.Add($"{ec.ComponentType}:{ec.InstancePath}");
            }

        Func<int, string> nodeNamer = matIdx =>
        {
            int nodeIdx = matIdx + 1;
            string name = nodeIdx < netlist.Nodes.Count
                ? netlist.Nodes.NameOf(nodeIdx)
                : $"node#{nodeIdx}";
            if (nodeTouchers.TryGetValue(nodeIdx, out var t) && t.Count > 0)
            {
                const int maxShow = 8;
                var shown = t.Count <= maxShow
                    ? t
                    : [.. t.Take(maxShow), $"...+{t.Count - maxShow} more"];
                name += $" (touched by: {string.Join(", ", shown)})";
            }
            return name;
        };

        Func<int, string> branchNamer = idx =>
            branchLabels.TryGetValue(idx, out var lbl) ? lbl : $"branch#{idx - nonGroundNodes}";

        // Pre-compute whether IfNecessary retry is possible.
        bool canRetry =
            settings.ConductanceRegularization == RegularizationMode.IfNecessary ||
            settings.InductanceRegularization  == RegularizationMode.IfNecessary;

        for (int fi = 0; fi < freqCount; fi++)
        {
            double hz    = freqsHz[fi];
            double omega = 2.0 * Math.PI * hz;

            // ── Stamp components + first regularization pass ──────────────────
            StampAll(mna, netlist, omega);
            ApplyRegularization(mna, netlist, nonGroundNodes, settings,
                applyIfNecessary: false); // first attempt omits IfNecessary regs

            // ── Factorize: attempt 1 ──────────────────────────────────────────
            SparseLU lu;
            try
            {
                lu = mna.Factorize(nodeNamer: nodeNamer, branchNamer: branchNamer);
            }
            catch (SingularMatrixException ex) when (canRetry)
            {
                // IfNecessary path: re-stamp and apply all non-Never regs, then retry.
                Console.Error.WriteLine(
                    $"[circuitRF] Regularization engaged at {hz:G4} Hz — matrix was singular " +
                    $"({ex.Message.Split('\n')[0].TrimEnd()}); retrying with " +
                    $"conductance={settings.ConductanceRegularization != RegularizationMode.Never} " +
                    $"inductance={settings.InductanceRegularization != RegularizationMode.Never} regularization.");

                StampAll(mna, netlist, omega);
                ApplyRegularization(mna, netlist, nonGroundNodes, settings,
                    applyIfNecessary: true);
                lu = mna.Factorize(nodeNamer: nodeNamer, branchNamer: branchNamer);
            }

            // ── Extract port Y-matrix via unit-voltage excitation ─────────────
            var yMat = new Mat<Complex>(N, N);
            var xBuf = new Complex[mna.Size];

            for (int j = 0; j < N; j++)
            {
                var b = mna.BuildRhsWithPortDrive(
                    branchRow:  ports[j].BranchIndex,
                    driveValue: Complex.One);

                lu.Solve(b, xBuf);

                // Branch current flows FROM signal TO ref (AddBranchCurrent convention).
                // Port current (INTO the + terminal) = −branch_current.
                for (int k = 0; k < N; k++)
                    yMat[k, j] = -xBuf[ports[k].BranchIndex];
            }

            // ── Y → S via RfCore (power-wave, per-port complex Z0) ────────────
            sMatrices[fi] = RFNetwork.YToS(yMat, z0PerPort);
        }

        var refZ0 = z0PerPort.Length > 0 ? z0PerPort[0] : new Complex(50, 0);
        var snp   = new SNP(freqsHz, sMatrices, MatrixType.S, MatrixFormat.RI, refZ0);
        return DataSetBuilder.FromSnp(snp);
    }

    // ── Assembly helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Stamp all components (two-phase: non-mutual first so InductorModel.LastBranchIndex
    /// is set before MutualInductanceModel reads it).
    /// </summary>
    private static void StampAll(MnaSystem mna, ElaboratedNetlist netlist, double omega)
    {
        mna.Reset();
        foreach (var ec in netlist.Components)
            if (ec.Model is not MutualInductanceModel)
                ec.Model.Stamp(mna, ec, omega);
        foreach (var ec in netlist.Components)
            if (ec.Model is MutualInductanceModel)
                ec.Model.Stamp(mna, ec, omega);
    }

    /// <summary>
    /// Apply conductance and/or inductance regularization to the assembled MNA.
    /// <paramref name="applyIfNecessary"/>: if true, treat IfNecessary as Active (retry pass);
    /// if false, only apply Always regs (first-attempt pass).
    /// Never-mode regs are never applied.
    /// </summary>
    private static void ApplyRegularization(
        MnaSystem         mna,
        ElaboratedNetlist netlist,
        int               nonGroundNodes,
        AnalysisSettings  settings,
        bool              applyIfNecessary)
    {
        bool applyCond = settings.ConductanceRegularization == RegularizationMode.Always
            || (applyIfNecessary && settings.ConductanceRegularization == RegularizationMode.IfNecessary);

        bool applyInd  = settings.InductanceRegularization == RegularizationMode.Always
            || (applyIfNecessary && settings.InductanceRegularization == RegularizationMode.IfNecessary);

        if (applyCond)
        {
            // Add gmin conductance from every non-ground node to ground (linear-engine §5).
            var g = new Complex(settings.Gmin, 0.0);
            for (int n = 1; n <= nonGroundNodes; n++)
                mna.AddAdmittance(n, 0, g);
        }

        if (applyInd)
        {
            // Add small series resistance to every inductor branch diagonal.
            // Cures rank-deficient coupled-inductance D-block (zero eigenvalue of inductance matrix).
            var rReg = new Complex(-settings.InductanceRegR, 0.0);
            foreach (var ec in netlist.Components)
                if (ec.Model is InductorModel im && im.LastBranchIndex >= 0)
                    mna.AddBranchConstraint(im.LastBranchIndex, im.LastBranchIndex, rReg);
        }
    }

    // ── Port collection + branch label map ────────────────────────────────────

    private record struct PortEntry(int PortNum, Complex Z0, int BranchIndex);

    /// <summary>
    /// Preliminary stamp pass (ω=1) to capture port branch indices and build a
    /// branch-index→component-name map for singularity diagnostics.
    /// Two-phase: non-mutual first so LastBranchIndex is stable when mutuals stamp.
    /// </summary>
    private static (List<PortEntry> Ports, Dictionary<int, string> BranchLabels)
        CollectPortsAndBranchLabels(ElaboratedNetlist netlist, int nonGroundNodes)
    {
        var tempMna      = new MnaSystem(nonGroundNodes);
        var branchLabels = new Dictionary<int, string>();

        foreach (var ec in netlist.Components)
        {
            if (ec.Model is MutualInductanceModel) continue;
            int before = tempMna.BranchCount;
            ec.Model.Stamp(tempMna, ec, 1.0);
            for (int b = before; b < tempMna.BranchCount; b++)
                branchLabels[tempMna.NodeCount + b] = $"{ec.ComponentType}:{ec.InstancePath}";
        }

        foreach (var ec in netlist.Components)
            if (ec.Model is MutualInductanceModel)
                ec.Model.Stamp(tempMna, ec, 1.0);

        var ports = new List<PortEntry>();
        foreach (var ec in netlist.Components)
        {
            if (ec.Model is PortModel pm)
                ports.Add(new PortEntry(GetPortNum(ec), GetZ0(ec), pm.LastBranchIndex));
            else if (ec.Model is TermModel tm)
                ports.Add(new PortEntry(GetPortNum(ec), GetZ0(ec), tm.LastBranchIndex));
        }

        ports.Sort((a, b) => a.PortNum.CompareTo(b.PortNum));
        return (ports, branchLabels);
    }

    private static int GetPortNum(ElaboratedComponent ec)
    {
        if (!ec.Parameters.TryGetValue("Num", out var v))
            throw new InvalidOperationException(
                $"{ec.InstancePath}: Port/Term is missing the Num parameter.");
        return (int)v.AsReal();
    }

    private static Complex GetZ0(ElaboratedComponent ec)
    {
        if (!ec.Parameters.TryGetValue("Z", out var v))
            return new Complex(50, 0);
        return v.Kind == CircuitRF.Core.Expressions.ValueKind.Complex
            ? v.AsComplex()
            : new Complex(v.AsReal(), 0);
    }
}
