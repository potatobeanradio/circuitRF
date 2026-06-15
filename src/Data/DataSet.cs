// ================================================================
//  DataSet.cs  —  Named collection of DataCubes
//
//  The container a run returns.  Holds many DataCubes of mixed kind
//  (Complex and Real) keyed by name.  Also provides the convenience
//  network-parameter accessors S(), Y(), Z() that speak the user's
//  language (port numbers, the way RF engineers think).
//
//  Contract: src/Core/Data/CLAUDE.md (circuitRF repo)
// ================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using NumFlat;
using RfCore.Data;

namespace RfCore.Data
{
    /// <summary>
    /// Named collection of <see cref="DataCube"/>s returned by a single analysis run.
    /// Mixed kinds (Complex / Real) coexist — one DataSet holds both S-param cubes
    /// and derived real measurements (PAE, Gain, …).
    /// </summary>
    public sealed class DataSet
    {
        private readonly Dictionary<string, DataCube> _cubes = new();

        // ---- Cube registration ----------------------------------

        public void Add(string name, DataCube cube)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Cube name must be non-empty.");
            _cubes[name] = cube;
        }

        public DataCube this[string name] =>
            _cubes.TryGetValue(name, out var c) ? c
            : throw new KeyNotFoundException($"No cube named '{name}' in this DataSet.");

        public bool Contains(string name) => _cubes.ContainsKey(name);

        /// <summary>
        /// Stack multiple DataSets along a new prepended axis.
        /// Every cube present in all DataSets is stacked; any cube missing from a DataSet
        /// causes an exception.  Call with a sweep axis whose length equals datasets.Count.
        /// </summary>
        public static DataSet StackSweepAxis(Axis sweepAxis, IReadOnlyList<DataSet> datasets)
        {
            if (datasets.Count == 0)
                throw new ArgumentException("At least one DataSet is required.", nameof(datasets));

            var result = new DataSet();
            foreach (var key in datasets[0].Cubes.Keys)
            {
                var cubes = new DataCube[datasets.Count];
                for (int n = 0; n < datasets.Count; n++)
                    cubes[n] = datasets[n][key];
                result.Add(key, DataCube.PrependAxis(sweepAxis, cubes));
            }
            return result;
        }

        public IReadOnlyDictionary<string, DataCube> Cubes =>
            (IReadOnlyDictionary<string, DataCube>)_cubes;

        // ---- Network-parameter convenience accessors ------------
        //
        //  S(i,j), Y(i,j), Z(i,j) address the cube by user port numbers.
        //  Port numbers are 1-based (S(2,1) = S21), matching Touchstone /
        //  splotRF convention.  The cube must have axes named "freq", "i", "j"
        //  and index axes store 1-based port numbers in their Values arrays.

        /// <summary>S(i,j) trace over frequency. i = response port, j = drive port (1-based).</summary>
        public DataCube S(int i, int j) => ParameterTrace("S", i, j);

        /// <summary>Y(i,j) trace over frequency.</summary>
        public DataCube Y(int i, int j) => ParameterTrace("Y", i, j);

        /// <summary>Z(i,j) trace over frequency.</summary>
        public DataCube Z(int i, int j) => ParameterTrace("Z", i, j);

        private DataCube ParameterTrace(string cubeName, int i, int j)
        {
            var cube     = this[cubeName];
            int iAxisDim = FindAxisDim(cube, "i");
            int jAxisDim = FindAxisDim(cube, "j");

            int iIdx = PortIndex(cube.Axes[iAxisDim], i, cubeName, "i");
            int jIdx = PortIndex(cube.Axes[jAxisDim], j, cubeName, "j");

            // Build slice args: pin i and j, keep all other axes (freq, sweep, …)
            var args = new object[cube.Rank];
            for (int d = 0; d < cube.Rank; d++)
            {
                if      (d == iAxisDim) args[d] = iIdx;
                else if (d == jAxisDim) args[d] = jIdx;
                else                    args[d] = (..);
            }
            return (DataCube)cube.Slice(args);
        }

        private static int FindAxisDim(DataCube cube, string name)
        {
            for (int d = 0; d < cube.Rank; d++)
                if (cube.Axes[d].Name == name) return d;
            throw new InvalidOperationException(
                $"Parameter cube does not have an axis named '{name}'.");
        }

        private static int PortIndex(Axis axis, int portNumber, string cubeName, string axisName)
        {
            for (int k = 0; k < axis.Values.Length; k++)
                if ((int)Math.Round(axis.Values[k]) == portNumber) return k;
            throw new ArgumentException(
                $"Port {portNumber} not found on axis '{axisName}' of cube '{cubeName}'.");
        }

        // ---- Node-voltage / branch-current accessors -----------

        /// <summary>
        /// V(nodeName, …)  — node voltage trace.
        /// Pins the node axis by name; remaining args address (harmonic, sweep, …) positionally.
        /// </summary>
        public DataCube V(string nodeName, params object[] remainingArgs) =>
            NodeTrace("V", "node", nodeName, remainingArgs);

        /// <summary>
        /// I(branchName, …)  — branch current trace.
        /// </summary>
        public DataCube I(string branchName, params object[] remainingArgs) =>
            NodeTrace("I", "node", branchName, remainingArgs);

        private DataCube NodeTrace(string cubeName, string nodeAxisName,
                                   string nodeLabel, object[] remainingArgs)
        {
            var cube    = this[cubeName];
            int nodeDim = FindAxisDim(cube, nodeAxisName);
            int nodeIdx = LabelIndex(cube.Axes[nodeDim], nodeLabel, cubeName, nodeAxisName);

            if (remainingArgs.Length != cube.Rank - 1)
                throw new ArgumentException(
                    $"Expected {cube.Rank - 1} positional args after node name, got {remainingArgs.Length}.");

            var args   = new object[cube.Rank];
            int argPos = 0;
            for (int d = 0; d < cube.Rank; d++)
            {
                if (d == nodeDim) args[d] = nodeIdx;
                else               args[d] = remainingArgs[argPos++];
            }
            return (DataCube)cube.Slice(args);
        }

        private static int LabelIndex(Axis axis, string label, string cubeName, string axisName)
        {
            if (axis.Labels is null)
                throw new InvalidOperationException(
                    $"Axis '{axisName}' of cube '{cubeName}' has no string labels — " +
                    $"cannot resolve name '{label}'. " +
                    "Only node/branch axes built with Axis.Labels support name lookup.");

            for (int k = 0; k < axis.Labels.Length; k++)
                if (axis.Labels[k] == label) return k;

            throw new ArgumentException(
                $"Node/branch name '{label}' not found on axis '{axisName}' of cube '{cubeName}'. " +
                $"Available: [{string.Join(", ", axis.Labels)}]");
        }
    }

    // ================================================================
    //  Z0Kind  —  classification of a Z0 cube's uniformity and kind
    // ================================================================

    /// <summary>
    /// Classification of the per-port reference impedances stored in a <c>Z0</c> cube.
    /// Used by the Data Display indicator to decide which badge (if any) to show.
    /// </summary>
    public enum Z0Kind
    {
        /// <summary>All ports share the same real-valued reference impedance (e.g. 50 Ω).</summary>
        UniformReal,
        /// <summary>All ports share the same complex reference impedance (e.g. 75−j10 Ω).</summary>
        UniformComplex,
        /// <summary>Ports have different reference impedances.</summary>
        NonUniform,
    }

    // ================================================================
    //  DataSetBuilder  —  helpers for S-parameter DataSet ↔ SNP
    // ================================================================

    public static class DataSetBuilder
    {
        private const double Z0UniformTol = 1e-9;   // 1 nΩ — negligible for any RF work

        /// <summary>
        /// Build a 1-axis Complex DataCube representing per-port reference impedances.
        /// Axis name "port", unit "port", 1-based port numbers; values are complex ohms.
        /// </summary>
        public static DataCube BuildZ0Cube(Complex[] z0PerPort)
        {
            int n        = z0PerPort.Length;
            var portVals = new double[n];
            for (int p = 0; p < n; p++) portVals[p] = p + 1;
            return new DataCube(new[] { new Axis("port", portVals, "port") }, z0PerPort);
        }

        /// <summary>
        /// Classify the reference impedances in a Z0 cube: all uniform-real, uniform-complex,
        /// or non-uniform (different values per port).
        /// </summary>
        public static Z0Kind ClassifyZ0(DataCube z0Cube)
        {
            var vals = z0Cube.ComplexValues;
            if (vals.Length == 0) return Z0Kind.UniformReal;

            var first   = vals[0];
            bool uniform = true;
            for (int i = 1; i < vals.Length; i++)
            {
                if (Math.Abs(vals[i].Real      - first.Real)      > Z0UniformTol ||
                    Math.Abs(vals[i].Imaginary - first.Imaginary) > Z0UniformTol)
                {
                    uniform = false;
                    break;
                }
            }
            if (!uniform) return Z0Kind.NonUniform;

            bool allReal = true;
            for (int i = 0; i < vals.Length; i++)
            {
                if (Math.Abs(vals[i].Imaginary) > Z0UniformTol) { allReal = false; break; }
            }
            return allReal ? Z0Kind.UniformReal : Z0Kind.UniformComplex;
        }

        /// <summary>
        /// Wrap an SNP (must be S-type) as a DataSet containing an "S" cube [freq, i, j]
        /// and a "Z0" cube [port] with per-port complex reference impedances.
        /// Port axis values are 1-based port numbers.
        /// </summary>
        public static DataSet FromSnp(SNP snp)
        {
            if (snp.IsEmpty)
                throw new ArgumentException("Cannot build a DataSet from an empty SNP.");

            if (snp.Type != MatrixType.S)
                snp = RFNetwork.ZToS(snp.Type == MatrixType.Z ? snp : RFNetwork.YToS(snp));

            int nFreq  = snp.FrequencyCount;
            int nPorts = snp.Ports;

            var freqAxis = new Axis("freq", snp.Frequencies, "Hz");
            var portVals = new double[nPorts];
            for (int p = 0; p < nPorts; p++) portVals[p] = p + 1;
            var iAxis = new Axis("i", portVals, "port");
            var jAxis = new Axis("j", portVals, "port");

            // Lay out as [freq, i, j] row-major
            var data = new Complex[nFreq * nPorts * nPorts];
            for (int fi = 0; fi < nFreq; fi++)
            {
                var mat = snp.Matrices[fi];
                for (int i = 0; i < nPorts; i++)
                for (int j = 0; j < nPorts; j++)
                    data[fi * nPorts * nPorts + i * nPorts + j] = mat[i, j];
            }

            var sCube = new DataCube(new[] { freqAxis, iAxis, jAxis }, data);
            var ds    = new DataSet();
            ds.Add("S", sCube);

            // Uniform Z0 cube — every Touchstone-derived S DataSet carries one
            // so consumers can always rely on "Z0" being present.
            var z0Vals = new Complex[nPorts];
            for (int p = 0; p < nPorts; p++) z0Vals[p] = snp.Z0;
            ds.Add("Z0", BuildZ0Cube(z0Vals));

            return ds;
        }

        /// <summary>
        /// Extract the "S" cube from a DataSet and reconstruct an SNP for Touchstone I/O.
        /// Reads the "Z0" cube for the reference impedance when present; falls back to 50 Ω.
        /// Non-uniform Z0 is flattened to port-1's value (SNP is uniform-only by design).
        /// </summary>
        public static SNP ToSnp(DataSet ds)
        {
            var cube   = ds["S"];
            int nFreq  = cube.Axes[0].Length;
            int nPorts = cube.Axes[1].Length;
            var freqs  = cube.Axes[0].Values;
            var raw    = cube.ComplexValues;  // row-major [freq, i, j]

            var mats = new NumFlat.Mat<Complex>[nFreq];
            for (int fi = 0; fi < nFreq; fi++)
            {
                mats[fi] = new NumFlat.Mat<Complex>(nPorts, nPorts);
                for (int i = 0; i < nPorts; i++)
                for (int j = 0; j < nPorts; j++)
                    mats[fi][i, j] = raw[fi * nPorts * nPorts + i * nPorts + j];
            }

            Complex refZ0;
            if (ds.Contains("Z0"))
            {
                var z0Cube = ds["Z0"];
                var z0Kind = ClassifyZ0(z0Cube);
                refZ0 = z0Cube.ComplexValues[0];
                if (z0Kind == Z0Kind.NonUniform)
                {
                    // SNP/Touchstone is uniform-only; use port-1's value and warn.
                    RFNetwork.Warn(
                        "ToSnp: DataSet has non-uniform per-port Z0 — SNP/Touchstone supports " +
                        "only a single reference impedance; using port-1 value " +
                        $"({refZ0} Ω). A cube-direct path is required for faithful non-uniform handling.");
                }
            }
            else
            {
                refZ0 = new Complex(50, 0);   // legacy .npy without Z0 cube
            }

            return new SNP(freqs, mats, MatrixType.S, MatrixFormat.RI, refZ0);
        }
    }
}
