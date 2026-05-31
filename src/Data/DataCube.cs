// ================================================================
//  DataCube.cs  —  Labeled N-dimensional array, single DataKind
//
//  The storage primitive and unit splotRF plots.  Backed by a flat
//  buffer (Complex[] or double[]) with strides; supports slicing and
//  reduction along named axes.
//
//  Contract: src/Core/Data/CLAUDE.md (circuitRF repo)
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace RfCore.Data
{
    // ============================================================
    //  DataKind
    // ============================================================

    public enum DataKind { Real, Complex }

    // ============================================================
    //  Axis  —  a named, unit-bearing axis
    // ============================================================

    public sealed class Axis
    {
        public string   Name   { get; }
        public double[] Values { get; }
        public string   Unit   { get; }
        public int      Length => Values.Length;

        public Axis(string name, double[] values, string unit = "")
        {
            Name   = name;
            Values = (double[])values.Clone();
            Unit   = unit;
        }

        internal Axis Slice(Range r)
        {
            var (offset, length) = r.GetOffsetAndLength(Values.Length);
            var sliced = new double[length];
            Array.Copy(Values, offset, sliced, 0, length);
            return new Axis(Name, sliced, Unit);
        }
    }

    // ============================================================
    //  DataCube
    // ============================================================

    /// <summary>
    /// Labeled N-dimensional array with a single <see cref="DataKind"/>
    /// (Real or Complex).  Axes are named and unit-bearing.
    /// </summary>
    public sealed class DataCube
    {
        // ---- Storage -------------------------------------------

        private readonly Complex[]? _complexData;
        private readonly double[]?  _realData;
        private readonly int[]      _strides;

        // ---- Public metadata -----------------------------------

        public DataKind DataKind { get; }
        public int      Rank     => Axes.Count;
        public IReadOnlyList<Axis> Axes { get; }

        // ---- Constructors --------------------------------------

        /// <summary>Create a Complex DataCube with pre-filled data.</summary>
        public DataCube(Axis[] axes, Complex[] data)
        {
            Axes         = axes.ToList().AsReadOnly();
            DataKind     = DataKind.Complex;
            _complexData = (Complex[])data.Clone();
            _strides     = ComputeStrides(axes);
            ValidateSize(_strides, axes, data.Length);
        }

        /// <summary>Create a Real DataCube with pre-filled data.</summary>
        public DataCube(Axis[] axes, double[] data)
        {
            Axes      = axes.ToList().AsReadOnly();
            DataKind  = DataKind.Real;
            _realData = (double[])data.Clone();
            _strides  = ComputeStrides(axes);
            ValidateSize(_strides, axes, data.Length);
        }

        private DataCube(Axis[] axes, Complex[] data, bool noCopy)
        {
            Axes         = axes.ToList().AsReadOnly();
            DataKind     = DataKind.Complex;
            _complexData = noCopy ? data : (Complex[])data.Clone();
            _strides     = ComputeStrides(axes);
        }

        private DataCube(Axis[] axes, double[] data, bool noCopy)
        {
            Axes      = axes.ToList().AsReadOnly();
            DataKind  = DataKind.Real;
            _realData = noCopy ? data : (double[])data.Clone();
            _strides  = ComputeStrides(axes);
        }

        // ---- Raw access ----------------------------------------

        /// <summary>
        /// Backing Complex[] for interop/plotting.  Only valid for Complex cubes.
        /// </summary>
        public Complex[] ComplexValues =>
            DataKind == DataKind.Complex
                ? (Complex[])_complexData!.Clone()
                : throw new InvalidOperationException("Cube is Real.");

        /// <summary>
        /// Backing double[] for interop/plotting.  Only valid for Real cubes.
        /// </summary>
        public double[] RealValues =>
            DataKind == DataKind.Real
                ? (double[])_realData!.Clone()
                : throw new InvalidOperationException("Cube is Complex.");

        // ---- Axis accessor -------------------------------------

        public Axis Axis(string name)
        {
            foreach (var a in Axes) if (a.Name == name) return a;
            throw new ArgumentException($"No axis named '{name}'.");
        }

        public Axis Axis(int dim) => Axes[dim];

        // ---- Positional indexer --------------------------------

        /// <summary>
        /// Positional indexer: every arg is an axis index.
        /// A single int pins (collapses) that dimension; a Range keeps it.
        /// Returns DataCube if any Range is present, bare element otherwise.
        /// See CLAUDE.md for slice semantics.
        /// </summary>
        public SliceResult this[params object[] args] => Slice(args);

        internal SliceResult Slice(object[] args)
        {
            if (args.Length != Rank)
                throw new ArgumentException(
                    $"Expected {Rank} slice arguments, got {args.Length}.");

            // Determine which axes survive (Range args) vs collapse (int args)
            var survivingAxes = new List<Axis>();
            var axisRanges    = new (bool isPin, int pin, int offset, int length)[Rank];

            for (int d = 0; d < Rank; d++)
            {
                int axisLen = Axes[d].Length;
                switch (args[d])
                {
                    case int pinIdx:
                        if (pinIdx < 0 || pinIdx >= axisLen)
                            throw new ArgumentOutOfRangeException(
                                $"Axis '{Axes[d].Name}' index {pinIdx} out of range [0,{axisLen}).");
                        axisRanges[d] = (true, pinIdx, pinIdx, 1);
                        break;

                    case Range r:
                        var (off, len) = r.GetOffsetAndLength(axisLen);
                        survivingAxes.Add(Axes[d].Slice(r));
                        axisRanges[d] = (false, 0, off, len);
                        break;

                    default:
                        throw new ArgumentException(
                            $"Slice arg {d} must be int or Range, got {args[d]?.GetType().Name}.");
                }
            }

            // If every axis was pinned → return bare element
            if (survivingAxes.Count == 0)
            {
                int flatIdx = FlatIndex(axisRanges.Select(r => r.pin).ToArray());
                return DataKind == DataKind.Complex
                    ? new SliceResult(_complexData![flatIdx])
                    : new SliceResult(_realData![flatIdx]);
            }

            // Otherwise → gather elements into a new DataCube
            var axArr = survivingAxes.ToArray();
            int totalElements = axArr.Aggregate(1, (acc, a) => acc * a.Length);

            if (DataKind == DataKind.Complex)
            {
                var buf = new Complex[totalElements];
                GatherComplex(axisRanges, axArr, buf, 0, 0, 0);
                return new SliceResult(new DataCube(axArr, buf, noCopy: true));
            }
            else
            {
                var buf = new double[totalElements];
                GatherReal(axisRanges, axArr, buf, 0, 0, 0);
                return new SliceResult(new DataCube(axArr, buf, noCopy: true));
            }
        }

        // ---- Element-wise transforms ---------------------------

        public DataCube Real()
        {
            if (DataKind == DataKind.Real) return this;
            var buf = new double[_complexData!.Length];
            for (int i = 0; i < buf.Length; i++) buf[i] = _complexData[i].Real;
            return new DataCube(AxesArray(), buf, noCopy: true);
        }

        public DataCube Imag()
        {
            RequireComplex("Imag");
            var buf = new double[_complexData!.Length];
            for (int i = 0; i < buf.Length; i++) buf[i] = _complexData[i].Imaginary;
            return new DataCube(AxesArray(), buf, noCopy: true);
        }

        public DataCube Mag()
        {
            if (DataKind == DataKind.Real) return this;
            var buf = new double[_complexData!.Length];
            for (int i = 0; i < buf.Length; i++) buf[i] = _complexData[i].Magnitude;
            return new DataCube(AxesArray(), buf, noCopy: true);
        }

        public DataCube Phase(bool degrees = true)
        {
            RequireComplex("Phase");
            double scale = degrees ? 180.0 / Math.PI : 1.0;
            var buf = new double[_complexData!.Length];
            for (int i = 0; i < buf.Length; i++) buf[i] = _complexData[i].Phase * scale;
            return new DataCube(AxesArray(), buf, noCopy: true);
        }

        /// <summary>10·log₁₀(|z|) — power dB.</summary>
        public DataCube DB10()
        {
            var buf = new double[ElementCount()];
            if (DataKind == DataKind.Complex)
                for (int i = 0; i < buf.Length; i++)
                    buf[i] = 10.0 * Math.Log10(_complexData![i].Magnitude + 1e-300);
            else
                for (int i = 0; i < buf.Length; i++)
                    buf[i] = 10.0 * Math.Log10(Math.Abs(_realData![i]) + 1e-300);
            return new DataCube(AxesArray(), buf, noCopy: true);
        }

        /// <summary>20·log₁₀(|z|) — amplitude dB. Use for S-parameters and voltages.</summary>
        public DataCube DB20()
        {
            var buf = new double[ElementCount()];
            if (DataKind == DataKind.Complex)
                for (int i = 0; i < buf.Length; i++)
                    buf[i] = 20.0 * Math.Log10(_complexData![i].Magnitude + 1e-300);
            else
                for (int i = 0; i < buf.Length; i++)
                    buf[i] = 20.0 * Math.Log10(Math.Abs(_realData![i]) + 1e-300);
            return new DataCube(AxesArray(), buf, noCopy: true);
        }

        /// <summary>Alias for DB10() — power dB of whatever the cube holds.</summary>
        public DataCube DB() => DB10();

        public DataCube Conj()
        {
            RequireComplex("Conj");
            var buf = new Complex[_complexData!.Length];
            for (int i = 0; i < buf.Length; i++) buf[i] = Complex.Conjugate(_complexData[i]);
            return new DataCube(AxesArray(), buf, noCopy: true);
        }

        // ---- Reductions ----------------------------------------

        public DataCube Max(string axisName) => Reduce(axisName, (a, b) => a > b, isMin: false);
        public DataCube Min(string axisName) => Reduce(axisName, (a, b) => a < b, isMin: true);

        public DataCube Peak(string axisName)
        {
            if (DataKind != DataKind.Real)
                throw new InvalidOperationException("Peak requires a Real cube — call .Mag() first.");
            return Reduce(axisName, (a, b) => Math.Abs(a) > Math.Abs(b), isMin: false);
        }

        public DataCube At(string axisName, int index)
        {
            int dim = AxisIndex(axisName);
            var args = new object[Rank];
            for (int d = 0; d < Rank; d++)
                args[d] = d == dim ? (object)index : (object)(..);
            var result = Slice(args);
            return result.Cube!;
        }

        // ---- Helpers -------------------------------------------

        private int ElementCount() =>
            DataKind == DataKind.Complex ? _complexData!.Length : _realData!.Length;

        private Axis[] AxesArray() => Axes.ToArray();

        private int AxisIndex(string name)
        {
            for (int d = 0; d < Rank; d++)
                if (Axes[d].Name == name) return d;
            throw new ArgumentException($"No axis named '{name}'.");
        }

        private void RequireComplex(string op)
        {
            if (DataKind != DataKind.Complex)
                throw new InvalidOperationException($"{op} requires a Complex cube.");
        }

        private int FlatIndex(int[] indices)
        {
            int flat = 0;
            for (int d = 0; d < Rank; d++) flat += indices[d] * _strides[d];
            return flat;
        }

        private static int[] ComputeStrides(Axis[] axes)
        {
            var strides = new int[axes.Length];
            int s = 1;
            for (int d = axes.Length - 1; d >= 0; d--)
            {
                strides[d] = s;
                s *= axes[d].Length;
            }
            return strides;
        }

        private static void ValidateSize(int[] strides, Axis[] axes, int dataLength)
        {
            int expected = axes.Length == 0 ? 0 : strides[0] * axes[0].Length;
            if (dataLength != expected)
                throw new ArgumentException(
                    $"Data length {dataLength} does not match axes shape {expected}.");
        }

        // ---- Gather helpers (recursive dimension walk) ---------

        private void GatherComplex(
            (bool isPin, int pin, int offset, int length)[] axisRanges,
            Axis[] surviving, Complex[] buf,
            int srcDim, int srcFlat, int dstFlat)
        {
            if (srcDim == Rank)
            {
                buf[dstFlat] = _complexData![srcFlat];
                return;
            }
            var (isPin, pin, offset, length) = axisRanges[srcDim];
            if (isPin)
            {
                GatherComplex(axisRanges, surviving, buf,
                              srcDim + 1, srcFlat + pin * _strides[srcDim], dstFlat);
            }
            else
            {
                // Find which surviving axis this maps to, to compute dst stride
                int dstDim   = CountSurvivingBefore(axisRanges, srcDim);
                int dstStride = DstStride(surviving, dstDim);
                for (int i = 0; i < length; i++)
                {
                    GatherComplex(axisRanges, surviving, buf,
                                  srcDim + 1,
                                  srcFlat + (offset + i) * _strides[srcDim],
                                  dstFlat  + i * dstStride);
                }
            }
        }

        private void GatherReal(
            (bool isPin, int pin, int offset, int length)[] axisRanges,
            Axis[] surviving, double[] buf,
            int srcDim, int srcFlat, int dstFlat)
        {
            if (srcDim == Rank)
            {
                buf[dstFlat] = _realData![srcFlat];
                return;
            }
            var (isPin, pin, offset, length) = axisRanges[srcDim];
            if (isPin)
            {
                GatherReal(axisRanges, surviving, buf,
                           srcDim + 1, srcFlat + pin * _strides[srcDim], dstFlat);
            }
            else
            {
                int dstDim    = CountSurvivingBefore(axisRanges, srcDim);
                int dstStride = DstStride(surviving, dstDim);
                for (int i = 0; i < length; i++)
                {
                    GatherReal(axisRanges, surviving, buf,
                               srcDim + 1,
                               srcFlat + (offset + i) * _strides[srcDim],
                               dstFlat  + i * dstStride);
                }
            }
        }

        private static int CountSurvivingBefore(
            (bool isPin, int pin, int offset, int length)[] axisRanges, int dim)
        {
            int count = 0;
            for (int d = 0; d < dim; d++)
                if (!axisRanges[d].isPin) count++;
            return count;
        }

        private static int DstStride(Axis[] surviving, int dstDim)
        {
            int s = 1;
            for (int d = surviving.Length - 1; d > dstDim; d--)
                s *= surviving[d].Length;
            return s;
        }

        private DataCube Reduce(string axisName, Func<double, double, bool> isBetter, bool isMin)
        {
            if (DataKind != DataKind.Real)
                throw new InvalidOperationException("Reduce requires a Real cube — call .Mag() first.");

            int dim    = AxisIndex(axisName);
            int axisLen = Axes[dim].Length;
            if (axisLen == 0)
                throw new InvalidOperationException("Cannot reduce an empty axis.");

            // Build output axes (remove the reduced dimension)
            var outAxes = Axes.Where((_, d) => d != dim).ToArray();
            int outLen  = outAxes.Length == 0 ? 1
                        : outAxes.Aggregate(1, (acc, a) => acc * a.Length);
            var buf = new double[outLen];
            var found = new bool[outLen];

            // Walk all elements
            int totalIn = ElementCount();
            var coords  = new int[Rank];
            for (int flat = 0; flat < totalIn; flat++)
            {
                // Decode flat index to coords
                int tmp = flat;
                for (int d = Rank - 1; d >= 0; d--)
                {
                    coords[d] = tmp % Axes[d].Length;
                    tmp       /= Axes[d].Length;
                }
                // Output index: remove dimension `dim`
                int outFlat = 0;
                for (int d = 0, od = 0; d < Rank; d++)
                {
                    if (d == dim) continue;
                    outFlat += coords[d] * DstStride(outAxes, od);
                    od++;
                }

                double val = _realData![flat];
                if (!found[outFlat] || isBetter(val, buf[outFlat]))
                {
                    buf[outFlat] = val;
                    found[outFlat] = true;
                }
            }

            return outAxes.Length == 0
                ? new DataCube(Array.Empty<Axis>(), buf, noCopy: true)
                : new DataCube(outAxes, buf, noCopy: true);
        }
    }

    // ============================================================
    //  SliceResult — discriminated union (DataCube | Complex | double)
    // ============================================================

    /// <summary>
    /// Result of a slice operation.  When all axes were pinned the
    /// result is the bare element; otherwise it is a DataCube.
    /// </summary>
    public sealed class SliceResult
    {
        public DataCube? Cube        { get; }
        public Complex?  ComplexValue { get; }
        public double?   RealValue    { get; }
        public bool      IsCube       => Cube        != null;
        public bool      IsComplex    => ComplexValue != null;
        public bool      IsReal       => RealValue    != null;

        internal SliceResult(DataCube cube)         { Cube         = cube; }
        internal SliceResult(Complex  complexValue) { ComplexValue = complexValue; }
        internal SliceResult(double   realValue)    { RealValue    = realValue; }

        public static implicit operator DataCube(SliceResult r) =>
            r.Cube ?? throw new InvalidCastException("SliceResult is a bare element, not a DataCube.");
        public static implicit operator Complex(SliceResult r) =>
            r.ComplexValue ?? throw new InvalidCastException("SliceResult is not a Complex scalar.");
        public static implicit operator double(SliceResult r) =>
            r.RealValue ?? throw new InvalidCastException("SliceResult is not a Real scalar.");
    }
}
