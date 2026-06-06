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
    //  DataSetBuilder  —  helpers for S-parameter DataSet ↔ SNP
    // ================================================================

    public static class DataSetBuilder
    {
        /// <summary>
        /// Wrap an SNP (must be S-type) as a DataSet containing a single
        /// "S" cube with axes [freq, i, j].
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
            return ds;
        }

        /// <summary>
        /// Extract the "S" cube from a DataSet and reconstruct an SNP for Touchstone I/O.
        /// The DataSet must contain an "S" cube with axes [freq, i, j].
        /// Reference impedance is 50Ω real (standard; per-port complex Z0 is not stored in the cube).
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

            return new SNP(freqs, mats, MatrixType.S, MatrixFormat.RI, new Complex(50, 0));
        }
    }
}
