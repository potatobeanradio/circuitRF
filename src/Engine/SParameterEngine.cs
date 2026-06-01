using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using NumFlat;
using RfCore;

namespace CircuitRF.Engine;

/// <summary>
/// Runs an S-parameter sweep over a frequency grid (linear-engine §2, §6, §9).
///
/// Per frequency:
///   1. Stamp all components (independent sources zeroed) into MnaSystem.
///   2. Add gmin conductance to every non-ground node (§5).
///   3. Factorize once (AMD permutation cached after first call).
///   4. For each port j: solve with 1 V drive at port j, 0 V at all others.
///      Read port branch currents → column j of the port Y-matrix.
///   5. Convert Y → S via RfCore (power-wave, per-port complex Z0).
///
/// Returns a DataSet with cube "S" {freq, i, j} (Complex, port indices 1-based).
/// </summary>
public static class SParameterEngine
{
    /// <summary>gmin conductance added from every node to ground (§5).</summary>
    public const double DefaultGmin = 1e-12;

    public static SNP Run(
        ElaboratedNetlist netlist,
        double[]          freqsHz,
        double            gmin = DefaultGmin)
    {
        // ── Identify ports (Port and Term instances, sorted by Num) ──────────
        var ports = CollectPorts(netlist);
        if (ports.Count == 0)
            throw new InvalidOperationException(
                "S-parameter analysis requires at least one Port or Term component.");
        int N = ports.Count;

        int nonGroundNodes = netlist.Nodes.Count - 1; // Count includes ground at index 0
        var mna = new MnaSystem(nonGroundNodes);

        var freqCount    = freqsHz.Length;
        var sMatrices    = new Mat<Complex>[freqCount];
        var z0PerPort    = ports.Select(p => p.Z0).ToArray();

        for (int fi = 0; fi < freqCount; fi++)
        {
            double hz    = freqsHz[fi];
            double omega = 2.0 * Math.PI * hz;

            // ── Stamp all components (sources zeroed) ─────────────────────
            // Inductors must be stamped before mutuals so LastBranchIndex is set.
            mna.Reset();
            foreach (var ec in netlist.Components)
                if (ec.Model is not CircuitRF.Core.Devices.MutualInductanceModel)
                    ec.Model.Stamp(mna, ec, omega);
            foreach (var ec in netlist.Components)
                if (ec.Model is CircuitRF.Core.Devices.MutualInductanceModel)
                    ec.Model.Stamp(mna, ec, omega);

            // ── gmin: small conductance from every node to ground ─────────
            for (int n = 1; n <= nonGroundNodes; n++)
                mna.AddAdmittance(n, 0, new Complex(gmin, 0.0));

            // ── Factorize once at this frequency ──────────────────────────
            var lu = mna.Factorize();

            // ── Extract port Y-matrix via unit-voltage excitation ─────────
            // For port j: drive = 1 V; all other ports = 0 V (short circuit).
            // Y_kj = branch current at port k when port j is driven with 1 V.
            var yMat    = new Mat<Complex>(N, N);
            var xBuf    = new Complex[mna.Size];

            for (int j = 0; j < N; j++)
            {
                var b = mna.BuildRhsWithPortDrive(
                    branchRow:  ports[j].BranchIndex,
                    driveValue: Complex.One);

                lu.Solve(b, xBuf);

                // Branch current flows FROM signal TO ref (AddBranchCurrent convention).
                // Port current (flowing INTO the positive terminal) = −branch_current.
                for (int k = 0; k < N; k++)
                    yMat[k, j] = -xBuf[ports[k].BranchIndex];
            }

            // ── Y → S via RfCore (power-wave, per-port complex Z0) ────────
            sMatrices[fi] = RFNetwork.YToS(yMat, z0PerPort);
        }

        // ── Wrap result as SNP (Type=S, Z0 = first port's Z0 for metadata) ─
        // The per-port Z0 was used for renorm; store uniform Z0 in the SNP
        // or the most common value. For hero1 all ports are 50 Ω.
        var refZ0 = z0PerPort.Length > 0 ? z0PerPort[0] : new Complex(50, 0);
        var result = new SNP(freqsHz, sMatrices, MatrixType.S, MatrixFormat.RI, refZ0);
        return result;
    }

    // ── Port collection ────────────────────────────────────────────────────

    private record struct PortEntry(int PortNum, Complex Z0, int BranchIndex);

    private static List<PortEntry> CollectPorts(ElaboratedNetlist netlist)
    {
        // Perform a single stamp pass with a temporary MnaSystem to get branch indices.
        // The real analysis will redo this, but using the same order → same indices.
        int nonGroundNodes = netlist.Nodes.Count - 1;
        var tempMna = new MnaSystem(nonGroundNodes);
        foreach (var ec in netlist.Components)
            ec.Model.Stamp(tempMna, ec, 1.0); // omega=1, values don't matter here

        // Now read back the LastBranchIndex from each Port/Term model
        var ports = new List<PortEntry>();
        foreach (var ec in netlist.Components)
        {
            int portNum;
            Complex z0;
            int branchIdx;

            if (ec.Model is PortModel pm)
            {
                portNum   = GetPortNum(ec);
                z0        = GetZ0(ec);
                branchIdx = pm.LastBranchIndex;
            }
            else if (ec.Model is TermModel tm)
            {
                portNum   = GetPortNum(ec);
                z0        = GetZ0(ec);
                branchIdx = tm.LastBranchIndex;
            }
            else continue;

            ports.Add(new PortEntry(portNum, z0, branchIdx));
        }

        ports.Sort((a, b) => a.PortNum.CompareTo(b.PortNum));
        return ports;
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
            return new Complex(50, 0);  // default 50 Ω
        return v.Kind == CircuitRF.Core.Expressions.ValueKind.Complex
            ? v.AsComplex()
            : new Complex(v.AsReal(), 0);
    }
}
