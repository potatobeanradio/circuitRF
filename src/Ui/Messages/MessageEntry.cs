using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Messages;

public enum MessageTimestampMode { Time, DateTime, None }

/// <summary>App-wide message-timestamp display mode. Set at startup from AppPreferences and by the
/// Settings dialog; MessageEntry.TimeText reads it. ModeChanged lets the Messages view re-render.</summary>
public static class MessageDisplay
{
    private static MessageTimestampMode _mode = MessageTimestampMode.Time;
    public static MessageTimestampMode Mode
    {
        get => _mode;
        set { if (_mode == value) return; _mode = value; ModeChanged?.Invoke(null, EventArgs.Empty); }
    }
    public static event EventHandler? ModeChanged;
}

/// <summary>
/// A single message in the Messages region. FilePath (nullable) enables the clickable
/// "reveal in OS file manager" feature — any message with a file path shows it as an
/// underlined link that opens the OS file manager (Finder/Explorer/xdg-open).
///
/// <para><b>Observable, not a record, because a message can be LIVE.</b> A long run's own line
/// updates in place as it progresses (see <see cref="IProgressMessage"/>) — text, level and progress
/// all change on the row that is already on screen. A new immutable entry per observation would
/// scroll a fresh line into the log several times a second, which is a worse way to say the same
/// thing. Every other message is posted once and never touched again, so it behaves exactly as the
/// record did.</para>
/// </summary>
public sealed partial class MessageEntry : ObservableObject
{
    private MessageLevel _level;
    private string       _text;
    private string?      _progressText;
    private double?      _progressPercent;
    private bool         _progressIndeterminate;
    private RunCancellation? _cancellation;

    public MessageEntry(MessageLevel level, string text, string? filePath, DateTime timestamp)
    {
        _level    = level;
        _text     = text;
        FilePath  = filePath;
        Timestamp = timestamp;
    }

    public MessageLevel Level
    {
        get => _level;
        internal set => SetProperty(ref _level, value);
    }

    public string Text
    {
        get => _text;
        internal set { if (SetProperty(ref _text, value)) OnPropertyChanged(nameof(TextInline)); }
    }

    /// <summary>
    /// The changing tail of a live message — the "1,194 / 2,525" counter — kept SEPARATE from
    /// <see cref="Text"/> and rendered AFTER the progress bar.
    ///
    /// <para>This is what stops the bar moving. A counter inside <see cref="Text"/> sits before the
    /// bar, so every time it grows it shoves the bar sideways; out here it is the last thing on the
    /// row and its own width changes push nothing. Null on an ordinary message and on work with no
    /// honest denominator.</para>
    /// </summary>
    public string? ProgressText
    {
        get => _progressText;
        internal set { if (SetProperty(ref _progressText, value)) OnPropertyChanged(nameof(HasProgressText)); }
    }

    public bool HasProgressText => !string.IsNullOrEmpty(_progressText);

    public string?  FilePath  { get; }
    public DateTime Timestamp { get; }

    /// <summary>0–100 while this message is showing progress; null for an ordinary message (and once
    /// a live one completes, so a finished run's line carries no leftover bar).</summary>
    public double? ProgressPercent
    {
        get => _progressPercent;
        internal set
        {
            if (!SetProperty(ref _progressPercent, value)) return;
            OnPropertyChanged(nameof(HasProgress));
            OnPropertyChanged(nameof(ProgressValue));
        }
    }

    /// <summary>True while the work has no honest denominator — a single HB solve is one Newton loop,
    /// not N steps, and a bar that invents a fraction for it would be lying about the wait.</summary>
    public bool ProgressIndeterminate
    {
        get => _progressIndeterminate;
        internal set => SetProperty(ref _progressIndeterminate, value);
    }

    public bool   HasProgress   => _progressPercent is not null;
    public double ProgressValue => _progressPercent ?? 0;

    /// <summary>
    /// The operation this row's bar is drawing, when it can be stopped — what the bar's right-click
    /// Cancel acts on. Null on an ordinary message and on a run that offers no cancellation.
    ///
    /// <para>Two rows of ONE run (a sweep row and a stage row) hold the SAME handle, so cancelling
    /// from either stops the whole computation rather than half of it.</para>
    /// </summary>
    public RunCancellation? Cancellation
    {
        get => _cancellation;
        internal set
        {
            if (ReferenceEquals(_cancellation, value)) return;
            if (_cancellation is not null) _cancellation.StateChanged -= OnCancellationStateChanged;
            _cancellation = value;
            if (_cancellation is not null) _cancellation.StateChanged += OnCancellationStateChanged;
            OnCancellationStateChanged(this, EventArgs.Empty);
        }
    }

    private void OnCancellationStateChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(CanCancelRun));
        OnPropertyChanged(nameof(CancelTooltip));
    }

    /// <summary>Whether the bar's Cancel would do anything right now — false for a row with no
    /// cancellable operation, for one already asked to stop, and for one whose run has settled.</summary>
    public bool CanCancelRun => _cancellation?.CanCancel == true;

    /// <summary>
    /// Why the menu item is enabled or not, said in full. A disabled item with no explanation is the
    /// thing this codebase has already been told twice not to ship — and the enabled case has
    /// something to say too, since cancellation lands at a work boundary rather than instantly.
    /// </summary>
    public string CancelTooltip => _cancellation switch
    {
        null                                     => "This operation cannot be stopped once it has started.",
        { IsFinished: true }                     => "This operation has already finished.",
        { IsCancellationRequested: true } c      => $"Already stopping {c.What} — it ends at the next work boundary.",
        { } c                                    => $"Stop {c.What}. It ends at the next work boundary and writes nothing.",
    };

    public string TimeText => MessageDisplay.Mode switch
    {
        MessageTimestampMode.None     => "",
        MessageTimestampMode.DateTime => Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
        _                             => Timestamp.ToString("HH:mm:ss"),
    };

    /// <summary>Message text with leading + trailing gaps, for inline rendering between the
    /// timestamp and the (separately clickable) file-path link.</summary>
    public string TextInline => "  " + Text + "  ";

    public static MessageEntry Info(string text, string? filePath = null)
        => new(MessageLevel.Info, text, filePath, DateTime.Now);

    public static MessageEntry Success(string text, string? filePath = null)
        => new(MessageLevel.Success, text, filePath, DateTime.Now);

    public static MessageEntry Warning(string text, string? filePath = null)
        => new(MessageLevel.Warning, text, filePath, DateTime.Now);

    public static MessageEntry Error(string text, string? filePath = null)
        => new(MessageLevel.Error, text, filePath, DateTime.Now);
}
