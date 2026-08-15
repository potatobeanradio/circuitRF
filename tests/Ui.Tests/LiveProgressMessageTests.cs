using System.Collections.Generic;
using System.Linq;
using CircuitRF.Engine;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.ViewModels.Dock;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The owner's ask: the "Running '&lt;schematic&gt;.csch'…" message should update itself as the run
/// progresses, with a small bar, rather than the log filling with one line per observation.
/// </summary>
public sealed class LiveProgressMessageTests
{
    // Driven with an INLINE marshaller rather than through MessagesTool's dispatcher. Avalonia's
    // Dispatcher.UIThread binds to whichever thread touches it first, which under a full parallel test
    // run is some other class's — so a test that let the mutation be Posted would queue it onto a loop
    // nobody is pumping and pass alone while failing in the suite. That is exactly the load-dependent
    // shape this repo already warns about; the marshaller seam exists so it cannot happen here.
    private static readonly System.Action<System.Action> Inline = a => a();

    private static (LiveProgressMessage Live, MessageEntry Entry) NewLive(string text = "Running 'Amp'…")
    {
        var entry = new MessageEntry(MessageLevel.Info, text, null, System.DateTime.Now)
        {
            ProgressIndeterminate = true,
            ProgressPercent       = 0,
        };
        return (new LiveProgressMessage(entry, Inline), entry);
    }

    [Fact]
    public void Update_RewritesTheSameRow_RatherThanAddingAnother()
    {
        var (live, entry) = NewLive();
        Assert.True(entry.HasProgress);

        live.Update("Running 'Amp' — SP1", "50 / 100", 50);

        // The SAME row, rewritten. This is the whole point: a 20,000-point sweep reporting several
        // times a second would otherwise bury the rest of the log under its own history.
        Assert.Same(entry, ((LiveProgressMessage)live).Entry);
        Assert.Equal("Running 'Amp' — SP1", entry.Text);
        Assert.Equal("50 / 100", entry.ProgressText);
        Assert.Equal(50, entry.ProgressValue);
        Assert.False(entry.ProgressIndeterminate);
    }

    [Fact]
    public void Update_RaisesPropertyChanged_SoAnAlreadyRenderedRowRepaints()
    {
        var (live, entry) = NewLive("start");

        var changed = new List<string>();
        entry.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        live.Update("halfway", percentComplete: 50);

        Assert.Contains(nameof(MessageEntry.Text),          changed);
        Assert.Contains(nameof(MessageEntry.TextInline),    changed);   // what the row actually binds
        Assert.Contains(nameof(MessageEntry.ProgressValue), changed);
    }

    /// <summary>
    /// The owner's ask: on completion the row KEEPS its bar and gains the outcome on the end, rather
    /// than collapsing to a bare "complete" that says nothing about what was run. The outcome lands
    /// after the COUNTER — i.e. at the true end of the row — not mid-row before the bar.
    /// </summary>
    [Fact]
    public void Finish_AppendsTheOutcome_AfterTheCounter_AndKeepsTheBar()
    {
        var (live, entry) = NewLive("Running…");

        live.Update("Running 'Amp' — DC1", "2,525 / 2,525", 100);
        live.Finish(MessageLevel.Success, "1 analysis run(s) complete");

        Assert.Equal(MessageLevel.Success, entry.Level);
        Assert.Equal("Running 'Amp' — DC1", entry.Text);
        Assert.Equal("2,525 / 2,525 - 1 analysis run(s) complete", entry.ProgressText);
        Assert.True(entry.HasProgress);
        Assert.Equal(100, entry.ProgressValue);
        Assert.False(entry.ProgressIndeterminate);
    }

    /// <summary>With no counter (indeterminate work) the outcome has nowhere else to go and appends to
    /// the text itself — the row must never lose its outcome just because it had no denominator.</summary>
    [Fact]
    public void Finish_WithNoCounter_AppendsTheOutcomeToTheTextInstead()
    {
        var (live, entry) = NewLive("Running…");

        live.Update("Running 'Amp' — HB1…", indeterminate: true);
        live.Finish(MessageLevel.Success, "1 analysis run(s) complete");

        Assert.Null(entry.ProgressText);
        Assert.Equal("Running 'Amp' — HB1… - 1 analysis run(s) complete", entry.Text);
    }

    /// <summary>An indeterminate bar is pinned FULL on finish — a finished row still showing an
    /// animating bar reads as a run that never stopped.</summary>
    [Fact]
    public void Finish_PinsAnIndeterminateBarFull()
    {
        var (live, entry) = NewLive("Running…");
        Assert.True(entry.ProgressIndeterminate);

        live.Finish(MessageLevel.Success, "done");

        Assert.False(entry.ProgressIndeterminate);
        Assert.Equal(100, entry.ProgressValue);
    }

    /// <summary>A cancelled run keeps the count it reached — "…1,194 / 2,525 - cancelled" is the one
    /// thing worth knowing about a run somebody stopped.</summary>
    [Fact]
    public void Finish_AfterACancel_KeepsThePartialCount()
    {
        var (live, entry) = NewLive("Running…");

        live.Update("Running 'Amp' — DC1", "1,194 / 2,525", 47.3);
        live.Finish(MessageLevel.Warning, "cancelled, no results written");

        Assert.Contains("1,194 / 2,525", entry.ProgressText);
        Assert.Contains("cancelled", entry.ProgressText);
        Assert.Equal(47.3, entry.ProgressValue);
    }

    /// <summary>
    /// Owner request, 2026-08-14: "the simulation progress bar glyph should be removed from the
    /// Messages window (both EM and Analysis) after the simulation is complete. The text that says
    /// simulation is complete should remain." <c>keepBar: false</c> is what
    /// <see cref="WorkspaceViewModel"/>'s Analysis-run and EM-run completions now pass — the outcome
    /// is still appended exactly like the default (<c>true</c>) path, but the bar itself (and the
    /// indeterminate spinner) goes.
    /// </summary>
    [Fact]
    public void Finish_WithKeepBarFalse_DropsTheBar_ButKeepsTheAppendedText()
    {
        var (live, entry) = NewLive("Running…");

        live.Update("Running 'Amp' — DC1", "2,525 / 2,525", 100);
        live.Finish(MessageLevel.Success, "1 analysis run(s) complete", keepBar: false);

        Assert.Equal(MessageLevel.Success, entry.Level);
        Assert.Equal("Running 'Amp' — DC1", entry.Text);
        Assert.Equal("2,525 / 2,525 - 1 analysis run(s) complete", entry.ProgressText);
        Assert.True(entry.HasProgressText);   // the text survives...
        Assert.False(entry.HasProgress);      // ...but the ProgressBar itself (bound to HasProgress) is gone
        Assert.False(entry.ProgressIndeterminate);
    }

    /// <summary>Same as above for an indeterminate run (no counter) — the outcome lands in
    /// <see cref="MessageEntry.Text"/> instead, and the bar still drops.</summary>
    [Fact]
    public void Finish_WithKeepBarFalse_AndNoCounter_DropsTheBar()
    {
        var (live, entry) = NewLive("Running…");

        live.Update("Running 'Amp' — HB1…", indeterminate: true);
        live.Finish(MessageLevel.Success, "1 analysis run(s) complete", keepBar: false);

        Assert.Equal("Running 'Amp' — HB1… - 1 analysis run(s) complete", entry.Text);
        Assert.False(entry.HasProgress);
        Assert.False(entry.ProgressIndeterminate);
    }

    [Fact]
    public void Complete_ReplacesTheText_AndDropsTheBar()
    {
        var (live, entry) = NewLive("Running…");

        live.Update("Running", "90 / 100", 90);
        live.Complete(MessageLevel.Error, "Run failed unexpectedly.");

        Assert.Equal(MessageLevel.Error, entry.Level);
        Assert.Equal("Run failed unexpectedly.", entry.Text);
        Assert.False(entry.HasProgress);
        Assert.False(entry.HasProgressText);   // the stale counter goes with the bar
    }

    [Fact]
    public void Update_ClampsOutOfRangePercentages()
    {
        var (live, entry) = NewLive();

        live.Update("over", percentComplete: 140);
        Assert.Equal(100, entry.ProgressValue);

        live.Update("under", percentComplete: -5);
        Assert.Equal(0, entry.ProgressValue);
    }

    [Fact]
    public void AnOrdinaryMessage_CarriesNoBar()
        => Assert.False(new MessageEntry(MessageLevel.Info, "Wrote netlist", null, System.DateTime.Now)
                        .HasProgress);

    /// <summary>
    /// A sink with no live-message support (a test fake, a headless driver) still reports the start
    /// and the end of the operation through the interface default — it simply has no bar in between.
    /// Never silently swallows the outcome.
    /// </summary>
    [Fact]
    public void ASinkWithoutLiveSupport_StillPostsTheStartAndTheOutcome()
    {
        var sink = new PlainSink();
        var live = ((IMessageSink)sink).BeginProgress("Running…");

        live.Update("ignored — this sink cannot rewrite a line", "50 / 100", 50);
        live.Finish(MessageLevel.Success, "1 analysis run(s) complete");

        Assert.Equal(["Running…", "1 analysis run(s) complete"], sink.Texts);
        Assert.Equal([MessageLevel.Info, MessageLevel.Success], sink.Levels);
    }

    // ── The counter, and why it is kept out of the message text ───────────────

    /// <summary>
    /// The counter is NOT space-padded. Padding gives a constant character count, which is not a
    /// constant WIDTH in a proportional UI font (a space is about half a digit) — that is why the row
    /// still twitched with it. The row keeps the counter steady by right-aligning it in a fixed-width
    /// box instead, and a PadLeft here would fight that.
    /// </summary>
    [Fact]
    public void FormatCounter_IsUnpadded_SoRightAlignmentCanDoTheWork()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            // Pins the culture because production deliberately uses the CURRENT one — a German user
            // reads "2.525" — so asserting literal separators on whatever culture the machine happens
            // to have would be a test that only passes in one country.
            Assert.Equal("1 / 2,525", WorkspaceViewModel.FormatCounter(1, 2525));
            Assert.Equal("1,194 / 2,525", WorkspaceViewModel.FormatCounter(1194, 2525));
            Assert.Equal("2,525 / 2,525", WorkspaceViewModel.FormatCounter(2525, 2525));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    /// <summary>
    /// THE regression gate for the bar sitting still. The bar renders immediately after
    /// <see cref="MessageEntry.Text"/>, so anything in there that grows during a run shoves it. This
    /// drives the real reporting path across a run whose count crosses several powers of ten and
    /// asserts the text never moves — every changing character lands in the counter instead.
    /// </summary>
    [Fact]
    public void ReportRunProgress_KeepsTheChangingCountOutOfTheText_SoTheBarCannotBePushed()
    {
        var (live, entry) = NewLive();
        var texts = new HashSet<string>();

        foreach (long done in new long[] { 1, 9, 99, 1194, 2525 })
        {
            WorkspaceViewModel.ReportRunProgress(live, "Amp.csch", new RunProgress("DC1", done, 2525));
            texts.Add(entry.Text);
        }

        Assert.Equal("Running 'Amp.csch'", Assert.Single(texts));
        Assert.Equal("2,525 / 2,525", entry.ProgressText);
    }

    /// <summary>The stage (analysis) name is deliberately not shown — it would change mid-run on a
    /// multi-analysis run, putting a width change back on the bar's left.</summary>
    [Fact]
    public void ReportRunProgress_TextIsUnchanged_AcrossAStageChange()
    {
        var (live, entry) = NewLive();

        WorkspaceViewModel.ReportRunProgress(live, "Amp.csch", new RunProgress("DC1", 10, 100));
        string afterDc = entry.Text;

        WorkspaceViewModel.ReportRunProgress(live, "Amp.csch", new RunProgress("SP1", 60, 100));

        Assert.Equal(afterDc, entry.Text);
        Assert.DoesNotContain("DC1", entry.Text);
        Assert.DoesNotContain("SP1", entry.Text);
    }

    /// <summary>Indeterminate work has no counter to show — the row must not leave a stale one behind
    /// from an earlier determinate stage.</summary>
    [Fact]
    public void ReportRunProgress_WithNoDenominator_ClearsTheCounter()
    {
        var (live, entry) = NewLive();

        WorkspaceViewModel.ReportRunProgress(live, "Amp.csch", new RunProgress("DC1", 5, 100));
        Assert.True(entry.HasProgressText);

        WorkspaceViewModel.ReportRunProgress(live, "Amp.csch", new RunProgress("HB1", 0, 0));
        Assert.False(entry.HasProgressText);
        Assert.True(entry.ProgressIndeterminate);
    }

    private sealed class PlainSink : IMessageSink
    {
        public List<string>       Texts  { get; } = [];
        public List<MessageLevel> Levels { get; } = [];

        public void Post(MessageLevel level, string text, string? filePath = null)
        {
            Levels.Add(level);
            Texts.Add(text);
        }

        public void Clear() { Texts.Clear(); Levels.Clear(); }
    }
}
