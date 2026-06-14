// ================================================================
//  Trace.cs  —  Trace model  (pure data + logic, no drawing)
//
//  Ported from splotRF/src/Models/Trace.cs — namespace renamed to
//  CircuitRF.Ui.DataDisplay.  Trace.Data stays as SNP (DataSet/
//  DataCube retarget is Phase 7.2).
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Avalonia;
using NumFlat;
using RfCore;

namespace CircuitRF.Ui.DataDisplay
{
    // ============================================================
    //  DependentVarFormat
    // ============================================================

    public enum DependentVarFormat
    {
        Complex, Db, Mag, Phase, Real, Imaginary
    }

    public static class DependentVarFormatExtensions
    {
        public static string Description(this DependentVarFormat f) => f switch
        {
            DependentVarFormat.Complex   => "complex",
            DependentVarFormat.Db        => "dB",
            DependentVarFormat.Mag       => "mag",
            DependentVarFormat.Phase     => "phase",
            DependentVarFormat.Real      => "real",
            DependentVarFormat.Imaginary => "imaginary",
            _                            => f.ToString()
        };
    }

    // ============================================================
    //  DerivedParameters
    // ============================================================

    public enum DerivedParameters
    {
        None, SourceStabilityCircle, LoadStabilityCircle, MaxGain, Mu, MuPrime,
    }

    public static class DerivedParametersExtensions
    {
        public static string Description(this DerivedParameters d) => d switch
        {
            DerivedParameters.SourceStabilityCircle => "Source Stability Circles",
            DerivedParameters.LoadStabilityCircle   => "Load Stability Circles",
            DerivedParameters.MuPrime               => "Source Stability, µ'",
            DerivedParameters.Mu                    => "Load Stability, µ",
            DerivedParameters.MaxGain               => "Max Gain",
            _                                       => ""
        };
    }

    // ============================================================
    //  Trace
    // ============================================================

    public class Trace
    {
        // ---- Identity ---------------------------------------------------

        public Guid Id { get; set; } = Guid.NewGuid();

        // ---- Data source ------------------------------------------------

        private SNP _data = new SNP(new double[] { 1e9 }, 2);
        public SNP Data
        {
            get => _data;
            set => _data = value;
        }

        // ---- Matrix selectors -------------------------------------------

        private int _row;
        public int Row
        {
            get => _row;
            set { if (value >= 0 && value < Data.Ports) _row = value; }
        }

        private int _col;
        public int Col
        {
            get => _col;
            set { if (value >= 0 && value < Data.Ports) _col = value; }
        }

        // ---- Reference impedance ----------------------------------------

        private Complex _z0 = new Complex(50, 0);
        public Complex Z0
        {
            get => _z0;
            set => _z0 = value;
        }

        // ---- Matrix type ------------------------------------------------

        private MatrixType _matrixType = MatrixType.S;
        public MatrixType MatrixType
        {
            get => _matrixType;
            set => _matrixType = value;
        }

        // ---- Y-axis format ----------------------------------------------

        private DependentVarFormat _yAxis = DependentVarFormat.Complex;
        public DependentVarFormat YAxis
        {
            get => _yAxis;
            set => _yAxis = value;
        }

        // ---- Derived parameter ------------------------------------------

        private DerivedParameters _derived = DerivedParameters.None;
        public DerivedParameters Derived
        {
            get => _derived;
            set
            {
                _derived = value;
                if (IsDerived)
                {
                    _row        = 0;
                    _col        = 0;
                    _matrixType = MatrixType.S;
                    _yAxis      = IsStabilityCircle
                        ? DependentVarFormat.Complex
                        : value == DerivedParameters.MaxGain
                            ? DependentVarFormat.Db
                            : DependentVarFormat.Mag;
                    if (StabilityCircleCentres.Count == 0 && Markers.Count > 0) BuildDerivedPath(PlotType.Smith, FreqUnit.GHz);
                    foreach (var m in Markers)
                    {
                        m.PositionStatic = new System.Numerics.Vector2(0, 0);
                        int fi = Array.FindIndex(Data.Frequencies, f => f == m.Freq - 1e-6);
                        if (fi < 0) fi = Data.Frequencies.Length - 1;
                        SnapMarkerToStabilityCircle(m, fi);
                    }
                }
            }
        }

        public bool IsDerived => Derived != DerivedParameters.None;

        public bool IsStabilityCircle =>
            Derived == DerivedParameters.LoadStabilityCircle ||
            Derived == DerivedParameters.SourceStabilityCircle;

        // ---- Secondary axis ---------------------------------------------

        private bool _useSecondaryAxis;
        public bool UseSecondaryAxis
        {
            get => _useSecondaryAxis;
            set => _useSecondaryAxis = value;
        }

        // ---- Display / serialisation properties -------------------------

        public MatrixFormat    MatrixFormat          { get; set; } = MatrixFormat.MA;
        public TraceProperties Properties            { get; set; } = new TraceProperties();
        public double          ColumnWidth           { get; set; } = 115;
        public PrecisionFormat FormatString          { get; set; } = PrecisionFormat.F;
        public int             MaximumFractionDigits { get; set; } = 3;

        /// <summary>Optional URL of the source file (for display / reload).</summary>
        public string? SourcePath { get; set; }

        // ---- Markers ----------------------------------------------------

        public List<Marker> Markers { get; } = new();

        // ---- Pre-built geometry (world coordinates) ---------------------

        public List<Vector2> Points                     { get; private set; } = new();
        public List<Vector2> StabilityCircleCentres     { get; private set; } = new();
        public List<double>  StabilityCircleRadii       { get; private set; } = new();
        public List<bool>    StabilityCircleStableInside { get; private set; } = new();

        // ---- Frequency range --------------------------------------------

        public double MinFreq => Data.Frequencies.Length > 0 ? Data.Frequencies.Min() : double.NaN;
        public double MaxFreq => Data.Frequencies.Length > 0 ? Data.Frequencies.Max() : double.NaN;

        // ---- Description string -----------------------------------------

        /// <summary>Full description including the source-file prefix.</summary>
        public string Description => DescriptionFor(includePrefix: true);

        /// <summary>Short description with no source-file prefix.</summary>
        public string ShortDescription => DescriptionFor(includePrefix: false);

        private string DescriptionFor(bool includePrefix)
        {
            string prefix = includePrefix && SourcePath != null
                ? System.IO.Path.GetFileNameWithoutExtension(SourcePath) + ".."
                : "";

            if (IsDerived)
                return Derived == DerivedParameters.MaxGain && YAxis == DependentVarFormat.Db
                    ? $"{prefix}dB({Derived.Description()})"
                    : $"{prefix}{Derived.Description()}";

            string el = $"({Row + 1},{Col + 1})";
            return YAxis switch
            {
                DependentVarFormat.Db        => $"{prefix}dB({MatrixType}{el})",
                DependentVarFormat.Mag       => $"{prefix}mag({MatrixType}{el})",
                DependentVarFormat.Phase     => $"{prefix}phase({MatrixType}{el})",
                DependentVarFormat.Complex   => $"{prefix}{MatrixType}{el}",
                DependentVarFormat.Real      => $"{prefix}real({MatrixType}{el})",
                DependentVarFormat.Imaginary => $"{prefix}imag({MatrixType}{el})",
                _                            => $"{prefix}{MatrixType}{el}"
            };
        }

        // ---- Constructors -----------------------------------------------

        public Trace(
            SNP                data,
            MatrixType         matrixType,
            int                row,
            int                col,
            DependentVarFormat yAxis,
            bool               secondaryAxis = false,
            TraceProperties?   properties    = null)
        {
            _data             = data;
            _matrixType       = matrixType;
            _yAxis            = yAxis;
            _row              = row;
            _col              = col;
            _useSecondaryAxis = secondaryAxis;
            Properties        = properties ?? new TraceProperties();
            _z0               = new Complex(data.Z0.Real, data.Z0.Imaginary);
        }

        public Trace(Trace src, int incrementColorBy = 0, bool includeMarkers = true)
        {
            _data             = src.Data;
            _matrixType       = src.MatrixType;
            _yAxis            = src.YAxis;
            _row              = src.Row;
            _col              = src.Col;
            _useSecondaryAxis = src.UseSecondaryAxis;
            _derived          = src.Derived;
            Properties        = new TraceProperties(src.Properties, incrementColorBy);
            _z0               = src.Z0;
            SourcePath        = src.SourcePath;
            ColumnWidth       = src.ColumnWidth;
            if (includeMarkers)
                foreach (var m in src.Markers)
                    Markers.Add(new Marker(m));
        }

        // ---- Path building ----------------------------------------------

        public void BuildPath(PlotType plotType, FreqUnit freqUnit)
        {
            if (IsDerived) BuildDerivedPath(plotType, freqUnit);
            else           BuildMatrixPath(plotType, freqUnit);
        }

        private void BuildMatrixPath(PlotType plotType, FreqUnit freqUnit)
        {
            Points.Clear();
            StabilityCircleCentres.Clear();
            StabilityCircleRadii.Clear();

            if (Row >= Data.Ports || Col >= Data.Ports) return;

            var z0Array = RFNetwork.Z0Array(_z0, Data.Ports);

            for (int fi = 0; fi < Data.FrequencyCount; fi++)
            {
                Complex raw;

                switch (MatrixType)
                {
                    case MatrixType.S:
                    {
                        var mat = Data.Matrices[fi];
                        if (_z0 != Data.Z0)
                            mat = RFNetwork.SToS(mat, Data.Z0, z0Array);
                        raw = mat[Row, Col];
                        break;
                    }
                    case MatrixType.Z:
                        raw = RFNetwork.SToZ(Data.Matrices[fi], Data.Z0)[Row, Col];
                        break;
                    default: // Y
                        raw = RFNetwork.SToY(Data.Matrices[fi], Data.Z0)[Row, Col];
                        break;
                }

                float x, y;

                if (plotType.IsRect())
                {
                    x = (float)(Data.Frequencies[fi] * freqUnit.Scale());
                    y = (float)(YAxis switch
                    {
                        DependentVarFormat.Mag       => raw.Magnitude,
                        DependentVarFormat.Phase     => raw.Phase * 180.0 / Math.PI,
                        DependentVarFormat.Real      => raw.Real,
                        DependentVarFormat.Imaginary => raw.Imaginary,
                        DependentVarFormat.Db        => 20.0 * Math.Log10(Math.Max(raw.Magnitude, 1e-300)),
                        _                            => raw.Magnitude
                    });
                }
                else // Smith / Polar
                {
                    x = (float)raw.Real;
                    y = (float)raw.Imaginary;
                }

                if (!float.IsFinite(y)) continue;
                Points.Add(new Vector2(x, y));
            }
        }

        private void BuildDerivedPath(PlotType plotType, FreqUnit freqUnit)
        {
            Points.Clear();
            StabilityCircleCentres.Clear();
            StabilityCircleRadii.Clear();
            StabilityCircleStableInside.Clear();

            if (Data.Ports != 2) return;

            var snp = new SNP(Data.Frequencies, Data.Matrices,
                              MatrixType.S, Data.Format, Data.Z0);

            if (plotType.IsRect())
            {
                double[] xData = Data.Frequencies
                    .Select(f => f * freqUnit.Scale()).ToArray();

                double[] yData = Derived switch
                {
                    DerivedParameters.MuPrime => RFNetwork.StabilityMuPrime(snp),
                    DerivedParameters.Mu      => RFNetwork.StabilityMu(snp),
                    DerivedParameters.MaxGain => RFNetwork.MaxGain(snp),
                    _                         => Array.Empty<double>()
                };

                for (int i = 0; i < xData.Length && i < yData.Length; i++)
                    if (double.IsFinite(yData[i]))
                        Points.Add(new Vector2((float)xData[i], (float)yData[i]));
            }
            else
            {
                if (Derived == DerivedParameters.LoadStabilityCircle)
                {
                    var (CL, rL) = RFNetwork.StabilityCirclesLoad(snp);
                    bool[] inside = RFNetwork.StableRegionInsideLoad(snp);
                    for (int i = 0; i < CL.Length; i++)
                    {
                        StabilityCircleCentres.Add(new Vector2((float)CL[i].Real, (float)CL[i].Imaginary));
                        StabilityCircleRadii.Add(rL[i]);
                        StabilityCircleStableInside.Add(inside[i]);
                    }
                }
                else if (Derived == DerivedParameters.SourceStabilityCircle)
                {
                    var (CS, rS) = RFNetwork.StabilityCirclesSource(snp);
                    bool[] inside = RFNetwork.StableRegionInsideSource(snp);
                    for (int i = 0; i < CS.Length; i++)
                    {
                        StabilityCircleCentres.Add(new Vector2((float)CS[i].Real, (float)CS[i].Imaginary));
                        StabilityCircleRadii.Add(rS[i]);
                        StabilityCircleStableInside.Add(inside[i]);
                    }
                }
            }
        }

        // ---- Bounding rect in world coords (used by autoscale) ----------

        public Rect PathBoundingRect()
        {
            if (Points.Count == 0) return default;
            float minX = Points.Min(p => p.X), maxX = Points.Max(p => p.X);
            float minY = Points.Min(p => p.Y), maxY = Points.Max(p => p.Y);
            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }

        // ---- Data retrieval ---------------------------------------------

        public Complex DataPoint(double freq, Complex? z0Override = null)
        {
            int fi = Array.FindIndex(Data.Frequencies, f => f == freq);
            if (fi < 0) return new Complex(double.NaN, double.NaN);

            if (IsDerived)
            {
                var snp = new SNP(Data.Frequencies, Data.Matrices,
                                  MatrixType.S, Data.Format, Data.Z0);
                double v = Derived switch
                {
                    DerivedParameters.Mu      => RFNetwork.StabilityMu(snp)[fi],
                    DerivedParameters.MuPrime => RFNetwork.StabilityMuPrime(snp)[fi],
                    DerivedParameters.MaxGain => RFNetwork.MaxGain(snp)[fi],
                    _                         => double.NaN
                };
                return new Complex(v, 0);
            }

            var z0  = z0Override ?? _z0;
            var mat = Data.Matrices[fi];

            if (MatrixType == MatrixType.S && z0 != Data.Z0)
                mat = RFNetwork.SToS(mat, Data.Z0, z0);
            else if (MatrixType == MatrixType.Z)
                mat = RFNetwork.SToZ(Data.Matrices[fi], Data.Z0);
            else if (MatrixType == MatrixType.Y)
                mat = RFNetwork.SToY(Data.Matrices[fi], Data.Z0);

            return mat[Row, Col];
        }

        public double DataPointScalar(double freq, Complex? z0Override = null)
        {
            var d = DataPoint(freq, z0Override);
            return YAxis switch
            {
                DependentVarFormat.Db        => 20.0 * Math.Log10(Math.Max(d.Magnitude, 1e-300)),
                DependentVarFormat.Imaginary => d.Imaginary,
                DependentVarFormat.Mag       => d.Magnitude,
                DependentVarFormat.Phase     => d.Phase * 180.0 / Math.PI,
                DependentVarFormat.Real      => d.Real,
                _                           => double.NaN
            };
        }

        // ---- Nearest-point search ----------------------------------------

        public (int FreqIndex, double Distance, Vector2 NearestPoint)?
            FindNearestTraceData(Vector2 queryPt)
        {
            if (IsStabilityCircle)
            {
                double  best    = double.PositiveInfinity;
                int     bestIdx = -1;
                Vector2 bestPt  = default;

                for (int i = 0; i < StabilityCircleCentres.Count; i++)
                {
                    float rPx = Math.Abs((float)StabilityCircleRadii[i]);
                    float dx  = queryPt.X - StabilityCircleCentres[i].X;
                    float dy  = queryPt.Y - StabilityCircleCentres[i].Y;
                    float dc  = MathF.Sqrt(dx * dx + dy * dy);
                    if (dc < 1e-6f) continue;

                    var   nearPt = new Vector2(StabilityCircleCentres[i].X + dx / dc * rPx,
                                               StabilityCircleCentres[i].Y + dy / dc * rPx);
                    double dist = Dist(queryPt, nearPt);
                    if (dist < best)
                    {
                        best    = dist;
                        bestIdx = i;
                        bestPt  = nearPt;
                    }
                }
                return bestIdx < 0 ? null : (bestIdx, best, bestPt);
            }
            else
            {
                if (Points.Count == 0) return null;
                double best    = double.PositiveInfinity;
                int    bestIdx = -1;

                if (YAxis == DependentVarFormat.Complex)
                {
                    for (int i = 0; i < Points.Count; i++)
                    {
                        double d = Dist(queryPt, Points[i]);
                        if (d < best) { best = d; bestIdx = i; }
                    }
                }
                else
                {
                    for (int i = 0; i < Points.Count; i++)
                    {
                        double d = Math.Abs(queryPt.X - Points[i].X);
                        if (d < best) { best = d; bestIdx = i; }
                    }
                }
                return (bestIdx, best, Points[bestIdx]);
            }
        }

        // ---- Stability-circle: nearest perimeter point ------------------

        public Vector2? FindNearestPointOnStabilityCircle(Vector2 queryWorld, int freqIndex)
        {
            if (!IsStabilityCircle || freqIndex >= StabilityCircleCentres.Count)
                return null;
            var   C  = StabilityCircleCentres[freqIndex];
            float r  = MathF.Abs((float)StabilityCircleRadii[freqIndex]);
            float dx = queryWorld.X - C.X;
            float dy = queryWorld.Y - C.Y;
            float dc = MathF.Sqrt(dx * dx + dy * dy);
            if (dc < 1e-9f) return null;
            return new Vector2(C.X + dx / dc * r, C.Y + dy / dc * r);
        }

        // ---- Safe bulk data assignment ----------------------------------

        public void SetData(SNP data, int row, int col, DerivedParameters derived,
                            PlotType plotType, FreqUnit freqUnit)
        {
            if (row >= data.Ports || col >= data.Ports) return;
            _row     = 0; _col = 0;
            _data    = data;
            _row     = row; _col = col;
            _derived = data.Ports != 2 ? DerivedParameters.None : derived;
            BuildPath(plotType, freqUnit);
        }

        // ---- Copy data as text ------------------------------------------

        public string CopyDataString(FreqUnit freqUnit, double[]? freq = null, string fmt = "G12")
        {
            var allFreq = freq ?? Data.Frequencies;
            var sb = new StringBuilder();
            sb.AppendLine($"freq ({freqUnit})\t{Description}");
            foreach (var f in allFreq)
            {
                sb.Append((f * freqUnit.Scale()).ToString(fmt));
                sb.Append('\t');
                sb.AppendLine(DataPoint(f).ToString());
            }
            return sb.ToString();
        }

        // ---- Marker-data methods ----------------------------------------

        public Vector2 GetMarkerDataLocation(Marker m)
        {
            if (IsStabilityCircle) return m.PositionStatic;
            int fi = Array.FindIndex(Data.Frequencies, f => f >= m.Freq - 1e-6);
            if (fi < 0) fi = Data.Frequencies.Length - 1;
            if (fi >= 0 && fi < Points.Count) return Points[fi];
            return Vector2.Zero;
        }

        public Complex GetMarkerDataPoint(Marker m)
        {
            if (IsStabilityCircle)
                return new Complex(m.PositionStatic.X, m.PositionStatic.Y);

            int fi = Array.FindIndex(Data.Frequencies, f => f == m.Freq);
            if (fi < 0) return new Complex(double.NaN, double.NaN);

            var mat   = Data.Matrices[fi];

            if (Data.Type != MatrixType || Z0 != Data.Z0)
                mat = RFNetwork.Convert(mat, Data.Type, Data.Z0, MatrixType, Z0);

            if (IsDerived)
            {
                double v = Derived switch
                {
                    DerivedParameters.Mu      => RFNetwork.StabilityMu(mat),
                    DerivedParameters.MuPrime => RFNetwork.StabilityMuPrime(mat),
                    DerivedParameters.MaxGain => RFNetwork.MaxGain(mat),
                    _                         => double.NaN
                };
                return new Complex(v, 0);
            }

            return mat[Row, Col];
        }

        public string FormatScalarValue(double val, Marker m)
        {
            if (!double.IsFinite(val)) return "NaN";
            string fmt = $"{m.FormatString}{m.MaximumFractionDigits}";
            return YAxis switch
            {
                DependentVarFormat.Db    => $"{val.ToString(fmt)} dB",
                DependentVarFormat.Phase => $"{val.ToString(fmt)}°",
                _                        => val.ToString(fmt),
            };
        }

        public string GetMarkerValString(Marker m, bool showFilePrefix = true)
        {
            string suffix = IsStabilityCircle ? " Γ" : "";
            string desc   = showFilePrefix ? Description : ShortDescription;

            if (YAxis == DependentVarFormat.Complex)
                return $"{desc}{suffix}={m.FormatComplex(GetMarkerDataPoint(m))}";

            double scalar = DataPointScalar(m.Freq);
            return $"{desc}={FormatScalarValue(scalar, m)}";
        }

        public string GetMultiMarkerLine(Marker m, Trace other)
        {
            if (m.IsDelta)
            {
                double ownVal   = DataPointScalar(m.Freq);
                double otherVal = other.DataPointScalar(m.Freq);
                double delta    = otherVal - ownVal;
                string valStr   = double.IsFinite(delta)
                    ? other.FormatScalarValue(delta, m)
                    : "NaN";
                return $"  Δ{other.ShortDescription}={valStr}";
            }

            if (other.YAxis == DependentVarFormat.Complex)
            {
                var    dp     = other.GetMarkerDataPoint(m);
                string valStr = double.IsNaN(dp.Real) ? "NaN" : m.FormatComplex(dp);
                return $"{other.ShortDescription}={valStr}";
            }

            double scalar = other.DataPointScalar(m.Freq);
            return $"{other.ShortDescription}={other.FormatScalarValue(scalar, m)}";
        }

        public string MuString(Marker m)
        {
            string fmt = $"{m.FormatString.ToString()}{m.MaximumFractionDigits}";
            int fi = Array.FindIndex(Data.Frequencies, f => f == m.Freq);
            if (fi < 0) return "Stability=NaN";
            switch (Derived)
            {
                case DerivedParameters.LoadStabilityCircle or DerivedParameters.Mu:
                    var val = RFNetwork.StabilityMu(Data);
                    return "Load Stability, µ=" + val[fi].ToString(fmt);
                case DerivedParameters.SourceStabilityCircle or DerivedParameters.MuPrime:
                    val = RFNetwork.StabilityMuPrime(Data);
                    return "Source Stability, µ'=" + val[fi].ToString(fmt);
            }
            return "";
        }

        public bool MarkerShowsImpedance(Marker m) =>
            !m.IsMulti && Row == Col && YAxis == DependentVarFormat.Complex;

        public string GetMarkerImpedanceString(Marker m)
        {
            int fi = Array.FindIndex(Data.Frequencies, f => f == m.Freq);
            if (fi < 0) return "impedance=NaN";

            Complex s = new Complex(m.PositionStatic.X, m.PositionStatic.Y);

            if (!IsDerived)
            {
                Mat<Complex> temp = RFNetwork.Convert(Data.Matrices[fi], Data.Type, Data.Z0, MatrixType, Z0);
                s = temp[Row, Col];
            }
            var Z  = Z0 * (Z0.Conjugate() / Z0 + s) / (Complex.One - s);
            var Zn = Z / Z0;

            return m.UseNormalizedImpedance
                ? $"impedance=Z0*({m.FormatComplex(Zn)})"
                : $"impedance={m.FormatComplex(Z)} Ω";
        }

        public List<(string Text, bool Bold)> BuildMarkerBoxLines(Marker m, FreqUnit freqUnit,
            bool showFilePrefix = true, IReadOnlyList<Trace>? otherTraces = null)
        {
            var lines = new List<(string, bool)>
            {
                (m.MarkerString,                        true),
                (m.FreqString,                          false),
                (GetMarkerValString(m, showFilePrefix), false)
            };
            if (MarkerShowsImpedance(m))
                lines.Add((GetMarkerImpedanceString(m), false));
            if (IsStabilityCircle)
                lines.Add((MuString(m), false));

            if (m.IsMulti && otherTraces != null)
                foreach (var other in otherTraces)
                    lines.Add((GetMultiMarkerLine(m, other), false));

            return lines;
        }

        public void SetMarkerFreq(Marker m, double newFreq)
        {
            int fi = Array.FindIndex(Data.Frequencies, f => f >= newFreq - 1e-6);
            if (fi < 0) fi = Data.Frequencies.Length - 1;
            m.Freq = Data.Frequencies[fi];
            SnapMarkerToStabilityCircle(m, fi);
        }

        public void IncrementMarkerFreq(Marker m)
        {
            int fi = Array.FindIndex(Data.Frequencies, f => f > m.Freq);
            if (fi < 0) fi = Data.Frequencies.Length - 1;
            m.Freq = Data.Frequencies[fi];
            m.PositionStatic = new Vector2(0, 0);
            SnapMarkerToStabilityCircle(m, fi);
        }

        public void DecrementMarkerFreq(Marker m)
        {
            int fi = Array.FindLastIndex(Data.Frequencies, f => f < m.Freq);
            if (fi < 0) fi = 0;
            m.Freq = Data.Frequencies[fi];
            m.PositionStatic = new Vector2(0, 0);
            SnapMarkerToStabilityCircle(m, fi);
        }

        public void SnapMarkerToStabilityCircle(Marker m, int freqIndex)
        {
            if (!IsStabilityCircle) return;
            var nearest = FindNearestPointOnStabilityCircle(m.PositionStatic, freqIndex);
            if (nearest.HasValue) m.PositionStatic = nearest.Value;
        }

        // ---- Equality ---------------------------------------------------

        public override bool Equals(object? obj) => obj is Trace t && t.Id == Id;
        public override int  GetHashCode()        => Id.GetHashCode();

        public bool SameElement(Trace other) =>
            Row == other.Row && Col == other.Col && Derived == other.Derived;

        // ---- Private helpers --------------------------------------------

        private static double Dist(Vector2 a, Vector2 b)
        {
            float dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
