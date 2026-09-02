// ================================================================
//  ComplexFieldCultureRoundTripTests.cs — the Z0 entry box must be able to
//  re-read the text it itself wrote, on every machine.
// ================================================================

using System.Globalization;
using System.Numerics;
using CircuitRF.Ui.DataDisplay.ViewModels;

namespace CircuitRF.Ui.Tests.Localization;

/// <summary>
/// The FIELD analogue of <see cref="FormatCultureInvarianceTests"/>, and deliberately a different
/// assertion. That gate compares BYTES because a file's audience is other machines. This one is a
/// ROUND TRIP because <see cref="ComplexStringHelper"/>'s two halves are each other's only
/// audience: <c>Format</c> fills the Data Display's Z0 box and <c>TryParse</c> reads it back when
/// the user commits it, so the property that matters is that the pair agrees with itself no matter
/// what locale the machine runs at.
///
/// <para><b>Why the round trip is sufficient HERE and not there.</b> The trap the file gate is
/// built around — a comma-decimal writer paired with a comma-decimal reader agreeing perfectly and
/// producing a file nobody else can open — cannot arise, because <c>TryParse</c>'s grammar is a
/// pair of compiled regexes admitting <c>'.'</c> and nothing else. It is structurally incapable of
/// following a locale, so the round trip can only be closed by <c>Format</c> being invariant too.
/// The bytes are asserted anyway, below, to keep that from being an argument rather than a test.</para>
///
/// <para><b>The bug this pins.</b> <c>Format</c>'s real-only branch passed
/// <see cref="CultureInfo.InvariantCulture"/> while its imaginary branch formatted both components
/// with the ambient culture. On a comma-decimal machine a complex reference impedance rendered as
/// <c>50,5+j10,2</c>, which <c>TryParse</c> then rejected — the Z0 box refusing a value the
/// application had just written into it, reported from a Danish install. The real branch's being
/// correct is why this went unseen: the default 50 Ω, and every plain real override, round-tripped
/// fine.</para>
/// </summary>
[Collection(CultureProbeCollection.Name)]
public sealed class ComplexFieldCultureRoundTripTests
{
    /// <summary>de-DE and fi-FI carry over from the file gate; da-DK is here because it is the
    /// locale the field report came from, and a probe that names the reported machine is worth the
    /// microsecond it costs.</summary>
    private static readonly string[] ProbeCultures = ["en-US", "de-DE", "fi-FI", "da-DK"];

    /// <summary>Values chosen so a leak is visible: each has a fractional part in at least one
    /// component, which is the only thing a decimal separator can corrupt. A purely integral
    /// complex value round-trips even through a broken formatter.</summary>
    public static TheoryData<double, double> Values => new()
    {
        {  50.0,    0.0   },   // the default — the case that always worked
        {  75.5,    0.0   },   // real, fractional: the branch that was already invariant
        {  50.5,   10.25  },   // complex, both fractional: the leak
        {   5.0,  100.5   },   // complex, imaginary fractional only
        {   5.25,-100.0   },   // negative imaginary — exercises the '-' sign branch
        {   0.125,  0.0625},   // fractions a comma-decimal culture renders with a comma
    };

    [Theory]
    [MemberData(nameof(Values))]
    public void Format_ThenTryParse_RoundTripsInEveryCulture(double re, double im)
    {
        var z = new Complex(re, im);

        foreach (var probe in ProbeCultures)
        {
            string text = InCulture(probe, () => ComplexStringHelper.Format(z));

            Assert.True(ComplexStringHelper.TryParse(text, out Complex back),
                $"Z0 ROUND TRIP BROKEN under '{probe}': Format({z}) produced \"{text}\", which " +
                $"TryParse then REJECTED. The Data Display's Z0 box writes this text and reads it " +
                $"back when the user commits the field, so the user sees the box refuse a value " +
                $"the application itself put there (\"Invalid Z0 — expected a real or complex " +
                $"value\"). TryParse's grammar admits '.' only, so Format must be invariant.");

            Assert.Equal(z.Real,      back.Real,      12);
            Assert.Equal(z.Imaginary, back.Imaginary, 12);
        }
    }

    /// <summary>
    /// The stronger property, and the one that makes the round trip above more than self-agreement:
    /// the TEXT itself does not vary with the locale. Asserted separately because a formatter and
    /// parser that both moved to commas together would satisfy the round trip and fail this.
    /// </summary>
    [Theory]
    [MemberData(nameof(Values))]
    public void Format_ProducesIdenticalTextInEveryCulture(double re, double im)
    {
        var z = new Complex(re, im);
        string reference = InCulture("en-US", () => ComplexStringHelper.Format(z));

        foreach (var probe in ProbeCultures)
        {
            string actual = InCulture(probe, () => ComplexStringHelper.Format(z));
            Assert.True(reference == actual,
                $"CULTURE LEAK in ComplexStringHelper.Format: under '{probe}' it produced " +
                $"\"{actual}\" where 'en-US' produced \"{reference}\". This string is half of a " +
                $"round trip, not display text — it must not follow the user's locale.");
        }
    }

    private static string InCulture(string name, Func<string> f)
    {
        var previous   = CultureInfo.CurrentCulture;
        var previousUi = CultureInfo.CurrentUICulture;
        try
        {
            var ci = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentCulture   = ci;
            CultureInfo.CurrentUICulture = ci;
            return f();
        }
        finally
        {
            CultureInfo.CurrentCulture   = previous;
            CultureInfo.CurrentUICulture = previousUi;
        }
    }
}
