namespace CircuitRF.Core.Devices;

/// <summary>
/// A source whose RF DRIVE can be scaled by the engine without re-elaborating the netlist, so a
/// continuation scheme can walk the drive up to the requested level (harmonic-balance.md §11 —
/// <c>DriveStepping</c>; the drive-ramp fallback in <c>HbEngine</c>).
///
/// <para><b>The AC excitation only, never a DC bias.</b> A drive ramp asks "what does this circuit
/// do at less signal", not "at less supply" — ramping the bias too would walk the operating point
/// off the branch the ramp exists to follow, and a FET's gate bias would come up from pinch-off at
/// the same time as its drive. Every implementation therefore scales its tone phasors and leaves
/// <c>Vdc</c>/<c>Idc</c> alone. (Bias ramping is a separate mechanism with its own setting —
/// <c>AnalysisSettings.DcBiasStepping</c>; do not conflate the two.)</para>
///
/// <para><b>The scale is a VOLTAGE (or current) multiplier, not a power one</b>, so a ramp of
/// <c>d</c> dB sets <c>10^(d/20)</c> — the same arithmetic whether the source declares an amplitude
/// (<c>V_1Tone</c>) or an available power (<c>P1Tone</c>, <c>PnTone</c>), which is what lets one
/// ramp move every source in a circuit together by a common dB offset.</para>
///
/// <para>1.0 is the declared drive and is what every source is constructed with; the engine restores
/// it in a <c>finally</c>, so nothing outside a ramp ever sees another value.</para>
/// </summary>
public interface IDriveScalable
{
    /// <summary>Linear multiplier on this source's tone excitation. 1.0 = the declared drive.</summary>
    double DriveScale { get; set; }
}
