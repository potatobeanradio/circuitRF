// ================================================================
//  Misc.cs  —  Plot-specific enums and TraceProperties
//
//  Ported from splotRF/src/Models/Misc.cs — namespace renamed to
//  CircuitRF.Ui.DataDisplay; font/color seams are in the Renderers layer.
// ================================================================

using System;
using System.Collections.Generic;
using Avalonia.Media;

namespace CircuitRF.Ui.DataDisplay
{
    // ============================================================
    //  PlotDetail
    // ============================================================

    public enum PlotDetail { Full, Medium, Quick }

    public static class PlotDetailExtensions
    {
        public static (bool MinorTicks, bool MajorTicks, bool Labels)
            Properties(this PlotDetail d) => d switch
        {
            PlotDetail.Full   => (true,  true,  true),
            PlotDetail.Medium => (false, true,  true),
            PlotDetail.Quick  => (false, false, false),
            _                 => (false, false, false)
        };
    }

    // ============================================================
    //  MarkerType / LineType
    // ============================================================

    public enum MarkerType { Circle, Square }
    public enum LineType   { Solid,  Dashed  }

    // ============================================================
    //  PrecisionFormat
    // ============================================================

    public enum PrecisionFormat { G, F, E }

    public static class PrecisionFormatExtensions
    {
        public static string Description(this PrecisionFormat f) => f switch
        {
            PrecisionFormat.G => "Auto",
            PrecisionFormat.F => "Fixed",
            PrecisionFormat.E => "Scientific",
            _                 => f.ToString()
        };
    }

    // ============================================================
    //  TraceProperties  —  per-trace visual state
    // ============================================================

    /// <summary>
    /// Stores line, marker, and fill visual properties for one trace.
    /// Uses Avalonia <see cref="Color"/> for storage; the renderer
    /// converts to SKColor via <see cref="RenderTheme"/>.
    /// </summary>
    public class TraceProperties
    {
        // ---- Color look-up table ----------------------------------------

        public static readonly Dictionary<int, Color> ColorLUT = new()
        {
            {  0, Colors.Black       },
            {  1, Colors.Blue        },
            {  2, Colors.Brown       },
            {  3, Colors.Transparent },
            {  4, Colors.Cyan        },
            {  5, Colors.Gray        },
            {  6, Colors.Green       },
            {  7, Colors.Indigo      },
            {  8, Color.FromRgb(0x3E, 0xB4, 0x89) }, // mint
            {  9, Colors.Orange      },
            { 10, Colors.Pink        },
            { 11, Colors.Purple      },
            { 12, Colors.Red         },
            { 13, Colors.Teal        },
            { 14, Colors.White       },
            { 15, Colors.Yellow      },
            { 16, Colors.CornflowerBlue }             // accent
        };

        public static readonly int[]        LineColorOrder  = { 12, 1, 9, 6, 11, 2, 10, 4, 5, 0, 7 };
        public static readonly double[]     LineWidthOrder  = { 1.0, 0.75 };
        public static readonly MarkerType[] MarkerTypeOrder = { MarkerType.Circle, MarkerType.Square };

        public static Color DefaultLineColor   = Colors.Red;
        public static Color DefaultMarkerColor = Colors.Red;
        public static Color DefaultFillColor   = Colors.CornflowerBlue;

        // ---- Identity ---------------------------------------------------

        public string Id { get; set; } = string.Empty;

        // ---- Enabled flag -----------------------------------------------

        public bool Enabled { get; set; } = false;

        // ---- Line -------------------------------------------------------

        private int    _lineColorIndex = LineColorOrder[0];
        private Color? _lineColorStorage;
        private double _lineWidth   = LineWidthOrder[0];
        private double _lineOpacity = 1.0;
        private bool   _lineEnabled = true;
        private LineType _lineType  = LineType.Solid;

        public int LineColorIndex
        {
            get => _lineColorIndex;
            set { _lineColorIndex = value; Custom = true; }
        }
        public Color? LineColorStorage
        {
            get => _lineColorStorage;
            set { _lineColorStorage = value; Custom = true; }
        }
        public double LineWidth
        {
            get => _lineWidth;
            set { _lineWidth = value; Custom = true; }
        }
        public double LineOpacity
        {
            get => _lineOpacity;
            set { _lineOpacity = value; Custom = true; }
        }
        public bool LineEnabled
        {
            get => _lineEnabled;
            set { _lineEnabled = value; Custom = true; }
        }
        public LineType LineType
        {
            get => _lineType;
            set { _lineType = value; Custom = true; }
        }

        // ---- Marker -----------------------------------------------------

        private int       _markerColorIndex = LineColorOrder[0];
        private Color?    _markerColorStorage;
        private double    _markerSize    = 1.5;
        private double    _markerOpacity = 1.0;
        private bool      _markerEnabled = false;
        private MarkerType _markerType   = MarkerType.Circle;

        public int MarkerColorIndex
        {
            get => _markerColorIndex;
            set { _markerColorIndex = value; Custom = true; }
        }
        public Color? MarkerColorStorage
        {
            get => _markerColorStorage;
            set { _markerColorStorage = value; Custom = true; }
        }
        public double MarkerSize
        {
            get => _markerSize;
            set { _markerSize = value; Custom = true; }
        }
        public double MarkerOpacity
        {
            get => _markerOpacity;
            set { _markerOpacity = value; Custom = true; }
        }
        public bool MarkerEnabled
        {
            get => _markerEnabled;
            set { _markerEnabled = value; Custom = true; }
        }
        public MarkerType MarkerType
        {
            get => _markerType;
            set { _markerType = value; Custom = true; }
        }

        // ---- Fill -------------------------------------------------------

        private int    _fillColorIndex = LineColorOrder[0];
        private Color? _fillColorStorage;
        private double _fillOpacity    = 0.0;

        public int FillColorIndex
        {
            get => _fillColorIndex;
            set { _fillColorIndex = value; Custom = true; }
        }
        public Color? FillColorStorage
        {
            get => _fillColorStorage;
            set { _fillColorStorage = value; Custom = true; }
        }
        public double FillOpacity
        {
            get => _fillOpacity;
            set { _fillOpacity = value; Custom = true; }
        }

        // ---- Resolved Avalonia colors -----------------------------------

        public Color LineColor =>
            LineColorStorage
            ?? (ColorLUT.TryGetValue(LineColorIndex, out var c) ? c : DefaultLineColor);

        public Color MarkerColor =>
            MarkerColorStorage
            ?? (ColorLUT.TryGetValue(MarkerColorIndex, out var c) ? c : DefaultMarkerColor);

        public Color FillColor =>
            FillColorStorage
            ?? (ColorLUT.TryGetValue(FillColorIndex, out var c) ? c : DefaultFillColor);

        // ---- Custom flag ------------------------------------------------

        public bool Custom { get; set; } = false;

        // ---- Constructors -----------------------------------------------

        public TraceProperties() { }

        public TraceProperties(string fileName) { Id = fileName; }

        /// <summary>Copy constructor; optionally cycles the color forward.</summary>
        public TraceProperties(TraceProperties src, int incrementColorBy = 0)
        {
            Id      = src.Id;
            Enabled = src.Enabled;
            CopyProperties(src);

            if (incrementColorBy > 0)
            {
                int ci = Array.IndexOf(LineColorOrder, LineColorIndex);
                if (ci >= 0)
                {
                    int next = ci + incrementColorBy;
                    int idx  = next < LineColorOrder.Length
                        ? LineColorOrder[next]
                        : LineColorOrder[0];
                    _lineColorIndex   = idx;
                    _markerColorIndex = idx;
                    _lineColorStorage   = null;
                    _markerColorStorage = null;
                    Custom = false;
                }
            }
        }

        /// <summary>Creates a secondary-axis–styled properties object.</summary>
        public static TraceProperties ForSecondary(string fileName = "")
        {
            return new TraceProperties(fileName)
            {
                _lineColorIndex   = LineColorOrder[1],
                _markerColorIndex = LineColorOrder[1],
                _lineWidth        = LineWidthOrder[1]
            };
        }

        // ---- Copy / comparison ------------------------------------------

        public void CopyProperties(TraceProperties src)
        {
            _lineColorIndex    = src._lineColorIndex;
            _lineColorStorage  = src._lineColorStorage;
            _lineWidth         = src._lineWidth;
            _lineOpacity       = src._lineOpacity;
            _lineEnabled       = src._lineEnabled;
            _lineType          = src._lineType;

            _markerColorIndex   = src._markerColorIndex;
            _markerColorStorage = src._markerColorStorage;
            _markerSize         = src._markerSize;
            _markerOpacity      = src._markerOpacity;
            _markerEnabled      = src._markerEnabled;
            _markerType         = src._markerType;

            _fillColorStorage = src._fillColorStorage;
            _fillColorIndex   = src._fillColorIndex;
            _fillOpacity      = src._fillOpacity;
            Custom            = src.Custom;
        }

        public bool HasSameProperties(TraceProperties rhs)
        {
            return _lineWidth        == rhs._lineWidth
                && _lineOpacity      == rhs._lineOpacity
                && Custom            == rhs.Custom
                && _lineColorStorage == rhs._lineColorStorage
                && _lineEnabled      == rhs._lineEnabled
                && _lineType         == rhs._lineType
                && _markerSize       == rhs._markerSize
                && _markerOpacity    == rhs._markerOpacity
                && _markerColorStorage == rhs._markerColorStorage
                && _markerEnabled    == rhs._markerEnabled
                && _markerType       == rhs._markerType;
        }

        public override bool Equals(object? obj) =>
            obj is TraceProperties rhs && Id == rhs.Id;

        public override int GetHashCode() => Id.GetHashCode();
    }
}
