using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Matching;
using CircuitRF.Core.Systems;
using CircuitRF.Ui.Filters;

namespace CircuitRF.Ui.ViewModels;

public sealed partial class ParameterEditorViewModel
{
    /// <summary>What a locked row says: the Filter carries a Designer design.</summary>
    internal const string FilterLockedReason =
        "This Filter was designed in the LC Filter Designer. Use Synthesise… to change it, so the "
      + "specification and the design stay the same.";

    /// <summary>
    /// The order a Filter's rows are shown in: the LC Filter Designer's own, card by card —
    /// impedances, topology, shape, edges, values — so the dialog and the window read alike.
    /// <c>IL</c> is the block's own and comes last. A name not listed keeps its place after them.
    /// </summary>
    private static readonly string[] FilterRowOrder =
    [
        "Zin", "Zout",
        "First", "OrderMode", "Order",
        "Response", "Form", "Ap", "Ripple", "Astop", "EllipticType",
        "Fc", "F1", "F2", "Fp", "Fs", "Fp1", "Fs1", "Fs2", "Fp2",
        "Values", "LSeries", "CSeries", "Parts",
        "IL",
    ];

    /// <summary>
    /// A Filter's parameters as rows: in <see cref="FilterRowOrder"/>, and without the Designer's
    /// own settings on a Filter that carries no design — only the Designer writes those, so on a
    /// plain block they would be empty rows nobody can fill in meaningfully.
    /// </summary>
    internal static List<EditableParameter> FilterRowParameters(EditableComponent comp)
    {
        bool designed = FilterPlacement.IsDesigned(comp);
        return comp.Parameters
            .Where(p => designed || !ComponentTypeRegistry.IsLcFilterDesignerParameter(SymbolKind.Filter, p.Name))
            .Select((p, i) => (p, i))
            .OrderBy(t => Array.IndexOf(FilterRowOrder, t.p.Name) is var k and >= 0 ? k : FilterRowOrder.Length + t.i)
            .Select(t => t.p)
            .ToList();
    }

    /// <summary>
    /// A Filter's rows its <c>Response</c> and <c>Form</c> do not read, greyed with the reason
    /// (brief FD-P §3.2) — and on a Filter that carries a Designer design, EVERY row the Designer
    /// writes, locked (owner, 2026-11-21): a change has to go through Synthesise…, so the block and
    /// the design behind it cannot drift apart. <c>IL</c> stays editable; the Designer does not
    /// write it, so locking it would leave it unchangeable. The test pad's mechanism: greying never
    /// touches a value, and it runs at build and on every refresh.
    /// </summary>
    private void ConfigureFilterRows()
    {
        if (_target?.Symbol != SymbolKind.Filter) return;
        bool designed = FilterPlacement.IsDesigned(_target);
        string Expr(string name) =>
            _target.Parameters.FirstOrDefault(p => p.Name == name)?.Expression.Trim() ?? "";
        // An unreadable value greys nothing: the model reads it as its default, and hiding rows on
        // a guess would say more than is known.
        FilterResponse? response = Enum.TryParse<FilterResponse>(Expr("Response"), true, out var r) ? r : null;
        NetworkForm? form = Enum.TryParse<NetworkForm>(Expr("Form"), true, out var f) ? f : null;
        foreach (var row in Rows)
        {
            bool locked = designed && row.Name != "IL";
            string? unused = FilterRowUnused(row.Name, response, form);
            string? reason = locked ? FilterLockedReason + (unused is null ? "" : " " + unused) : unused;
            row.SetExpressionReadOnly(locked);
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
