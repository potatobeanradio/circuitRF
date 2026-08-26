// Split out of src/Ui/Schematic/ComponentTypeRegistry.cs when the cell-folder format moved to
// CircuitRF.Design (brief-cli-em-verb.md R-emcli-3, the same treatment DrcWaiver got).
//
// WHY: CcellParameter.Dimension persists this enum, so the `.ccell` reader names it. The registry it
// was declared in is 1,300 lines of schematic component metadata — placement defaults, indexed
// parameter groups, the palette's categories — none of which a headless EM run can reach and none of
// which belongs on this side of the firewall.

namespace CircuitRF.Design.Cells;

/// <summary>Physical dimension of a component parameter — drives the closed Unit ComboBox.</summary>
public enum UnitDimension
{
    None,
    Resistance,
    Inductance,
    Capacitance,
    Frequency,
    Voltage,
    Current,
    Power,
    Length,
    Angle,
}
