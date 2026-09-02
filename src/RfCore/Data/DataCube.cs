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
        public string    Name   { get; }
        public double[]  Values { get; }
        public string    Unit   { get; }
        /// <summary>
        /// Optional per-element string labels (same length as Values).
        /// Used by the node/branch axes of V and I cubes so that
        /// <see cref="DataSet.V"/> / <see cref="DataSet.I"/> can resolve
        /// a name like "X1.drain" or "X1.M1:d" to an axis index.
        /// Null for numeric-only axes (frequency, port number, etc.).
        /// </summary>
        public string[]? Labels { get; }
        public int       Length => Values.Length;

        public Axis(string name, double[] values, string unit = "", string[]? labels = null)
        {
            if (labels is not null && labels.Length != values.Length)
                throw new ArgumentException(
                    $"Labels length ({labels.Length}) must match Values length ({values.Length}).");
            Name   = name;
            Values = (double[])values.Clone();
            Unit   = unit;
            Labels = labels is not null ? (string[])labels.Clone() : null;
        }

        internal Axis Slice(Range r)
        {
            var (offset, length) = r.GetOffsetAndLength(Values.Length);
            var sliced = new double[length];
            Array.Copy(Values, offset, sliced, 0, length);
            string[]? slicedLabels = null;
            if (Labels is not null)
            {
                slicedLabels = new string[length];
                Array.Copy(Labels, offset, slicedLabels, 0, length);
            }
            return new Axis(Name, sliced, Unit, slicedLabels);
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
        /// <summary>
        /// Readable alias for the full-range C# <c>..</c> operator.
        /// Use in slice arguments to keep an entire axis:
        /// <c>ds.V("X1.drain", 1, All)</c> is identical to <c>ds.V("X1.drain", 1, ..)</c>.
        /// </summary>
        public static readonly Range All = Range.All;

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

        // The internal buffer-adopting constructors validate their shape too, for the same reason the
        // public ones do — see ValidateSize. A cube whose axes claim more elements than its buffer
        // holds does not fail where it is BUILT; it fails much later, in Slice's gather, as a bare
        // IndexOutOfRangeException on a stack that names only the reader. Every caller below builds
        // its buffer from the axes it passes, so this only ever fires on a genuine bug — and when it
        // does, the throw lands on the code that made the cube.
        private DataCube(Axis[] axes, Complex[] data, bool noCopy)
        {
            Axes         = axes.ToList().AsReadOnly();
            DataKind     = DataKind.Complex;
            _complexData = noCopy ? data : (Complex[])data.Clone();
            _strides     = ComputeStrides(axes);
            ValidateSize(_strides, axes, _complexData.Length);
        }

        private DataCube(Axis[] axes, double[] data, bool noCopy)
        {
            Axes      = axes.ToList().AsReadOnly();
            DataKind  = DataKind.Real;
            _realData = noCopy ? data : (double[])data.Clone();
            _strides  = ComputeStrides(axes);
            ValidateSize(_strides, axes, _realData.Length);
        }

        // ---- Scalar (0-rank) factory ----------------------------

        /// <summary>Creates a scalar (0-rank) Real DataCube holding a single value.</summary>
        public static DataCube Scalar(double value) =>
            new(Array.Empty<Axis>(), new[] { value }, noCopy: true);

        /// <summary>Creates a scalar (0-rank) Complex DataCube holding a single value.</summary>
        public static DataCube Scalar(Complex value) =>
            new(Array.Empty<Axis>(), new[] { value }, noCopy: true);

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

        /// <summary>
        /// How many elements the backing buffer actually holds, whichever kind it is — WITHOUT
        /// copying it. <see cref="ComplexValues"/>/<see cref="RealValues"/> clone, so asking them
        /// for a length duplicates the whole cube; a diagnostic that runs while something is already
        /// wrong must not do that to a 600-point sweep.
        ///
        /// <para>Exposed for the crash note, and it is the number that matters there: every read
        /// path is validated against the axes product (<see cref="RequireShapeConsistent"/>), so a
        /// report where this disagrees with that product is a report where the validation itself is
        /// not seeing what the gather sees.</para>
        /// </summary>
        public int BufferLength => ElementCount();

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

            // Every constructor validates shape-vs-buffer, so this can only fire for a cube that
            // reached a reader without one. Check anyway, HERE, because the alternative is what the
            // field keeps reporting: the gather walks off the buffer and surfaces as a bare
            // IndexOutOfRangeException on a stack that names only the reader, with nothing in it to
            // identify the cube (src/RfCore/RESOLVED.md). One multiply per slice buys a message that
            // says which shape and how short.
            RequireShapeConsistent();

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

        /// <summary>log₁₀(|z|) element-wise → Real cube.  For real cubes uses log₁₀(|x|).</summary>
        public DataCube Log10()
        {
            var buf = new double[ElementCount()];
            if (DataKind == DataKind.Complex)
                for (int i = 0; i < buf.Length; i++)
                    buf[i] = Math.Log10(_complexData![i].Magnitude + 1e-300);
            else
                for (int i = 0; i < buf.Length; i++)
                    buf[i] = Math.Log10(Math.Abs(_realData![i]) + 1e-300);
            return new DataCube(AxesArray(), buf, noCopy: true);
        }

        /// <summary>ln(|z|) element-wise → Real cube.</summary>
        public DataCube Ln()
        {
            var buf = new double[ElementCount()];
            if (DataKind == DataKind.Complex)
                for (int i = 0; i < buf.Length; i++)
                    buf[i] = Math.Log(_complexData![i].Magnitude + 1e-300);
            else
                for (int i = 0; i < buf.Length; i++)
                    buf[i] = Math.Log(Math.Abs(_realData![i]) + 1e-300);
            return new DataCube(AxesArray(), buf, noCopy: true);
        }

        // ---- Arithmetic operators (element-wise) ---------------

        // Helpers used by operators:

        // RequireSameShape is now superseded by SameShapeByName+broadcast path — kept for reference.
        private static void RequireSameShape(DataCube a, DataCube b)
        {
            if (a.Rank != b.Rank)
                throw new ArgumentException($"Cube rank mismatch: {a.Rank} vs {b.Rank}.");
            for (int d = 0; d < a.Rank; d++)
                if (a.Axes[d].Length != b.Axes[d].Length)
                    throw new ArgumentException(
                        $"Axis {d} length mismatch: {a.Axes[d].Length} vs {b.Axes[d].Length}.");
        }

        private static DataCube MapElements(DataCube a,
            Func<double, double> realOp, Func<Complex, Complex> complexOp)
        {
            int n = a.ElementCount();
            if (a.DataKind == DataKind.Real)
            {
                var buf = new double[n];
                for (int i = 0; i < n; i++) buf[i] = realOp(a._realData![i]);
                return new DataCube(a.AxesArray(), buf, noCopy: true);
            }
            else
            {
                var buf = new Complex[n];
                for (int i = 0; i < n; i++) buf[i] = complexOp(a._complexData![i]);
                return new DataCube(a.AxesArray(), buf, noCopy: true);
            }
        }

        private static DataCube MapToComplex(DataCube a,
            Func<double, Complex> realMap, Func<Complex, Complex> complexMap)
        {
            int n = a.ElementCount();
            var buf = new Complex[n];
            if (a.DataKind == DataKind.Real)
                for (int i = 0; i < n; i++) buf[i] = realMap(a._realData![i]);
            else
                for (int i = 0; i < n; i++) buf[i] = complexMap(a._complexData![i]);
            return new DataCube(a.AxesArray(), buf, noCopy: true);
        }

        // Fast path: operands have identical axes by name+order+length — byte-identical to the old behavior.
        private static DataCube ZipIdentical(DataCube a, DataCube b,
            Func<double, double, double> realOp, Func<Complex, Complex, Complex> complexOp)
        {
            int n = a.ElementCount();
            if (a.DataKind == DataKind.Real && b.DataKind == DataKind.Real)
            {
                var buf = new double[n];
                for (int i = 0; i < n; i++) buf[i] = realOp(a._realData![i], b._realData![i]);
                return new DataCube(a.AxesArray(), buf, noCopy: true);
            }
            var cbuf = new Complex[n];
            for (int i = 0; i < n; i++)
            {
                var ca = a.DataKind == DataKind.Complex ? a._complexData![i] : new Complex(a._realData![i], 0);
                var cb = b.DataKind == DataKind.Complex ? b._complexData![i] : new Complex(b._realData![i], 0);
                cbuf[i] = complexOp(ca, cb);
            }
            return new DataCube(a.AxesArray(), cbuf, noCopy: true);
        }

        private static DataCube ElementWise(DataCube a, DataCube b,
            Func<double, double, double> realOp, Func<Complex, Complex, Complex> complexOp)
        {
            // Fast path: identical axes by name+order+length → existing tight zip (byte-identical result).
            if (SameShapeByName(a, b))
                return ZipIdentical(a, b, realOp, complexOp);

            // Broadcast path: align by axis name; union axis-set; replicate across missing axes.
            Axis[] axes  = UnionAxes(a, b);   // throws on incompatible shared axis
            int[]  rstr  = ComputeStrides(axes);
            int    rank  = axes.Length;
            int    total = 1; foreach (var ax in axes) total *= ax.Length;
            int[]  posA  = MapPositions(a, axes);  // posA[e] = index of a.Axes[e] within `axes`
            int[]  posB  = MapPositions(b, axes);
            bool   cplx  = a.DataKind == DataKind.Complex || b.DataKind == DataKind.Complex;
            var    idx   = new int[rank];

            if (!cplx)
            {
                var buf = new double[total];
                for (int f = 0; f < total; f++)
                {
                    BroadcastDecode(f, rstr, idx);
                    buf[f] = realOp(a._realData![BroadcastOperandFlat(a._strides, posA, idx)],
                                    b._realData![BroadcastOperandFlat(b._strides, posB, idx)]);
                }
                return new DataCube(axes, buf, noCopy: true);
            }
            else
            {
                var buf = new Complex[total];
                for (int f = 0; f < total; f++)
                {
                    BroadcastDecode(f, rstr, idx);
                    int ia = BroadcastOperandFlat(a._strides, posA, idx);
                    int ib = BroadcastOperandFlat(b._strides, posB, idx);
                    var ca = a.DataKind == DataKind.Complex ? a._complexData![ia] : new Complex(a._realData![ia], 0);
                    var cb = b.DataKind == DataKind.Complex ? b._complexData![ib] : new Complex(b._realData![ib], 0);
                    buf[f] = complexOp(ca, cb);
                }
                return new DataCube(axes, buf, noCopy: true);
            }
        }

        private static bool SameShapeByName(DataCube a, DataCube b)
        {
            if (a.Rank != b.Rank) return false;
            for (int d = 0; d < a.Rank; d++)
                if (a.Axes[d].Name != b.Axes[d].Name || a.Axes[d].Length != b.Axes[d].Length) return false;
            return true;
        }

        // Result = higher-rank operand's axes, plus any axis from the lower-rank operand not present by name.
        // Shared axes must agree in length and coordinates (same sweep provenance).
        private static Axis[] UnionAxes(DataCube a, DataCube b)
        {
            var (big, small) = a.Rank >= b.Rank ? (a, b) : (b, a);
            var result = new List<Axis>(big.Axes);
            foreach (var sx in small.Axes)
            {
                int j = result.FindIndex(ax => ax.Name == sx.Name);
                if (j < 0) { result.Add(sx); continue; }
                if (result[j].Length != sx.Length)
                    throw new ArgumentException(
                        $"Cannot align axis '{sx.Name}': lengths {result[j].Length} vs {sx.Length}.");
                for (int k = 0; k < sx.Length; k++)
                    if (Math.Abs(result[j].Values[k] - sx.Values[k]) > 1e-12 * (1 + Math.Abs(sx.Values[k])))
                        throw new ArgumentException($"Cannot align axis '{sx.Name}': differing coordinates.");
            }
            return result.ToArray();
        }

        private static int[] MapPositions(DataCube op, Axis[] resultAxes)
        {
            var pos = new int[op.Rank];
            for (int e = 0; e < op.Rank; e++)
                pos[e] = Array.FindIndex(resultAxes, ax => ax.Name == op.Axes[e].Name);
            return pos;
        }

        private static void BroadcastDecode(int flat, int[] strides, int[] idx)
        {
            int rem = flat;
            for (int d = 0; d < strides.Length; d++) { idx[d] = rem / strides[d]; rem %= strides[d]; }
        }

        // Sum over the operand's own axes; axes it lacks never contribute → natural replication.
        private static int BroadcastOperandFlat(int[] opStrides, int[] posInResult, int[] resultIdx)
        {
            int f = 0;
            for (int e = 0; e < opStrides.Length; e++) f += resultIdx[posInResult[e]] * opStrides[e];
            return f;
        }

        // Cube × Cube
        public static DataCube operator +(DataCube a, DataCube b) => ElementWise(a, b, (x, y) => x + y, (x, y) => x + y);
        public static DataCube operator -(DataCube a, DataCube b) => ElementWise(a, b, (x, y) => x - y, (x, y) => x - y);
        public static DataCube operator *(DataCube a, DataCube b) => ElementWise(a, b, (x, y) => x * y, (x, y) => x * y);
        public static DataCube operator /(DataCube a, DataCube b) => ElementWise(a, b, (x, y) => x / y, (x, y) => x / y);
        public static DataCube operator -(DataCube a)             => MapElements(a, x => -x, x => -x);

        // Cube × double (scalar broadcast; result kind same as cube)
        public static DataCube operator +(DataCube a, double s) => MapElements(a, x => x + s, x => x + s);
        public static DataCube operator +(double s, DataCube a) => MapElements(a, x => s + x, x => s + x);
        public static DataCube operator -(DataCube a, double s) => MapElements(a, x => x - s, x => x - s);
        public static DataCube operator -(double s, DataCube a) => MapElements(a, x => s - x, x => new Complex(s, 0) - x);
        public static DataCube operator *(DataCube a, double s) => MapElements(a, x => x * s, x => x * s);
        public static DataCube operator *(double s, DataCube a) => MapElements(a, x => s * x, x => s * x);
        public static DataCube operator /(DataCube a, double s) => MapElements(a, x => x / s, x => x / s);
        public static DataCube operator /(double s, DataCube a) => MapElements(a, x => s / x, x => new Complex(s, 0) / x);

        // Cube × Complex (always Complex result)
        public static DataCube operator +(DataCube a, Complex s) => MapToComplex(a, x => x + s, x => x + s);
        public static DataCube operator +(Complex s, DataCube a) => MapToComplex(a, x => s + x, x => s + x);
        public static DataCube operator -(DataCube a, Complex s) => MapToComplex(a, x => x - s, x => x - s);
        public static DataCube operator -(Complex s, DataCube a) => MapToComplex(a, x => s - x, x => s - x);
        public static DataCube operator *(DataCube a, Complex s) => MapToComplex(a, x => new Complex(x, 0) * s, x => x * s);
        public static DataCube operator *(Complex s, DataCube a) => MapToComplex(a, x => s * x, x => s * x);
        public static DataCube operator /(DataCube a, Complex s) => MapToComplex(a, x => new Complex(x, 0) / s, x => x / s);
        public static DataCube operator /(Complex s, DataCube a) => MapToComplex(a, x => s / x, x => s / x);

        // ---- Reductions ----------------------------------------

        public DataCube Max(string axisName) => Reduce(axisName, (a, b) => a > b, isMin: false);
        public DataCube Min(string axisName) => Reduce(axisName, (a, b) => a < b, isMin: true);

        public DataCube Peak(string axisName)
        {
            if (DataKind != DataKind.Real)
                throw new InvalidOperationException("Peak requires a Real cube — call .Mag() first.");
            return Reduce(axisName, (a, b) => Math.Abs(a) > Math.Abs(b), isMin: false);
        }

        /// <summary>
        /// Pins one axis BY NAME to a single index and keeps every other axis — the shape-independent
        /// counterpart to a positional slice. <c>c.At("Pin", 0)</c> on <c>[RFfreq, Pin]</c> yields
        /// <c>[RFfreq]</c>, which then broadcasts back against the original by axis name (that is what
        /// makes a per-frequency reference value expressible at all).
        /// <para><paramref name="index"/> may be NEGATIVE, counting from the end: −1 is the last
        /// point, −2 the one before it. "Referenced to the top of the sweep" is the same idiom as
        /// "referenced to the start", and a caller should not have to know the length to say it.</para>
        /// <para>Pinning the only axis leaves a RANK-0 cube, not a bare element — this used to
        /// dereference a null <c>SliceResult.Cube</c> and throw a NullReferenceException, which is
        /// exactly the no-sweep case a shape-independent expression hits first.</para>
        /// </summary>
        public DataCube At(string axisName, int index)
        {
            int dim = AxisIndex(axisName);
            int len = Axes[dim].Length;
            int i   = index < 0 ? len + index : index;
            if (i < 0 || i >= len)
                throw new ArgumentOutOfRangeException(nameof(index),
                    $"Index {index} is outside axis '{axisName}' (length {len}): " +
                    $"use 0..{len - 1}, or -1..-{len} from the end.");

            var args = new object[Rank];
            for (int d = 0; d < Rank; d++)
                args[d] = d == dim ? (object)i : (object)(..);
            var result = Slice(args);
            if (result.IsCube) return result.Cube!;
            return result.IsComplex ? Scalar(result.ComplexValue!.Value)
                                    : Scalar(result.RealValue!.Value);
        }

        // ---- Stacking -------------------------------------------

        /// <summary>
        /// Stack <paramref name="cubes"/> along a new prepended axis.
        /// All cubes must have the same shape (rank + axis lengths) and DataKind.
        /// <paramref name="newAxis"/>.Length must equal cubes.Count.
        /// Produces a cube of shape [N, d₀, d₁, …] from N cubes of shape [d₀, d₁, …].
        /// Scalar (rank-0) cubes produce a rank-1 result.
        /// </summary>
        public static DataCube PrependAxis(Axis newAxis, IReadOnlyList<DataCube> cubes)
        {
            if (cubes.Count == 0)
                throw new ArgumentException("At least one cube is required.", nameof(cubes));
            if (newAxis.Length != cubes.Count)
                throw new ArgumentException(
                    $"New axis length ({newAxis.Length}) must match cube count ({cubes.Count}).");

            var first = cubes[0];
            for (int n = 1; n < cubes.Count; n++)
            {
                if (cubes[n].DataKind != first.DataKind)
                    throw new ArgumentException($"Cube [{n}] DataKind mismatch.");
                if (cubes[n].Rank != first.Rank)
                    throw new ArgumentException($"Cube [{n}] rank mismatch.");
                for (int d = 0; d < first.Rank; d++)
                    if (cubes[n].Axes[d].Length != first.Axes[d].Length)
                        throw new ArgumentException(
                            $"Cube [{n}] axis {d} length {cubes[n].Axes[d].Length} " +
                            $"!= {first.Axes[d].Length}.");
            }

            int chunkSize = first.Rank == 0 ? 1
                : first._strides[0] * first.Axes[0].Length;

            var axes = new Axis[1 + first.Rank];
            axes[0] = newAxis;
            for (int d = 0; d < first.Rank; d++) axes[1 + d] = first.Axes[d];

            if (first.DataKind == DataKind.Complex)
            {
                var data = new Complex[cubes.Count * chunkSize];
                for (int n = 0; n < cubes.Count; n++)
                    Array.Copy(cubes[n]._complexData!, 0, data, n * chunkSize, chunkSize);
                return new DataCube(axes, data, noCopy: true);
            }
            else
            {
                var data = new double[cubes.Count * chunkSize];
                for (int n = 0; n < cubes.Count; n++)
                    Array.Copy(cubes[n]._realData!, 0, data, n * chunkSize, chunkSize);
                return new DataCube(axes, data, noCopy: true);
            }
        }

        // ---- Helpers -------------------------------------------

        private int ElementCount() =>
            DataKind == DataKind.Complex ? _complexData!.Length : _realData!.Length;

        /// <summary>
        /// The read-side half of <see cref="ValidateSize"/> — same arithmetic, applied to the cube's
        /// own state rather than to constructor arguments. See <see cref="Slice"/> for why it exists.
        /// </summary>
        private void RequireShapeConsistent()
        {
            int expected = Rank == 0 ? 1 : _strides[0] * Axes[0].Length;
            int actual   = ElementCount();
            if (actual == expected) return;

            string shape = Rank == 0
                ? "scalar"
                : string.Join(" x ", Axes.Select(a => $"{a.Name}[{a.Length}]"));
            throw new InvalidOperationException(
                $"Malformed cube: axes {shape} claim {expected} elements, buffer holds {actual}.");
        }

        private Axis[] AxesArray() => Axes.ToArray();

        private int AxisIndex(string name)
        {
            for (int d = 0; d < Rank; d++)
                if (Axes[d].Name == name) return d;
            string available = Rank == 0
                ? "this value has no axes (it is a single point)"
                : $"available axes: {string.Join(", ", Axes.Select(a => a.Name))}";
            throw new ArgumentException($"No axis named '{name}' — {available}.");
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
            // Empty-product for rank-0 (scalar) is 1, not 0.
            int expected = axes.Length == 0 ? 1 : strides[0] * axes[0].Length;
            if (dataLength == expected) return;

            // Name the shape, not just the two numbers: this message is the whole value of validating
            // here rather than letting the gather run off the buffer later, so it has to identify WHICH
            // cube is malformed when it turns up in a crash report.
            string shape = axes.Length == 0
                ? "scalar"
                : string.Join(" x ", axes.Select(a => $"{a.Name}[{a.Length}]"));
            throw new ArgumentException(
                $"Data length {dataLength} does not match axes shape {expected} ({shape}).");
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

        // ---- Variation check -------------------------------------------

        /// <summary>
        /// Returns true when the cube contains at least two values that differ
        /// by more than <paramref name="epsilon"/> relative to the data scale,
        /// i.e. the field carries information worth plotting.
        /// NaN-only or empty cubes return false.
        /// For Complex cubes the magnitude is used.
        /// </summary>
        public static bool CubeVaries(DataCube cube, double epsilon = 1e-9)
        {
            if (cube.DataKind == DataKind.Real)
            {
                var data = cube._realData;
                if (data == null || data.Length == 0) return false;

                double min = double.MaxValue, max = double.MinValue;
                foreach (double v in data)
                {
                    if (double.IsNaN(v)) continue;
                    if (v < min) min = v;
                    if (v > max) max = v;
                    // Early-out: clearly varies.
                    if ((max - min) > epsilon * Math.Max(1.0, Math.Abs(max))) return true;
                }
                if (max == double.MinValue) return false;  // all NaN
                return (max - min) > epsilon * Math.Max(1.0, Math.Abs(max));
            }
            else
            {
                var data = cube._complexData;
                if (data == null || data.Length == 0) return false;

                double min = double.MaxValue, max = double.MinValue;
                foreach (var v in data)
                {
                    double m = v.Magnitude;
                    if (double.IsNaN(m)) continue;
                    if (m < min) min = m;
                    if (m > max) max = m;
                    if ((max - min) > epsilon * Math.Max(1.0, max)) return true;
                }
                if (max == double.MinValue) return false;
                return (max - min) > epsilon * Math.Max(1.0, max);
            }
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
