using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CircuitRF.Core.Matching;
using CircuitRF.Ui.Matching;
using CircuitRF.Ui.Schematic;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// The Match half of the Parameter Editor — the compact panel a selected <c>Match</c> shows in the
/// Properties region (match.md §9.8), built exactly like <c>ParameterEditorViewModel.WBond</c>.
///
/// <para>It states what the component IS — band, order, response, both terminations, worst in-band
/// return loss — and offers <b>Open Match Designer…</b>. It edits nothing: a matching network's
/// parameters are a band, two terminations and a rack of linked sliders, none of which fits in a
/// 420 px column of text rows, and offering a half-editor beside a full one is how the two come to
/// disagree.</para>
/// </summary>
public partial class ParameterEditorViewModel
{
    /// <summary>True when the selected component is a Match — gates the whole panel.</summary>
    public bool IsMatch => _target?.Symbol == SymbolKind.Match;

    /// <summary>
    /// The parameters that must NOT appear as generic text rows — the same mechanism, and the same
    /// reason, as <c>IsWBondPanelParameter</c>. For a <c>Match</c> that is <b>every parameter it
    /// has</b> (owner, 2026-08-28).
    ///
    /// <para><c>Design</c> is the whole matching-network design, base64 of its JSON (match.md §7.2).
    /// It is documented as a HIDDEN parameter and was never meant to be a row: shown as text it is a
    /// screenful of characters nobody can read, act on, or safely edit — and editing it by hand is
    /// the one way to produce a component that refuses at elaboration.</para>
    ///
    /// <para><b>The ECHO parameters joined it.</b> <c>F1</c>, <c>F2</c>, <c>Bands</c>,
    /// <c>F3</c>-<c>F6</c>, <c>Order</c>, <c>Response</c>, <c>Form</c>, <c>R1</c> and <c>R2</c> are
    /// written by the Designer and read back by nothing, so a row offering one is an edit box that
    /// changes a label and not the circuit — and since the glyph began drawing itself from
    /// <c>Form</c> and <c>Bands</c>, one that could silently make the symbol describe a network the
    /// component does not contain. Nothing is hidden by dropping them: the panel above states the
    /// band, the order, the form, the response and both terminations, and the Designer — the only
    /// place a Match is edited at all — states the rest.</para>
    ///
    /// <para>Read from the registry rather than listed here, so a parameter added to the component
    /// type is covered the day it is added. A name the registry does not declare is NOT one of ours
    /// and still gets a row.</para>
    /// </summary>
    internal static bool IsMatchPanelParameter(string name) => MatchOwnParameters.Contains(name);

    private static readonly HashSet<string> MatchOwnParameters =
        ComponentTypeRegistry.DefaultParameters(SymbolKind.Match, 0)
                             .Select(p => p.Name)
                             .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Raised by <see cref="OpenMatchDesignerCommand"/>. The view opens the window: only it knows
    /// which <c>TopLevel</c> owns this panel, and this class stays free of Avalonia exactly as the
    /// rest of the Parameter Editor does.
    /// </summary>
    public event Action<EditableComponent>? OpenMatchDesignerRequested;

    /// <summary>Opens the Match Designer for the selected instance.</summary>
    [RelayCommand]
    private void OpenMatchDesigner()
    {
        if (_target is { Symbol: SymbolKind.Match }) OpenMatchDesignerRequested?.Invoke(_target);
    }

    /// <summary>
    /// The band, "3.3 – 5 GHz" — or every EFFECTIVE band for a multiband design, "2.201 – 2.5 &amp;
    /// 5.15 – 5.85 GHz" (match.md §18.3).
    /// </summary>
    /// <remarks>
    /// <b>The effective bands, not the requested ones</b>, because they are what the network was
    /// built to; <see cref="MatchBandTooltip"/> carries the requested pair when the two differ, so
    /// nothing is hidden and the line stays one line.
    /// </remarks>
    public string MatchBandSummary
    {
        get
        {
            if (_matchDesign is not { } d) return "";
            if (d.BandCount < 2) return $"{Ghz(d.F1)} – {Ghz(d.F2)} GHz";
            return string.Join(" & ", d.Bands.Select(b => $"{Ghz(b.Lo)} – {Ghz(b.Hi)}")) + " GHz";
        }
    }

    /// <summary>The requested bands, when symmetrisation moved one of them; otherwise empty.</summary>
    public string MatchBandTooltip
    {
        get
        {
            if (_matchDesign is not { } d || d.BandCount < 2 || !d.Effective.Widened) return "";
            var requested = d.BandCount >= 3
                ? new[] { (d.F1, d.F2), (d.F3, d.F4), (d.F5, d.F6) }
                : [(d.F1, d.F2), (d.F3, d.F4)];
            return "Requested "
                   + string.Join(" & ", requested.Select(b => $"{Ghz(b.Item1)} – {Ghz(b.Item2)}"))
                   + " GHz. " + d.Effective.Note;
        }
    }

    /// <summary>"order 4 · Chebyshev — Fano optimum".</summary>
    public string MatchOrderSummary => _matchDesign is not { } d
        ? ""
        : $"order {d.Order} · "
          + $"{d.BandCount switch { >= 3 => "tri-band", 2 => "dual-band", _ => FormName(d.Form) }}"
          + $" · {ResponseName(d.Response)}";

    /// <summary>Termination 1, as one line.</summary>
    public string MatchTerm1Summary => _matchDesign is { } d ? TerminationLine(d, d.Term1) : "";

    /// <summary>Termination 2, as one line.</summary>
    public string MatchTerm2Summary => _matchDesign is { } d ? TerminationLine(d, d.Term2) : "";

    /// <summary>
    /// "worst in-band RL 16.66 dB", or the refusal's own message when there is no network — the panel
    /// says the same thing the Designer's status strip would, rather than showing a blank where a
    /// number should be.
    /// </summary>
    public string MatchReturnLossSummary => _matchSummary;

    /// <summary>
    /// True when the line wants attention — the design refuses OUTRIGHT, or it synthesises and its
    /// transforms do not reach the far termination. Both are states a user has to act on, and a
    /// freshly written design with no transforms yet is normally the second one.
    /// </summary>
    public bool MatchNeedsAttention => _matchRefused;

    /// <summary>An unreadable <c>Design</c> payload, stated rather than hidden.</summary>
    public string MatchPayloadError => _matchPayloadError;

    private MatchDesign? _matchDesign;
    private string _matchSummary = "";
    private string _matchPayloadError = "";
    private bool _matchRefused;

    /// <summary>
    /// Re-reads the design from the selected component. Called from the editor's own refresh, so the
    /// panel follows an undo or an edit made in the Designer window.
    /// </summary>
    private void RefreshMatchProperties()
    {
        _matchDesign = null;
        _matchSummary = "";
        _matchPayloadError = "";
        _matchRefused = false;

        if (_target?.Symbol == SymbolKind.Match)
        {
            string payload = _target.Parameters
                .FirstOrDefault(p => p.Name == MatchEmbedding.DesignParameter)?.Expression ?? "";

            if (MatchEmbedding.TryDecode(payload, out var design) && design is not null)
            {
                _matchDesign = design;
                var rebuild = MatchRebuild.Rebuild(design);
                if (rebuild.Network is { } network)
                {
                    double worst = MatchResponse.WorstReturnLossDb(network, design.Bands);
                    _matchSummary = $"worst in-band RL {(-worst).ToString("0.00", CultureInfo.InvariantCulture)} dB"
                                    + (rebuild.OnTarget ? "" : " · Π N² not reached");
                    _matchRefused = !rebuild.OnTarget;
                }
                else
                {
                    _matchSummary = rebuild.Refusal?.Message ?? "This design does not synthesise.";
                    _matchRefused = true;
                }
            }
            else
            {
                _matchPayloadError =
                    "This Match's Design parameter could not be decoded. Open the Designer to see what "
                    + "it falls back to; nothing has been written over the stored payload.";
            }
        }

        OnPropertyChanged(nameof(IsMatch));
        OnPropertyChanged(nameof(MatchBandSummary));
        OnPropertyChanged(nameof(MatchOrderSummary));
        OnPropertyChanged(nameof(MatchTerm1Summary));
        OnPropertyChanged(nameof(MatchTerm2Summary));
        OnPropertyChanged(nameof(MatchReturnLossSummary));
        OnPropertyChanged(nameof(MatchNeedsAttention));
        OnPropertyChanged(nameof(MatchPayloadError));
    }

    private static string Ghz(double hz) =>
        (hz / 1e9).ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>The response family under match.md §6.9's names. Display only; the enum is untouched.</summary>
    private static string ResponseName(ResponseShape shape) => shape switch
    {
        ResponseShape.ChebyshevFano     => "Chebyshev — single-match (optimum)",
        ResponseShape.ChebyshevTwoEnded => "Chebyshev — double-match (exact)",
        ResponseShape.Butterworth       => "Butterworth",
        _                               => "Bessel",
    };

    /// <summary>The network form, lower-case, as the Designer's own cards spell it (match.md §16.7).</summary>
    private static string FormName(NetworkForm form) => form switch
    {
        NetworkForm.Lowpass  => "lowpass",
        NetworkForm.Highpass => "highpass",
        _                    => "bandpass",
    };

    private static string TerminationLine(MatchDesign design, Termination t)
    {
        string r = MatchValueFormat.FormatWithUnit(t.R, MatchQuantity.Resistance, "Ω", 4);
        if (t.Kind == ReactanceKind.None) return $"{r}, resistive";

        var quantity = t.Kind == ReactanceKind.L ? MatchQuantity.Inductance : MatchQuantity.Capacitance;
        string x = MatchValueFormat.FormatWithUnit(t.Value, quantity, MatchValueFormat.AutoUnit, 4);
        string how = t.Topology == TerminationTopology.Series ? "series" : "parallel";
        return $"{r} {how} {x}  (Q {t.QAt(design.Omega0).ToString("0.###", CultureInfo.InvariantCulture)})";
    }
}
