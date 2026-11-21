using System;
using System.Linq;
using CircuitRF.Core.Matching;
using CircuitRF.Core.Systems;

namespace CircuitRF.Ui.ViewModels;

public sealed partial class ParameterEditorViewModel
{
    /// <summary>
    /// A Filter's rows its <c>Response</c> and <c>Form</c> do not read, greyed with the reason
    /// (brief FD-P §3.2). The test pad's mechanism: greying never touches a value, so switching
    /// back restores what was there, and it runs at build and on every refresh because a
    /// <c>Response</c> or <c>Form</c> change is an ordinary edit that keeps the rows.
    /// </summary>
    private void ConfigureFilterRows()
    {
        if (_target?.Symbol != SymbolKind.Filter) return;
        string Expr(string name) =>
            _target.Parameters.FirstOrDefault(p => p.Name == name)?.Expression.Trim() ?? "";
        // An unreadable value greys nothing: the model reads it as its default, and hiding rows on
        // a guess would say more than is known.
        FilterResponse? response = Enum.TryParse<FilterResponse>(Expr("Response"), true, out var r) ? r : null;
        NetworkForm? form = Enum.TryParse<NetworkForm>(Expr("Form"), true, out var f) ? f : null;
        foreach (var row in Rows)
        {
            string? reason = FilterRowUnused(row.Name, response, form);
            row.SetDisabledByMode(reason is not null, reason);
        }
    }

    /// <summary>Why <paramref name="name"/> is not read for this response and form, or null when it is.</summary>
    internal static string? FilterRowUnused(string name, FilterResponse? response, NetworkForm? form)
    {
        bool band = form is NetworkForm.Bandpass or NetworkForm.Bandstop;
        bool edge = form is NetworkForm.Lowpass or NetworkForm.Highpass;
        return name switch
        {
            "Ripple" when response is FilterResponse.Butterworth or FilterResponse.Bessel
                                    or FilterResponse.Gaussian or FilterResponse.InvChebyshev =>
                $"Not read by {response}: only Chebyshev and Elliptic have a passband ripple.",
            "Astop" when response is FilterResponse.Butterworth or FilterResponse.Chebyshev
                                   or FilterResponse.Bessel or FilterResponse.Gaussian =>
                $"Not read by {response}: only InvChebyshev and Elliptic have a stopband floor.",
            "EllipticType" when response is not null && response != FilterResponse.Elliptic =>
                $"Not read by {response}: it chooses between elliptic responses.",
            "Fc" or "Fp" or "Fs" when band =>
                $"Not read by a {form}: it reads F1 and F2.",
            "F1" or "F2" or "Fp1" or "Fs1" or "Fs2" or "Fp2" when edge =>
                $"Not read by a {form}: it reads Fc.",
            _ => null,
        };
    }
}
