using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Ui.Messages;
using CircuitRF.WBond;
using CircuitRF.WBond.Mom;
using NumFlat;
using RfCore;

namespace CircuitRF.Ui.WBond;

/// <summary>One frequency's worth of the comparison — a value type, so the grid binds it directly.</summary>
/// <param name="FrequencyGhz">The point.</param>
/// <param name="LumpedInductancePh">The selected array's series L from the lumped model.</param>
/// <param name="MomInductancePh">The same from the distributed model.</param>
/// <param name="InductanceDeltaPercent">MoM relative to lumped, in percent, signed.</param>
/// <param name="LumpedCapacitanceFf">The selected array's total shunt C to the reference, lumped.</param>
/// <param name="MomCapacitanceFf">The same from the distributed model.</param>
/// <param name="CapacitanceDeltaPercent">MoM relative to lumped, in percent, signed.</param>
/// <param name="MaxAdmittanceDeltaPercent">
/// <c>max|ΔY| / max|Y|</c> over the <b>whole</b> matrix, so it does not change with the array
/// selection. A max-norm ratio rather than a per-entry relative deliberately: the off-diagonal entries
/// between distant arrays are near-cancellations, so a per-entry relative there is a large number about
/// a quantity that is physically nothing (WM-1 recorded the same trap for the capacitance sweep).
/// </param>
/// <param name="LumpedS21Db">|S| between the selected array's two terminals, 50 Ω, in dB.</param>
/// <param name="MomS21Db">The same from the distributed model.</param>
public sealed record WBondMomCompareRow(
    double FrequencyGhz,
    double LumpedInductancePh,
    double MomInductancePh,
    double InductanceDeltaPercent,
    double LumpedCapacitanceFf,
    double MomCapacitanceFf,
    double CapacitanceDeltaPercent,
    double MaxAdmittanceDeltaPercent,
    double LumpedS21Db,
    double MomS21Db);

/// <summary>
/// The whole brain of <c>Design ▸ Compare Distributed Model…</c> (brief-wbond-mom-w2 §7.3): the mesh
/// report shown <b>before</b> anything is solved, the frequency grid, and the comparison table.
///
/// <h3>Why the dialog is a table and not a plot</h3>
/// <para>The question it answers is "do the two models agree, and where do they stop agreeing?" — which
/// is a handful of numbers, not a curve. Anyone who wants the curve exports both models through
/// <see cref="WBondTouchstoneExport"/> and plots them in Data Display, which is what that surface is
/// for.</para>
///
/// <h3>The report comes first, and that is the point of the dialog existing</h3>
/// <para><see cref="WireMomMesh.Predict"/> allocates nothing, so the unknown count, the port count, the
/// memory and the predicted wait are all on screen before the Run button is pressed. The repository has
/// already paid once for a ceiling that predicted, passed, and threw twenty real minutes later; a user
/// about to wait several seconds — or several minutes — finds out here.</para>
///
/// <h3>The comparison is a subtraction, with nothing in between</h3>
/// <para>Both models publish a 2M × 2M terminal-basis admittance, every terminal referenced to the
/// ground plane at z = 0, in the order <c>G1.i, G1.o, G2.i, …</c>. That was a deliberate WM-1 decision,
/// so there is no renormalisation, no port re-mapping and no cascading here — and there must not
/// be.</para>
/// </summary>
public sealed partial class WBondMomCompareViewModel : ObservableObject
{
    private readonly WBondDesign _design;

    /// <summary>Everything Run computed, for every array — so the array selector never re-solves.</summary>
    private WBondMomComparison? _comparison;

    public WBondMomCompareViewModel(WBondDesign design)
    {
        _design = design ?? throw new ArgumentNullException(nameof(design));

        ArrayNames = [.. design.Arrays.Select(a => a.Name)];
        RefreshMeshReport();
    }

    // ------------------------------------------------------------------ inputs

    /// <summary>WM-1's measured default. Fast 8 · Balanced 24 · Accurate 48.</summary>
    [ObservableProperty] private int _segmentsPerWire = WireMomSettings.Default.TargetSegmentsPerWire;

    [ObservableProperty] private double _startGhz = 0.01;

    [ObservableProperty] private double _stopGhz = 40.0;

    [ObservableProperty] private int _points = 7;

    /// <summary>
    /// Log by default, so the dialog opens on exactly the comparison this feature was built to produce:
    /// 0.01 to 40 GHz in seven points is 0.01, 0.1, 1, 5, 10, 20, 40 GHz to within rounding.
    /// </summary>
    [ObservableProperty] private bool _logarithmic = true;

    // ------------------------------------------------------------------ outputs

    /// <summary>The mesh report, live, before anything is solved. Never empty.</summary>
    [ObservableProperty] private string _meshReport = "";

    /// <summary>The mesh's own warnings (proximity, clamped wires), one line each. May be empty.</summary>
    public ObservableCollection<string> Warnings { get; } = [];

    /// <summary>The solved result's notes — the quasi-static caveat and the capacitance note.</summary>
    public ObservableCollection<string> Notes { get; } = [];

    public ObservableCollection<WBondMomCompareRow> Rows { get; } = [];

    public string[] ArrayNames { get; }

    [ObservableProperty] private int _selectedArray;

    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private string? _errorMessage;

    /// <summary>
    /// What the run is doing right now, for the dialog's own status line.
    ///
    /// <para><b>The dialog needs this even though the Messages panel has the same thing.</b> This
    /// dialog is MODAL, so the panel is behind it and unreadable for the entire run — and the run is
    /// minutes on a large design. Both are fed from one set of observations (see
    /// <see cref="WBondBackgroundRun"/>'s <c>mirror</c>); the panel row is the record afterwards, this
    /// is what can actually be seen while waiting.</para>
    /// </summary>
    [ObservableProperty] private string _progressText = "";

    /// <summary>0–100 for the current stage, mirroring the panel's stage row.</summary>
    [ObservableProperty] private double _progressPercent;

    /// <summary>True while the running stage has no honest denominator.</summary>
    [ObservableProperty] private bool _progressIndeterminate = true;

    /// <summary>
    /// True from the moment a stop is asked for until the run actually ends. Cancellation lands at a
    /// work boundary — a matrix row, a Cholesky column, a frequency point — so on a large design this
    /// state lasts seconds and has to be visible, or the button reads as having ignored the press.
    /// </summary>
    [ObservableProperty] private bool _isCancelling;

    partial void OnIsCancellingChanged(bool value)
    {
        OnPropertyChanged(nameof(RunButtonText));
        OnPropertyChanged(nameof(IsRunButtonEnabled));
        CancelRunCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(RunButtonText));
        OnPropertyChanged(nameof(IsRunButtonEnabled));
        CancelRunCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// What the one Run/Cancel button says. The button that started the work is the one that stops it
    /// (there is nowhere in this dialog for a second control to go), so its label carries all three
    /// states rather than the code-behind swapping two of them.
    /// </summary>
    public string RunButtonText => !IsBusy ? "Run" : IsCancelling ? "Cancelling…" : "Cancel";

    /// <summary>False only while a stop is pending: there is nothing left to ask for, and pressing
    /// again must not read as a second, stronger cancel.</summary>
    public bool IsRunButtonEnabled => !IsCancelling;

    /// <summary>Live for the run in flight; null when nothing is running. It is what makes the dialog's
    /// Cancel real, and what stops a second Run being started over the first.</summary>
    private CancellationTokenSource? _runCts;

    /// <summary>The run's stop, shared by the dialog's own button, the dialog's own progress bar
    /// (right-click ▸ Cancel) and the two Messages-panel rows. Null when nothing is running.</summary>
    private RunCancellation? _cancellation;

    /// <summary>
    /// Stops the run in flight, from whichever surface asked — the Run/Cancel button, this dialog's
    /// progress bar, or a panel row's bar. All of them go through ONE
    /// <see cref="RunCancellation"/>, so the second ask is a no-op rather than a second request, and
    /// every surface sees the pending state at once.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCancelRun))]
    public void CancelRun() => _cancellation?.Cancel();

    private bool CanCancelRun() => IsBusy && !IsCancelling;

    public bool HasArraySelector => ArrayNames.Length > 1;

    partial void OnSegmentsPerWireChanged(int value) => RefreshMeshReport();

    partial void OnStartGhzChanged(double value) => RefreshMeshReport();

    partial void OnStopGhzChanged(double value) => RefreshMeshReport();

    partial void OnPointsChanged(int value) => RefreshMeshReport();

    partial void OnLogarithmicChanged(bool value) => RefreshMeshReport();

    partial void OnSelectedArrayChanged(int value) => Project();

    // ------------------------------------------------------------------ the report (§7.3 a)

    /// <summary>
    /// Rebuilds the pre-solve report. Cheap by construction — <see cref="WireMomMesh.Predict"/> walks
    /// the polylines and allocates nothing — so this can be wired straight to every input's change
    /// notification.
    /// </summary>
    public void RefreshMeshReport()
    {
        Warnings.Clear();

        if (_design.Arrays.Count == 0)
        {
            MeshReport = "This design has no wire arrays, so it has no ports to compare.";
            return;
        }

        try
        {
            var options = CurrentOptions();
            var settings = WBondTouchstoneExport.MomSettings(options);

            // A REFUSAL IS THE FIRST THING TO SHOW, not the last. Predict never refuses (it exists so a
            // number can be shown before anyone waits), so the refusal is asked for separately —
            // otherwise this panel would report a cheerful unknown count for a design that cannot be
            // solved at all, and the user would find out only after pressing Run.
            if (WireMomMesh.RefusalFor(_design, settings) is { } refusal)
            {
                MeshReport = refusal;
                return;
            }

            var report = WireMomMesh.Predict(_design, settings);
            int points = Math.Max(1, Points);

            string ladder = string.Join(" · ", WireMomSettings.Ladder.Select(r => $"{r.Name} {r.SegmentsPerWire}"));

            MeshReport =
                $"Segments per wire: {SegmentsPerWire}  ({ladder})\n" +
                $"{report.Wires} wire(s) → {report.Segments:N0} current unknowns, " +
                $"{report.Nodes:N0} charge unknowns, {report.Terminals} ports " +
                $"({string.Join(", ", WireMomMesh.TerminalNamesFor(_design))})\n" +
                report.CostSummary(points);

            // A SLOW RUN IS WARNED ABOUT, NOT REFUSED. It goes first, above the mesh's own warnings,
            // because it is the one a user is about to act on by pressing Run.
            if (WireMomMesh.SlowRunWarning(_design, points, settings) is { } slow) Warnings.Add("⚠ " + slow);

            foreach (string warning in report.Warnings) Warnings.Add("⚠ " + warning);
        }
        catch (Exception ex)
        {
            // A refusal (no ground plane, above the segment ceiling) is exactly what this panel exists
            // to surface, and it carries its own remedies — so it is shown here rather than after a wait.
            MeshReport = ex.Message;
        }
    }

    // THE PREDICTION LIVES IN WireMomCost, NOT HERE. This panel used to carry its own power-law fit of
    // the WM-2 measurements; WM-3's M1/M2/M3 made every constant in it wrong by 2-3x, and a second copy
    // of a measured model is a copy that goes stale silently. The one this shows is the one the mesh
    // report, the slow-run warning and the ceiling refusal all quote.

    // ------------------------------------------------------------------ the run (§7.3 b,c)

    public WBondTouchstoneExport.Options CurrentOptions() => new(
        Z0Ohms: 50.0,
        StartHz: StartGhz * 1e9,
        StopHz: StopGhz * 1e9,
        Points: Math.Max(1, Points),
        Logarithmic: Logarithmic,
        Model: WBondNetworkModel.Distributed,
        SegmentsPerWire: Math.Max(1, SegmentsPerWire));

    /// <summary>
    /// Runs both models on the current grid and fills <see cref="Rows"/>.
    ///
    /// <para>The solve happens on a worker thread and honours cancellation — §6.5's sizes make a
    /// several-second run normal, and a frozen window is how a user concludes a feature is broken.</para>
    /// </summary>
    public async Task RunAsync(IMessageSink? messages = null, CancellationToken cancel = default)
    {
        if (IsBusy) return;

        ErrorMessage = null;
        ProgressText = "Starting…";
        ProgressIndeterminate = true;
        ProgressPercent = 0;
        IsCancelling = false;
        IsBusy = true;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        _runCts = cts;

        // One handle for every surface that can stop this run: the dialog's Run/Cancel button, the
        // dialog's own progress bar, and the two rows this posts in the Messages panel. Saying what
        // "cancel" means here is part of the request rather than the button's job — the kernel checks
        // the token at a matrix row, a Cholesky column or a frequency point, never inside a
        // factorisation.
        var cancellation = new RunCancellation("the model comparison", () =>
        {
            ProgressText = "Stopping at the next work boundary…";
            IsCancelling = true;
            cts.Cancel();
        });
        _cancellation = cancellation;

        try
        {
            var options = CurrentOptions();
            var frequencies = WBondTouchstoneExport.BuildFrequencies(
                options.StartHz, options.StopHz, options.Points, options.Logarithmic);

            var outcome = await WBondBackgroundRun.ExecuteAsync(
                messages,
                "Comparing wirebond models",
                $"Model comparison started: the lumped and distributed models over " +
                $"{options.Points} frequency point(s), {SegmentsPerWire} segments per wire.",
                options.Points,
                run => Compare(_design, frequencies, options, cts.Token, run),
                _ => $"compared — {options.Points} frequency point(s)",
                cts.Token,
                ShowProgress,
                cancellation).ConfigureAwait(true);

            if (outcome.Cancelled)
            {
                // Deliberately NOT an error: a stop is an outcome. The table keeps whatever the previous
                // run put in it rather than being blanked, so cancelling a refinement does not throw away
                // the answer the user already had.
                //
                // The bar is pinned and made determinate as it goes: an indeterminate bar still
                // animating under the word "Stopped" is the same lie a finished row with a running bar
                // would be.
                ProgressIndeterminate = false;
                ProgressPercent       = 0;
                ProgressText          = "Stopped.";
                return;
            }

            if (outcome.Error is { } error)
            {
                _comparison = null;
                Rows.Clear();
                ErrorMessage = error;
                ProgressText = "";
                return;
            }

            var comparison = outcome.Value!;
            _comparison = comparison;

            Notes.Clear();
            foreach (string note in comparison.Notes) Notes.Add(note);

            ProgressText = "";
            Project();
        }
        finally
        {
            cancellation.Finish();
            IsBusy        = false;
            IsCancelling  = false;
            _runCts       = null;
            _cancellation = null;
        }
    }

    /// <summary>Mirrors one observation onto the dialog's own status line and bar.</summary>
    private void ShowProgress(WBondProgress p)
    {
        // A pending stop OWNS the status line until the run actually ends. Observations keep arriving
        // for as long as the work takes to reach its next boundary, and letting them overwrite
        // "Stopping…" with the name of the stage still running is how a user concludes the cancel was
        // ignored. The bar itself keeps moving — the work genuinely is still going.
        if (IsCancelling) return;

        string what = string.IsNullOrEmpty(p.Stage) ? "starting" : p.Stage;

        if (p.StageTotal > 0)
        {
            ProgressText = $"{what} — {p.StageCompleted:N0} / {p.StageTotal:N0}";
            ProgressPercent = 100.0 * p.StageCompleted / p.StageTotal;
            ProgressIndeterminate = false;
        }
        else
        {
            ProgressText = what;
            ProgressIndeterminate = true;
        }
    }

    /// <summary>
    /// Both models, on one grid — the whole comparison, with no dialog anywhere in it.
    ///
    /// <para><b>Public because the gate and the dialog must be one computation.</b> §6.6's correlation
    /// study asserts on exactly what the Compare dialog renders; two implementations that agreed would
    /// prove nothing, and two that quietly drifted apart would be worse than one.</para>
    ///
    /// <para>It also takes the frequencies as a <i>list</i> rather than as a Start/Stop/Points grid,
    /// because §6.6's own seven points — 0.01, 0.1, 1, 5, 10, 20, 40 GHz — are not a grid of either
    /// kind. See <c>src/WBond/Mom/RESOLVED.md</c>.</para>
    /// </summary>
    public static WBondMomComparison Compare(
        WBondDesign design, IReadOnlyList<double> frequenciesHz, WBondTouchstoneExport.Options options,
        CancellationToken cancel = default, WBondRunControl? run = null)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(frequenciesHz);
        ArgumentNullException.ThrowIfNull(options);

        var frequencies = frequenciesHz as double[] ?? [.. frequenciesHz];

        // THE LUMPED MODEL IS NOT GIVEN THE CONTROL, deliberately. Its own loop ticks once per frequency
        // point, and so does the distributed solve — handing it to both would count every point twice
        // against a denominator of N and leave the bar at 200%. It is milliseconds anyway; it gets a
        // stage label and nothing else.
        run?.BeginStage("computing the lumped model");
        var lumped = WBondTouchstoneExport.TerminalAdmittances(design, frequencies);

        var momResult = WBondTouchstoneExport.SolveDistributed(design, frequencies, options, null, cancel, run);

        var z0 = new Complex(options.Z0Ohms, 0.0);
        int arrays = design.Arrays.Count;
        int t = 2 * arrays;

        var snapshots = new WBondMomComparisonPoint[frequencies.Length];

        run?.BeginStage("comparing the two models", frequencies.Length);

        for (int fi = 0; fi < frequencies.Length; fi++)
        {
            cancel.ThrowIfCancellationRequested();

            double omega = 2.0 * Math.PI * frequencies[fi];

            var yLumped = lumped[fi];
            var yMom = ToMat(momResult.PortAdmittance(fi), t);

            var sLumped = RFNetwork.YToS(yLumped, z0);
            var sMom = RFNetwork.YToS(yMom, z0);

            double worstDelta = 0.0, scale = 0.0;
            for (int i = 0; i < t; i++)
                for (int j = 0; j < t; j++)
                {
                    worstDelta = Math.Max(worstDelta, (yMom[i, j] - yLumped[i, j]).Magnitude);
                    scale = Math.Max(scale, yLumped[i, j].Magnitude);
                }

            var perArray = new WBondMomComparisonArray[arrays];
            for (int k = 0; k < arrays; k++)
            {
                perArray[k] = new WBondMomComparisonArray(
                    SeriesInductanceHenries(yLumped, t, k, omega),
                    SeriesInductanceHenries(yMom, t, k, omega),
                    ShuntCapacitanceFarads(yLumped, t, k, omega),
                    ShuntCapacitanceFarads(yMom, t, k, omega),
                    Decibels(sLumped[2 * k, 2 * k + 1]),
                    Decibels(sMom[2 * k, 2 * k + 1]));
            }

            snapshots[fi] = new WBondMomComparisonPoint(
                frequencies[fi],
                scale > 0.0 ? 100.0 * worstDelta / scale : 0.0,
                perArray);

            run?.TickStage();
        }

        return new WBondMomComparison(snapshots, momResult.Notes, momResult.Report);
    }

    /// <summary>
    /// Array <i>k</i>'s series inductance, read off the terminal-basis admittance:
    /// <c>Z_series = −1 / Y[2k, 2k+1]</c>.
    ///
    /// <para>Exact for a pure series arm, and the shunt correction is negligible wherever the shunt is —
    /// at 10 MHz a ~35 fF shunt is ~455 kΩ against a ~0.1 Ω arm. <b>Both models are read the same way</b>,
    /// which is what makes the difference a difference between models rather than between extractions.</para>
    /// </summary>
    private static double SeriesInductanceHenries(Mat<Complex> y, int t, int array, double omega)
    {
        var offDiagonal = y[2 * array, 2 * array + 1];
        if (offDiagonal == Complex.Zero || omega == 0.0) return 0.0;
        return (-1.0 / offDiagonal).Imaginary / omega;
    }

    /// <summary>
    /// Array <i>k</i>'s total capacitance to the reference plane, from both its terminals.
    ///
    /// <para><b>A row sum of Y is the admittance from that port to the reference</b>: driving every port
    /// to the same potential leaves no voltage across any series element, so all that flows is the shunt.
    /// That definition is model-independent, which is the whole reason to use it here.</para>
    /// </summary>
    private static double ShuntCapacitanceFarads(Mat<Complex> y, int t, int array, double omega)
    {
        if (omega == 0.0) return 0.0;

        Complex shunt = Complex.Zero;
        for (int j = 0; j < t; j++) shunt += y[2 * array, j] + y[2 * array + 1, j];
        return shunt.Imaginary / omega;
    }

    private static double Decibels(Complex s) =>
        s.Magnitude <= 0.0 ? double.NegativeInfinity : 20.0 * Math.Log10(s.Magnitude);

    private static Mat<Complex> ToMat(Complex[] flat, int n)
    {
        var mat = new Mat<Complex>(n, n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                mat[i, j] = flat[i * n + j];
        return mat;
    }

    // ------------------------------------------------------------------ projection and copy

    /// <summary>Re-renders <see cref="Rows"/> for the selected array. No solve.</summary>
    private void Project()
    {
        Rows.Clear();
        if (_comparison is not { } comparison) return;

        foreach (var row in comparison.Rows(SelectedArray)) Rows.Add(row);
    }

    /// <summary>
    /// The table as tab-separated text — what makes it a thing that can be pasted somewhere and looked
    /// at, rather than a thing only this dialog has ever rendered.
    /// </summary>
    public string ToTabSeparated()
    {
        var sb = new StringBuilder();
        string array = ArrayNames.Length == 0 ? "" : ArrayNames[Math.Clamp(SelectedArray, 0, ArrayNames.Length - 1)];

        sb.Append(CultureInfo.InvariantCulture,
            $"# wBond model comparison — array {array}, {SegmentsPerWire} segments per wire\n");
        sb.Append("f (GHz)\tL lumped (pH)\tL MoM (pH)\tdL %\tC lumped (fF)\tC MoM (fF)\tdC %\t" +
                  "max dY/Y %\t|S21| lumped (dB)\t|S21| MoM (dB)\n");

        foreach (var r in Rows)
            sb.Append(CultureInfo.InvariantCulture,
                $"{r.FrequencyGhz:0.####}\t{r.LumpedInductancePh:0.###}\t{r.MomInductancePh:0.###}\t" +
                $"{r.InductanceDeltaPercent:0.###}\t{r.LumpedCapacitanceFf:0.####}\t" +
                $"{r.MomCapacitanceFf:0.####}\t{r.CapacitanceDeltaPercent:0.###}\t" +
                $"{r.MaxAdmittanceDeltaPercent:0.###}\t{r.LumpedS21Db:0.####}\t{r.MomS21Db:0.####}\n");

        foreach (string note in Notes) sb.Append("# ").Append(note).Append('\n');

        return sb.ToString();
    }

}

/// <summary>One frequency point of a comparison, in SI, for every array at once.</summary>
public sealed record WBondMomComparisonPoint(
    double FrequencyHz,
    double MaxAdmittanceDeltaPercent,
    WBondMomComparisonArray[] Arrays);

/// <summary>One array's quantities at one frequency, in SI — henries, farads, dB.</summary>
public sealed record WBondMomComparisonArray(
    double LumpedInductance, double MomInductance,
    double LumpedCapacitance, double MomCapacitance,
    double LumpedS21Db, double MomS21Db);

/// <summary>
/// The result of running both models on one frequency grid: the numbers, the distributed model's own
/// notes, and the mesh they came from.
///
/// <para>Per-array quantities are kept in SI and projected to the table's units by
/// <see cref="Rows"/>, so the array selector is a re-render rather than a re-solve.</para>
/// </summary>
public sealed record WBondMomComparison(
    IReadOnlyList<WBondMomComparisonPoint> Points,
    IReadOnlyList<string> Notes,
    WireMomMeshReport MeshReport)
{
    /// <summary>The table, for one array, in the units it is displayed in.</summary>
    public IReadOnlyList<WBondMomCompareRow> Rows(int array)
    {
        var rows = new List<WBondMomCompareRow>(Points.Count);

        foreach (var point in Points)
        {
            if (point.Arrays.Length == 0) continue;
            var a = point.Arrays[Math.Clamp(array, 0, point.Arrays.Length - 1)];

            rows.Add(new WBondMomCompareRow(
                point.FrequencyHz * 1e-9,
                a.LumpedInductance * 1e12,
                a.MomInductance * 1e12,
                Percent(a.MomInductance, a.LumpedInductance),
                a.LumpedCapacitance * 1e15,
                a.MomCapacitance * 1e15,
                Percent(a.MomCapacitance, a.LumpedCapacitance),
                point.MaxAdmittanceDeltaPercent,
                a.LumpedS21Db,
                a.MomS21Db));
        }

        return rows;
    }

    private static double Percent(double value, double reference) =>
        reference == 0.0 ? 0.0 : 100.0 * (value - reference) / reference;
}
