using System;
using System.Numerics;
using CircuitRF.Harmonica;

namespace CircuitRF.Ui.Harmonica;

/// <summary>Which of the Set Termination dialog's three combined-text rows.</summary>
public enum TerminationField { GammaRealImag, GammaMagAngle, ZRealImag }

/// <summary>The edit the user settled on: exactly one of the two is set — whichever row was last
/// typed in — or neither, if nothing changed and the marker's own current value travels back as a
/// no-op impedance write.</summary>
public readonly record struct TerminationEdit(Complex? Impedance, Complex? Gamma);

/// <summary>
/// R8B §1.3 — the Set Termination dialog's edit state machine, extracted with no Avalonia reference so
/// it can be driven by a real test rather than a hand-built simulation of a handler shape.
///
/// <para><b>Ownership, not a re-entrancy flag.</b> The dialog used to guard its three
/// <c>TextChanged</c> handlers with a single <c>bool _loading</c> set for the duration of the
/// programmatic write that echoes an edit into the other two boxes. That is a window in time, not a
/// statement about identity — an echo that lands after the window closes (a deferred raise, IME
/// commit, binding round-trip) is processed as if the user had typed it, which is what let a
/// termination edit corrupt the box under the caret. Here, ownership is <see cref="Editing"/>: an
/// <see cref="Edit"/> call for any field other than the one currently owned is simply ignored,
/// regardless of when or how it was raised.</para>
/// </summary>
public sealed class TerminationEditModel
{
    private readonly double _z0;
    private Complex _gamma;
    private Complex _z;

    public TerminationEditModel(Complex initialGamma, double z0)
    {
        _z0 = z0;
        _gamma = initialGamma;
        _z = HarmonicaDataSet.ImpedanceOf(initialGamma, z0);
    }

    public Complex Gamma => _gamma;
    public Complex Z => _z;
    public bool LastEditWasGamma { get; private set; }

    /// <summary>Which field the user is in. Null = nobody; every <see cref="Edit"/> call for a
    /// different field is ignored.</summary>
    public TerminationField? Editing { get; set; }

    /// <summary>Returns true if <paramref name="text"/> parsed and the model moved. Ignored (returns
    /// false, no change) if <paramref name="field"/> is not the currently <see cref="Editing"/> one —
    /// this is what makes an echo from the OTHER two boxes' own reformat harmless no matter when it
    /// arrives.</summary>
    public bool Edit(TerminationField field, string? text)
    {
        if (field != Editing) return false;

        var format = field == TerminationField.GammaMagAngle
            ? ReadoutFormat.MagnitudeAngle : ReadoutFormat.RealImaginary;
        if (!HarmonicaReadoutFormatting.TryParse(text, format, out var value)) return false;

        if (field == TerminationField.ZRealImag)
        {
            LastEditWasGamma = false;
            _z = value;
            _gamma = HarmonicaDataSet.GammaOf(value, _z0);
        }
        else
        {
            LastEditWasGamma = true;
            _gamma = value;
            _z = HarmonicaDataSet.ImpedanceOf(value, _z0);
        }
        return true;
    }

    /// <summary>The text a field should DISPLAY right now. Never called for <see cref="Editing"/> —
    /// enforced by throwing, since a silent wrong answer here is how the underlying bug came back
    /// twice already.</summary>
    public string TextFor(TerminationField field)
    {
        if (field == Editing)
            throw new InvalidOperationException(
                $"TextFor({field}) must never be consulted while it is the field being edited.");

        return field switch
        {
            TerminationField.GammaRealImag => HarmonicaReadoutFormatting.FormatGamma(_gamma, ReadoutFormat.RealImaginary),
            TerminationField.GammaMagAngle  => HarmonicaReadoutFormatting.FormatGamma(_gamma, ReadoutFormat.MagnitudeAngle),
            TerminationField.ZRealImag      => HarmonicaReadoutFormatting.FormatZ(_z, ReadoutFormat.RealImaginary),
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
    }

    public TerminationEdit Commit()
        => LastEditWasGamma ? new TerminationEdit(null, _gamma) : new TerminationEdit(_z, null);
}
