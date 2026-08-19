using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// <c>Design ▸ Compare Distributed Model…</c> — a shell over
/// <see cref="WBondMomCompareViewModel"/> and deliberately nothing more (brief-wbond-mom-w2 §7.3).
///
/// <para><b>The Run happens off the UI thread and honours cancel.</b> That much is not optional: a
/// 40-wire design at 24 segments is a several-second run and a frozen window is how a user concludes
/// a feature is broken. Closing the dialog cancels the run.</para>
///
/// <para><b>The progress bar is inside the dialog, not only in the Messages panel</b>, and that is
/// forced by the dialog being modal: the panel's two live rows are posted exactly as an EM run's are,
/// but they are behind this window for the entire run and cannot be read. They are the record
/// afterwards; the bar and the stage line here are what can be seen while waiting. Both are driven by
/// the same observations — see <see cref="WBondBackgroundRun"/>.</para>
///
/// <para>Everything else here is scope this dialog does not have — no plot, no Data Display document,
/// no docking panel, no persisted settings.</para>
/// </summary>
public partial class WBondMomCompareDialog : Window
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly IMessageSink? _messages;

    public WBondMomCompareDialog() : this(new WBondDesign(), null) { }

    public WBondMomCompareDialog(WBondDesign design, IMessageSink? messages = null)
    {
        InitializeComponent();

        _messages = messages;
        Model = new WBondMomCompareViewModel(design);
        DataContext = Model;

        // The Log/Linear pair is one two-way binding and one mirror: RadioButton groups do not bind a
        // single bool cleanly in both directions, so Log carries the binding and Linear follows it.
        LinRadio.IsChecked = !Model.Logarithmic;
        LinRadio.IsCheckedChanged += (_, _) =>
        {
            if (LinRadio.IsChecked == true) Model.Logarithmic = false;
        };

        Closed += (_, _) => _cancel.Cancel();
    }

    internal WBondMomCompareViewModel Model { get; }

    public static Task ShowAsync(Window owner, WBondDesign design, IMessageSink? messages = null) =>
        new WBondMomCompareDialog(design, messages).ShowDialog(owner);

    /// <summary>
    /// Run, and — while a run is in flight — Cancel.
    ///
    /// <para><b>The button that started the work is the one that stops it</b>, the same arrangement the
    /// EM panel's Simulate/Cancel uses. A separate disabled-Run-plus-Cancel pair would leave two
    /// controls saying one thing, and there is nowhere in this dialog for the second to go.</para>
    /// </summary>
    private async void OnRun(object? sender, RoutedEventArgs e)
    {
        if (Model.IsBusy)
        {
            // What "cancel" means here — the kernel checks the token at work boundaries, never inside
            // a factorisation — is said by the request itself (WBondMomCompareViewModel.RunAsync's
            // handle), so this surface and the bar's context menu cannot word it differently.
            Model.CancelRun();
            return;
        }

        // THE SAME GATE AN EXPORT TAKES, and it lives here rather than in the view model on purpose:
        // it is a concurrency rule about this process's memory, not a property of the comparison, and
        // a process-wide latch inside the view model would make two tests that each run a comparison
        // fail each other under xUnit's parallel class execution.
        //
        // This dialog is modal, so it cannot collide with itself — but a Touchstone export runs in the
        // BACKGROUND and stays running while this is opened, and two runs would each size their thread
        // count against the whole memory budget. See WBondPublishCommands.
        if (!WBondPublishCommands.TryBeginRun())
        {
            Model.ErrorMessage = "A wirebond computation is already running.";
            return;
        }

        // The button's own label and enablement follow the view model (RunButtonText /
        // IsRunButtonEnabled), not this handler: the stop can also arrive from this dialog's progress
        // bar or from a Messages-panel row, and a label maintained here would not see those.
        Cursor = new Cursor(StandardCursorType.Wait);
        try
        {
            await Model.RunAsync(_messages, _cancel.Token);
        }
        finally
        {
            Cursor = Cursor.Default;
            WBondPublishCommands.EndRun();
        }
    }

    /// <summary>
    /// Tab-separated, which is what makes the table a thing that can be pasted into a spreadsheet or a
    /// note rather than a thing only this dialog has ever rendered.
    /// </summary>
    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try { await clipboard.SetTextAsync(Model.ToTabSeparated()); }
        catch (Exception) { /* a clipboard that refuses is not worth failing the dialog over */ }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
