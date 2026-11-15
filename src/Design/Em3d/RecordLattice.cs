// brief-paa-2 R-paa2-9 — the lattice a result's ports stand on, read from a record or a far-field set. The fit itself is
// ArrayLatticeFit in src/Core, where the PhasedArray component also calls it.

using CircuitRF.Core.Devices.Arrays;
using CircuitRF.Design.Layout.Em;

namespace CircuitRF.Design.Em3d;

/// <summary>R-paa2-9 — a result's ports fitted to a lattice.</summary>
public static class RecordLattice
{
    /// <summary>What an older record is said to be.</summary>
    public const string NotRecorded = ArrayLatticeFit.NotRecorded;

    public static RecordLatticeFit Of(EmResultRecord record, int? recordRows = null, int? recordCols = null, double? recordDx = null, double? recordDy = null)
        => ArrayLatticeFit.Of([.. record.Ports.OrderBy(p => p.Number).Select(p => p.Position is { } at ? ((double, double)?)(at.X, at.Y) : null)],
                              recordRows, recordCols, recordDx, recordDy);

    public static RecordLatticeFit Of(PortFarFieldSet far, int? recordRows = null, int? recordCols = null, double? recordDx = null, double? recordDy = null)
        => ArrayLatticeFit.Of(far.PortPositions, recordRows, recordCols, recordDx, recordDy);

    public static RecordLatticeFit Of(IReadOnlyList<(double X, double Y)?> positions, int? recordRows = null, int? recordCols = null,
                                      double? recordDx = null, double? recordDy = null)
        => ArrayLatticeFit.Of(positions, recordRows, recordCols, recordDx, recordDy);
}
