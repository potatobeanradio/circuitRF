using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using CircuitRF.Ui.Schematic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// Body VM for Harmonic Balance analysis (analysis-authoring.md §4.2 L3).
///
/// Basic section (always visible): Tone (f₀), Max harmonics, Single/Multi-tone toggle.
/// When Multi-tone: the tone LIST (2 … <see cref="AnalysisSettings.HbMaxTones"/> rows, add/remove)
/// and Max mix order appear inline, alongside a live count of the mixing products the current
/// (tone count, order) pair would retain.
/// Advanced section (Expander, collapsed by default): FFT oversample, tolerance, drive stepping,
/// guard harmonic, Newton λ, max iterations, optional parametric sweep.
///
/// All expression fields have live "≈" previews via AnalysisPreviewHelper.
///
/// <para><b><see cref="Tones"/> is canonical; <c>ToneCoeff</c>/<c>Tone2Coeff</c> and their units
/// are named accessors onto rows 1 and 2.</b> Keeping those four properties settable is not
/// cosmetic — the dialog's whole existing test suite and the Loadpull/Pursuit bodies address the
/// first two tones by those names, and a multi-tone analysis must still mirror Tone 1 into the
/// scalar <c>ToneExpr</c> for the consumers that read it.</para>
///
/// <para><b>Why the product count is shown in the dialog.</b> The engine refuses a multi-tone
/// analysis whose retained set exceeds <see cref="AnalysisSettings.HbMaxMixProducts"/>, and the
/// count grows steeply with tone count — 6 tones at the default MaxMixOrder=5 asks for 1,827
/// products and is refused. Showing the number next to the knob that sets it means the refusal
/// is visible while authoring rather than at Run.</para>
/// </summary>
public sealed partial class HbBodyViewModel : ObservableObject
{
    private readonly SchematicEditModel _model;

    // ── Drive stepping options (ComboBox) ─────────────────────────────────────
    public static readonly string[] DriveSteppingOptions = ["Always", "IfNecessary", "Never"];

    // ── Frequency unit list exposed for AXAML x:Static binding ───────────────
    public static readonly string[] FreqUnits = FreqUnitHelper.Units;

    /// <summary>Largest tone count the engine will accept — the dialog's Add gate.</summary>
    public static int MaxTones => AnalysisSettings.Default.HbMaxTones;

    /// <summary>Largest retained mixing-product count the engine will accept.</summary>
    public static int MaxMixProducts => AnalysisSettings.Default.HbMaxMixProducts;

    // ── The tone list — CANONICAL. Row 0 is Tone 1; always at least one row. ──
    public ObservableCollection<HbToneRowViewModel> Tones { get; } = [];

    // ── Named accessors onto rows 1 and 2 (see the class remarks) ────────────

    // These GETTERS must tolerate an empty list. SetTones() clears the collection before refilling
    // it, and Clear() raises CollectionChanged, which raises PropertyChanged for these accessors,
    // which a bound control answers by READING them — mid-clear, with no row 0 to read. A test
    // never sees it (nothing subscribes); the running dialog would throw IndexOutOfRange.
    public string ToneCoeff
    {
        get => Tones.Count > 0 ? Tones[0].Coeff : "1";
        set { EnsureToneCount(1); Tones[0].Coeff = value; }
    }

    public string ToneUnit
    {
        get => Tones.Count > 0 ? Tones[0].Unit : "GHz";
        set { EnsureToneCount(1); Tones[0].Unit = value; }
    }

    public string TonePreview => Tones.Count > 0 ? Tones[0].Preview : "";

    public string Tone2Coeff
    {
        get => Tones.Count > 1 ? Tones[1].Coeff : "2";
        set { EnsureToneCount(2); Tones[1].Coeff = value; }
    }

    public string Tone2Unit
    {
        get => Tones.Count > 1 ? Tones[1].Unit : "GHz";
        set { EnsureToneCount(2); Tones[1].Unit = value; }
    }

    public string Tone2Preview => Tones.Count > 1 ? Tones[1].Preview : "";

    [ObservableProperty] private string _maxHarmonicExpr    = "7";
    [ObservableProperty] private string _maxHarmonicPreview = "";

    // ── Single / Multi-tone toggle ────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSingleTone), nameof(IsMultiTone), nameof(ToneLabel))]
    private bool _multiTone = false;

    public bool   IsSingleTone => !MultiTone;
    public bool   IsMultiTone  =>  MultiTone;
    public string ToneLabel    =>  MultiTone ? "Tone 1" : "Tone (f₀)";

    [ObservableProperty] private string _maxMixOrderExpr    = "5";
    [ObservableProperty] private string _maxMixOrderPreview = "";

    /// <summary>
    /// Live "N tones, order O → M products" readout beside Max mix order, so the engine's
    /// ceiling is visible while authoring instead of arriving as a refusal at Run. Reports the
    /// over-cap case explicitly rather than just showing a large number.
    /// </summary>
    public string MixProductPreview
    {
        get
        {
            if (!MultiTone) return "";
            int t = Tones.Count;
            if (!int.TryParse(MaxMixOrderExpr.Trim(), NumberStyles.Integer,
                              CultureInfo.InvariantCulture, out int order) || order < 1)
                return "";   // an expression, not a literal — the engine resolves it at Run

            int products = MixingLattice.CountFor(t, order);
            string tail  = products > MaxMixProducts
                ? $" — OVER the {MaxMixProducts:N0} limit"
                : $" (limit {MaxMixProducts:N0})";
            return $"{t} tones, order {order} → {products:N0} mixing products{tail}";
        }
    }

    // ── Advanced: Newton / convergence ────────────────────────────────────────
    [ObservableProperty] private bool   _advancedExpanded    = false;
    [ObservableProperty] private string _fftOverSampleExpr   = "1";
    [ObservableProperty] private string _tolExpr             = "1e-6";
    [ObservableProperty] private string _tolPreview          = "";
    [ObservableProperty] private string _driveSteppingExpr   = "IfNecessary";
    [ObservableProperty] private string _guardHarmonicExpr   = "0";
    [ObservableProperty] private string _guardHarmonicPreview = "";
    [ObservableProperty] private string _lambdaExpr          = "1";
    [ObservableProperty] private string _lambdaPreview       = "";
    [ObservableProperty] private string _maxIterExpr         = "100";
    [ObservableProperty] private string _maxIterPreview      = "";

    // ── Constructor ───────────────────────────────────────────────────────────

    public HbBodyViewModel(SchematicEditModel model)
    {
        _model = model;
        Tones.CollectionChanged += OnTonesChanged;
        AddToneInternal(new HbToneRowViewModel(model, "1", "GHz"));
    }
    // Field initializers set the remaining defaults. Previews are empty for bare-number
    // defaults — no need to call property setters in the constructor.

    // ── Tone-list plumbing ────────────────────────────────────────────────────

    private void OnTonesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        for (int i = 0; i < Tones.Count; i++)
        {
            Tones[i].Index        = i + 1;
            Tones[i].CanRemoveSelf = Tones.Count > 2;   // multi-tone needs at least two
        }
        AddToneCommand.NotifyCanExecuteChanged();
        RemoveToneCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanAddTone));
        OnPropertyChanged(nameof(ToneCountSummary));
        OnPropertyChanged(nameof(MixProductPreview));
        RaiseNamedToneAccessors();
    }

    // A row's own edits must surface on the named accessors too — a test or a binding reading
    // ToneCoeff has to see an edit made through Tones[0], and vice versa; they are one value.
    private void OnToneRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HbToneRowViewModel.Coeff)
                           or nameof(HbToneRowViewModel.Unit)
                           or nameof(HbToneRowViewModel.Preview))
            RaiseNamedToneAccessors();
    }

    private void RaiseNamedToneAccessors()
    {
        OnPropertyChanged(nameof(ToneCoeff));
        OnPropertyChanged(nameof(ToneUnit));
        OnPropertyChanged(nameof(TonePreview));
        OnPropertyChanged(nameof(Tone2Coeff));
        OnPropertyChanged(nameof(Tone2Unit));
        OnPropertyChanged(nameof(Tone2Preview));
    }

    private void AddToneInternal(HbToneRowViewModel row)
    {
        row.SetRemoveCallback(r => { if (Tones.Count > 2) Tones.Remove(r); });
        row.PropertyChanged += OnToneRowChanged;
        Tones.Add(row);
    }

    /// <summary>Grows the list to at least <paramref name="count"/> rows, seeding new ones.</summary>
    private void EnsureToneCount(int count)
    {
        if (Tones.Count == 0 && count > 0)
            AddToneInternal(new HbToneRowViewModel(_model, "1", "GHz"));
        while (Tones.Count < count)
            AddToneInternal(new HbToneRowViewModel(_model, NextToneSeed(), Tones[0].Unit));
    }

    /// <summary>
    /// Seed for a newly added tone: one step past the last one when the tones so far are plain
    /// numbers, else a copy of the last expression for the user to edit. Never a blank field.
    /// </summary>
    private string NextToneSeed()
    {
        var last = Tones[^1];
        if (double.TryParse(last.Coeff.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
        {
            double step = 1.0;
            if (Tones.Count >= 2 &&
                double.TryParse(Tones[^2].Coeff.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double prev))
            {
                double d = v - prev;
                if (Math.Abs(d) > 1e-15) step = d;
            }
            return (v + step).ToString("G6", CultureInfo.InvariantCulture);
        }
        return last.Coeff;
    }

    public bool CanAddTone => Tones.Count < MaxTones;

    public string ToneCountSummary =>
        Tones.Count >= MaxTones ? $"{Tones.Count} tones (maximum)" : $"{Tones.Count} tones";

    [RelayCommand(CanExecute = nameof(CanAddTone))]
    private void AddTone()
    {
        if (!CanAddTone) return;
        MultiTone = true;
        EnsureToneCount(Math.Max(2, Tones.Count + 1));
    }

    [RelayCommand(CanExecute = nameof(CanRemoveTone))]
    private void RemoveTone(HbToneRowViewModel? row)
    {
        if (row is null || Tones.Count <= 2) return;
        Tones.Remove(row);
    }

    private bool CanRemoveTone(HbToneRowViewModel? row) => row is not null && Tones.Count > 2;

    // ── Preview side-effects ──────────────────────────────────────────────────

    partial void OnMaxHarmonicExprChanged(string value)   => MaxHarmonicPreview = Prev(value);

    partial void OnMaxMixOrderExprChanged(string value)
    {
        MaxMixOrderPreview = Prev(value);
        OnPropertyChanged(nameof(MixProductPreview));
    }

    partial void OnMultiToneChanged(bool value) => OnPropertyChanged(nameof(MixProductPreview));

    partial void OnTolExprChanged(string value)           => TolPreview           = Prev(value);
    partial void OnGuardHarmonicExprChanged(string value) => GuardHarmonicPreview = Prev(value);
    partial void OnLambdaExprChanged(string value)        => LambdaPreview        = Prev(value);
    partial void OnMaxIterExprChanged(string value)       => MaxIterPreview       = Prev(value);

    private string Prev(string expr) => AnalysisPreviewHelper.ComputePreview(expr, _model);

    // ── Single / Multi-tone commands ──────────────────────────────────────────

    [RelayCommand] private void SetSingleTone() => MultiTone = false;

    [RelayCommand]
    private void SetMultiTone()
    {
        MultiTone = true;
        // Convenience: adopt the tone frequencies from a PnTone on the schematic so the dialog matches
        // the multi-tone source. Graceful no-op if there's no PnTone (or it has no Freq[i]).
        AdoptPnToneTones();
        EnsureToneCount(2);       // multi-tone always shows at least two rows
    }

    /// <summary>
    /// Copies EVERY Freq[i] present on the first PnTone on the schematic (expression + unit,
    /// var/expression-preserving) into the tone list, growing it to match. Stops at the first gap,
    /// mirroring how the .cnl reader collects Tone[i], and is capped at
    /// <see cref="MaxTones"/> so a source with more tones than the engine accepts cannot push the
    /// dialog into a state that can only fail at Run. No-op when no PnTone exists.
    /// </summary>
    private void AdoptPnToneTones()
    {
        var pn = _model.Components.FirstOrDefault(c => c.Symbol == SymbolKind.PnTone);
        if (pn is null) return;

        var adopted = new List<(string Expr, string Unit)>();
        for (int i = 1; i <= MaxTones; i++)
        {
            if (!TryReadFreq(pn, i, out string f, out string u)) break;   // stop at the first gap
            adopted.Add((f, u));
        }
        if (adopted.Count == 0) return;

        SetTones(adopted);
    }

    /// <summary>Replaces the tone list wholesale, preserving each entry's raw expression + unit.</summary>
    private void SetTones(IReadOnlyList<(string Expr, string Unit)> tones)
    {
        foreach (var row in Tones) row.PropertyChanged -= OnToneRowChanged;
        Tones.Clear();
        foreach (var (expr, unit) in tones)
            AddToneInternal(new HbToneRowViewModel(_model, expr, unit));
        if (Tones.Count == 0) AddToneInternal(new HbToneRowViewModel(_model, "1", "GHz"));
    }

    private static bool TryReadFreq(EditableComponent pn, int i, out string expr, out string unit)
    {
        var p = pn.Parameters.FirstOrDefault(q => q.Name == $"Freq[{i}]");
        if (p is null || string.IsNullOrWhiteSpace(p.Expression)) { expr = ""; unit = ""; return false; }
        expr = p.Expression;
        unit = string.IsNullOrEmpty(p.Unit) ? "Hz" : p.Unit;
        return true;
    }

    // ── Build ─────────────────────────────────────────────────────────────────

    public HarmonicBalanceAnalysis BuildAnalysis(string name, bool enabled)
    {
        // Store raw expr + separate unit — do NOT bake via ToHzExpr.
        HarmonicBalanceAnalysis analysis = MultiTone
            ? new HarmonicBalanceAnalysis(name)
            {
                // Mirror Tone 1 into the scalar ToneExpr/ToneUnit as well as ToneExprs[0]. The engine
                // reads ToneExprs[0] for multi-tone, but FromAnalysis and other consumers read the
                // scalar field — keeping both in sync makes the dialog round-trip lossless.
                ToneExpr          = ToneCoeff,
                ToneUnit          = ToneUnit,
                NumFreqsExpr      = Tones.Count.ToString(CultureInfo.InvariantCulture),
                ToneExprs         = Tones.Select(t => t.Coeff).ToArray(),
                ToneUnits         = Tones.Select(t => t.Unit).ToArray(),
                MaxMixOrderExpr   = MaxMixOrderExpr,
                MaxHarmonicExpr   = MaxHarmonicExpr,
                FFTOverSampleExpr = FftOverSampleExpr,
                TolExpr           = TolExpr,
                DriveSteppingExpr = DriveSteppingExpr,
                GuardHarmonicExpr = GuardHarmonicExpr,
                LambdaExpr        = LambdaExpr,
                MaxIterExpr       = MaxIterExpr,
            }
            : new HarmonicBalanceAnalysis(name)
            {
                ToneExpr          = ToneCoeff,
                ToneUnit          = ToneUnit,
                MaxHarmonicExpr   = MaxHarmonicExpr,
                FFTOverSampleExpr = FftOverSampleExpr,
                TolExpr           = TolExpr,
                DriveSteppingExpr = DriveSteppingExpr,
                GuardHarmonicExpr = GuardHarmonicExpr,
                LambdaExpr        = LambdaExpr,
                MaxIterExpr       = MaxIterExpr,
            };

        analysis.Enabled = enabled;
        return analysis;
    }

    // ── FromAnalysis ──────────────────────────────────────────────────────────

    public static HbBodyViewModel FromAnalysis(HarmonicBalanceAnalysis hb, SchematicEditModel model)
    {
        var vm = new HbBodyViewModel(model);

        // Read stored raw expr + unit directly.
        // Set _prevXUnit before ToneUnit so OnToneUnitChanged sees from==to → no rescaling.
        // Legacy nicety: when ToneUnit=="Hz" and ToneExpr is a plain number, use Split for
        // pretty display (e.g. "2.4e9" → "2.4" GHz). Never Split a non-numeric expression.
        bool multi = int.TryParse(hb.NumFreqsExpr, out int n) && n > 1;

        // Tone 1's canonical source in multi-tone is ToneExprs[0] (what the engine reads); single-tone
        // uses the scalar ToneExpr. Reading the right field makes a multi-tone round-trip lossless even
        // for analyses whose scalar ToneExpr was never populated (the original dialog-OK bug).
        string toneExpr = multi && hb.ToneExprs.Length > 0 ? hb.ToneExprs[0] : hb.ToneExpr;
        string toneUnit = multi && hb.ToneUnits.Length > 0 ? hb.ToneUnits[0]
                        : string.IsNullOrEmpty(hb.ToneUnit) ? "Hz" : hb.ToneUnit;
        if (toneUnit == "Hz")
        {
            var (tc, tu) = FreqUnitHelper.Split(toneExpr);
            toneExpr = tc; toneUnit = tu;
        }
        // Seed row 1 by CONSTRUCTION rather than by assigning Unit then Coeff: assigning the unit
        // would trip the row's rescale-on-unit-change and silently multiply a stored coefficient.
        vm.SetTones([(toneExpr, toneUnit)]);
        vm.MaxHarmonicExpr = hb.MaxHarmonicExpr;

        if (multi)
        {
            vm.MultiTone = true;

            // Tone 1 is already seeded above; take tones 2..N from ToneExprs, which is what the
            // engine reads. HbToneRowViewModel does the Hz-split nicety per row, so an expression
            // ("RFfreq - ToneSpacing/2") is preserved and a baked number displays readably.
            var rows = new List<(string, string)> { (toneExpr, toneUnit) };
            for (int i = 1; i < Math.Max(2, Math.Min(n, hb.ToneExprs.Length)); i++)
            {
                string expr = i < hb.ToneExprs.Length ? hb.ToneExprs[i] : "2e9";
                string unit = i < hb.ToneUnits.Length ? hb.ToneUnits[i] : "Hz";
                rows.Add((expr, unit));
            }
            if (rows.Count < 2) rows.Add(("2e9", "Hz"));

            vm.SetTones(rows);
            vm.MaxMixOrderExpr = hb.MaxMixOrderExpr;
        }

        // Advanced
        vm.FftOverSampleExpr = hb.FFTOverSampleExpr;
        vm.TolExpr           = hb.TolExpr;
        vm.DriveSteppingExpr = hb.DriveSteppingExpr;
        vm.GuardHarmonicExpr = hb.GuardHarmonicExpr;
        vm.LambdaExpr        = hb.LambdaExpr;
        vm.MaxIterExpr       = hb.MaxIterExpr;

        return vm;
    }
}
