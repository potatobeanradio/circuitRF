using System;
using CircuitRF.Core.Matching;
using CircuitRF.Engine.Matching;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Matching;

/// <summary>
/// One of match.md §10.2's candidate models, as the Designer lists it: what it fitted, what it
/// cost in Γ, whether it is physical, and a button to take it instead of the winner.
/// </summary>
/// <remarks>
/// <b>All of them are always listed, including the non-physical ones.</b> §10.2 is explicit that the
/// residual is data the user is entitled to see and never a hidden gate — a fit with a negative
/// element is shown, labelled, and simply not applicable.
/// </remarks>
public sealed partial class MatchProbeFitRowViewModel : ObservableObject
{
    private readonly MatchTerminationViewModel _owner;

    internal MatchProbeFitRowViewModel(
        MatchTerminationViewModel owner, TerminationProbe.ProbeFit fit, double omega0,
        bool conjugate, bool isBest)
    {
        _owner = owner;
        Fit = fit;
        Omega0 = omega0;
        Conjugate = conjugate;
        IsBest = isBest;
        ApplyCommand = new RelayCommand(() => _owner.ApplyProbeFit(this), () => fit.Physical);
    }

    /// <summary>The fit itself, as measured — never a conjugate target.</summary>
    public TerminationProbe.ProbeFit Fit { get; }

    /// <summary>Band centre the conjugate would be taken at.</summary>
    public double Omega0 { get; }

    /// <summary>Whether this row's Apply would install the conjugate of <see cref="Fit"/>.</summary>
    public bool Conjugate { get; }

    /// <summary>True for the row the probe applied.</summary>
    public bool IsBest { get; }

    /// <summary>"parallel R‖C".</summary>
    public string Name => Fit.Name;

    /// <summary>False when R or the reactance came out non-positive; the row is shown, not applicable.</summary>
    public bool IsPhysical => Fit.Physical;

    /// <summary>Takes this fit instead of the winner.</summary>
    public IRelayCommand ApplyCommand { get; }

    /// <summary>The fitted values, formatted in the Designer's own units.</summary>
    public string ValuesText
    {
        get
        {
            var settings = _owner.Settings;
            string r = MatchValueFormat.FormatWithUnit(
                Fit.R, MatchQuantity.Resistance, settings.ResistanceUnit, settings.SignificantDigits);

            // The R-alone model has no reactance, and "C = 0 pF" would be a value where there is none.
            if (Fit.Kind == ReactanceKind.None) return $"R = {r},  no reactance";

            var q = Fit.Kind == ReactanceKind.L ? MatchQuantity.Inductance : MatchQuantity.Capacitance;
            string x = MatchValueFormat.FormatWithUnit(
                Fit.Value, q, settings.UnitFor(q), settings.SignificantDigits);
            return $"R = {r},  {(Fit.Kind == ReactanceKind.L ? "L" : "C")} = {x}";
        }
    }

    /// <summary>The residual, in Γ, to three figures — the number §10.2 insists on showing.</summary>
    public string ResidualText => $"mean |ΔΓ| = {Fit.Residual:G3}";

    /// <summary>Why this row cannot be applied, or empty.</summary>
    public string PhysicalNote => Fit.Physical
        ? ""
        // "zero or negative", because zero is what a shorted pin produces and calling that negative
        // sends the reader looking for a sign error that is not there.
        : Fit.R <= 0 ? "non-physical: R came out zero or negative"
                     : "non-physical: the reactance came out negative or degenerate";

    /// <summary>What Apply would install — the conjugate when the toggle is on.</summary>
    public string TargetNote
    {
        get
        {
            if (!Conjugate || !Fit.Physical) return "";
            var t = Fit.ConjugateAt(Omega0);
            var q = t.Kind == ReactanceKind.L ? MatchQuantity.Inductance : MatchQuantity.Capacitance;
            string x = MatchValueFormat.FormatWithUnit(
                t.Value, q, _owner.Settings.UnitFor(q), _owner.Settings.SignificantDigits);
            return $"conjugate target: {t.Name}, {(t.Kind == ReactanceKind.L ? "L" : "C")} = {x}";
        }
    }
}
