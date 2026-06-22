namespace CircuitRF.Ui.DataDisplay
{
    /// <summary>Which optimum load termination a summary Table evaluates every column at.</summary>
    public enum TableOptimum { Mxp, Mxe }

    /// <summary>How a summary Table reads each surface metric at the optimum coordinate.</summary>
    public enum TableReadMode { Interp, Nearest }
}
