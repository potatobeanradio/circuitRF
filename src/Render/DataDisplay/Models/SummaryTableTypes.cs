namespace CircuitRF.Render.DataDisplay
{
    /// <summary>Which load termination a summary Table evaluates every column at — the max-power or max-efficiency
    /// optimum, or the load a marker sits on (<see cref="Plot.TableMarkerLink"/>, brief-ddl-3 R-ddl3-5).</summary>
    public enum TableOptimum { Mxp, Mxe, AtMarker }

    /// <summary>How a summary Table reads each surface metric at the optimum coordinate.</summary>
    public enum TableReadMode { Interp, Nearest }
}
