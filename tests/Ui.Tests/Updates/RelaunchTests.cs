using System.Runtime.CompilerServices;

using CircuitRF.Ui;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Updates;

namespace CircuitRF.Ui.Tests.Updates;

/// <summary>
/// The Relaunch action in the Messages panel (owner request, 2026-09-06) — the button on the
/// "updated in the background" line that shuts this session down, starts the version that was
/// installed, and reopens the workspaces that were open.
///
/// <para><b>What it replaced.</b> docs/design/auto-update.md §10 refused a Relaunch button outright,
/// on the grounds that the application can be holding unsaved workspaces and a one-click relaunch
/// invites data loss to save a keystroke. That objection is answered rather than overruled: the button
/// runs the ordinary Quit, so every window is asked about its unsaved work first and any cancelled
/// prompt calls the whole thing off. These pin the parts of that which can be checked without a
/// display — the hand-off note, the successor's argument, and what the announcement actually
/// posts.</para>
/// </summary>
[Collection(AppDataRootCollection.Name)]
public sealed class RelaunchTests : IDisposable
{
    private readonly string _root;

    public RelaunchTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-relaunch-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        AppDataRoot.RedirectTo(_root);
    }

    public void Dispose()
    {
        RelaunchRequest.Handler = null;
        AppRelaunch.Launcher    = null;
        AppDataRoot.RedirectTo(null);
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private string MakeWorkspace(string name)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        string cws = Path.Combine(dir, name + ".cws");
        File.WriteAllText(cws, "{}");
        return cws;
    }

    /// <summary>A sink that keeps the action as well as the text, which the default interface
    /// implementation deliberately does not.</summary>
    private sealed class ActionSink : IMessageSink
    {
        public List<(MessageLevel Level, string Text, string? Label)> Posted { get; } = [];
        public Func<Task>? LastAction { get; private set; }

        public void Post(MessageLevel level, string text, string? filePath = null)
            => Posted.Add((level, text, null));

        public void PostAction(MessageLevel level, string text, string actionLabel, Func<Task> action)
        {
            Posted.Add((level, text, actionLabel));
            LastAction = action;
        }

        public void Clear() => Posted.Clear();
    }

    // ── the hand-off note ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The whole point of the feature: what was open comes back. Order is preserved because the
    /// FIRST workspace opens into the first window and the rest get windows of their own
    /// (App.OpenFiles), so it decides which one the user is looking at.
    /// </summary>
    [Fact]
    public void TheWorkspacesOpenAtRelaunch_AreWhatTheNextLaunchReopens()
    {
        string a = MakeWorkspace("alpha");
        string b = MakeWorkspace("beta");

        RelaunchSession.Write([a, b]);

        var taken = RelaunchSession.Take();
        Assert.NotNull(taken);
        Assert.Equal([a, b], taken);
    }

    /// <summary>
    /// <b>Consumed once.</b> A relaunch that crashes partway through reopening must not become a
    /// launch that reopens the same workspaces for ever — the note is deleted as it is read, before
    /// anything is done with it.
    /// </summary>
    [Fact]
    public void TheNoteIsAnsweredExactlyOnce()
    {
        RelaunchSession.Write([MakeWorkspace("alpha")]);

        Assert.Single(RelaunchSession.Take()!);
        Assert.Null(RelaunchSession.Take());
        Assert.False(File.Exists(RelaunchSession.FilePath));
    }

    /// <summary>An ordinary launch. Null, not empty — the difference decides whether the user's
    /// configured launch action runs.</summary>
    [Fact]
    public void AnOrdinaryLaunchHasNoNote() => Assert.Null(RelaunchSession.Take());

    /// <summary>
    /// A relaunch from a window with nothing open still records that it WAS a relaunch. Empty is not
    /// null: without the note the next launch would run the user's start-up action and open their
    /// default workspace, which is not what a restart-to-change-nothing should do.
    /// </summary>
    [Fact]
    public void ARelaunchWithNothingOpen_StillLeavesANote()
    {
        RelaunchSession.Write([]);

        string[]? taken = RelaunchSession.Take();

        Assert.NotNull(taken);
        Assert.Empty(taken);
    }

    /// <summary>
    /// The cancelled-prompt path. A user who answered "cancel" at a save dialog is still working in
    /// this session on this version; a note left behind would reopen these workspaces on top of
    /// whatever they do next time they launch.
    /// </summary>
    [Fact]
    public void CancellingTheRelaunchRemovesTheNote()
    {
        RelaunchSession.Write([MakeWorkspace("alpha")]);
        RelaunchSession.Clear();

        Assert.Null(RelaunchSession.Take());
    }

    /// <summary>
    /// A workspace deleted or unmounted between the two launches is dropped silently. The user asked
    /// to carry on where they left off, not to be told what has changed on disk since — and the
    /// alternative is an error dialog in front of a session they did not choose to start.
    /// </summary>
    [Fact]
    public void AWorkspaceThatIsNoLongerThere_IsDroppedRatherThanReported()
    {
        string gone = MakeWorkspace("gone");
        string kept = MakeWorkspace("kept");
        RelaunchSession.Write([gone, kept]);
        File.Delete(gone);

        var taken = RelaunchSession.Take();
        Assert.NotNull(taken);
        Assert.Equal([kept], taken);
    }

    /// <summary>
    /// Unreadable still means a relaunch happened. Opening nothing is the honest outcome; treating it
    /// as an ordinary launch would run the start-up action over the top of a restart the user asked
    /// for.
    /// </summary>
    [Fact]
    public void AnUnreadableNoteStillCountsAsARelaunch()
    {
        Directory.CreateDirectory(UpdatePaths.Root);
        File.WriteAllText(RelaunchSession.FilePath, "{ not json");

        string[]? taken = RelaunchSession.Take();

        Assert.NotNull(taken);
        Assert.Empty(taken);
        Assert.False(File.Exists(RelaunchSession.FilePath));
    }

    // ── the successor's wait argument ────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The argument that stops the relaunch closing the application instead of restarting it.</b>
    /// Windows holds a Mutex and Linux a flock()ed file for the life of the process, so a successor
    /// that starts while its predecessor is still shutting down takes the "second instance" branch,
    /// forwards its arguments to the instance on its way out, and exits with no window.
    /// </summary>
    [Fact]
    public void TheWaitArgumentIsReadAndStrippedFromTheCommandLine()
    {
        string[] args = [AppRelaunch.WaitForPidArgument, "4321"];

        int? pid = AppRelaunch.TakeWaitForPid(ref args);

        Assert.Equal(4321, pid);
        Assert.Empty(args);
    }

    /// <summary>
    /// It must not disturb anything else on the command line — a relaunch and a file to open can
    /// arrive together on the platforms that pass files in argv.
    /// </summary>
    [Fact]
    public void EverythingElseOnTheCommandLineSurvives()
    {
        string[] args = ["/tmp/one.cws", AppRelaunch.WaitForPidArgument, "77", "/tmp/two.csch"];

        Assert.Equal(77, AppRelaunch.TakeWaitForPid(ref args));
        Assert.Equal(["/tmp/one.cws", "/tmp/two.csch"], args);
    }

    /// <summary>
    /// A malformed pair takes its value with it. Leaving a bare number behind would hand it to the
    /// startup file scan, where it is filtered by File.Exists — by luck rather than by design.
    /// </summary>
    [Fact]
    public void AMalformedWaitArgumentConsumesItsValueAnyway()
    {
        string[] args = [AppRelaunch.WaitForPidArgument, "not-a-pid", "/tmp/one.cws"];

        Assert.Null(AppRelaunch.TakeWaitForPid(ref args));
        Assert.Equal(["/tmp/one.cws"], args);
    }

    [Fact]
    public void AnOrdinaryCommandLineCarriesNoWait()
    {
        string[] args = ["/tmp/one.cws"];

        Assert.Null(AppRelaunch.TakeWaitForPid(ref args));
        Assert.Equal(["/tmp/one.cws"], args);
    }

    /// <summary>
    /// Every reason the predecessor cannot be found means the same thing — it is already gone — and
    /// none of them may block a launch. The one thing this wait protects is a single-instance guard,
    /// which a dead process cannot be holding.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]     // no such process
    public void WaitingOnAProcessThatIsNotThere_ReturnsAtOnce(int pid)
    {
        var start = DateTime.UtcNow;

        AppRelaunch.WaitForProcessExit(pid, timeoutMs: 30_000);

        Assert.True(DateTime.UtcNow - start < TimeSpan.FromSeconds(5));
    }

    /// <summary>Waiting on ourselves would never return, so it is refused rather than attempted.</summary>
    [Fact]
    public void WaitingOnOurOwnProcess_ReturnsAtOnce()
    {
        var start = DateTime.UtcNow;

        AppRelaunch.WaitForProcessExit(Environment.ProcessId, timeoutMs: 30_000);

        Assert.True(DateTime.UtcNow - start < TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// macOS starts the successor through Launch Services for the same reason the update hand-over
    /// does — an inherited launch-time attribution pointing at a bundle the update has replaced is
    /// denied ~/Documents with no prompt (see AppRelaunchTests). The wait argument goes with it.
    /// </summary>
    [Fact]
    public void OnMacOs_TheSuccessorIsAskedForByBundle_AndCarriesTheWait()
    {
        if (!OperatingSystem.IsMacOS()) return;

        string? bundle = null;
        IReadOnlyList<string>? args = null;
        AppRelaunch.Launcher = (b, a) => { bundle = b; args = a; return true; };

        bool started = AppRelaunch.StartSuccessor(
            "/Applications/circuitRF.app/Contents/MacOS/circuitRF");

        Assert.True(started);
        Assert.Equal("/Applications/circuitRF.app", bundle);
        Assert.Equal([AppRelaunch.WaitForPidArgument, Environment.ProcessId.ToString()], args);
    }

    [Fact]
    public void NoExecutable_IsNotAStart() => Assert.False(AppRelaunch.StartSuccessor(""));

    // ── what the Messages panel is actually given ────────────────────────────────────────────

    /// <summary>
    /// With a handler installed the announcement carries the button, its caption says what it does,
    /// and the line names the version the user is being moved to.
    ///
    /// <para>The caption is the bare verb since 2026-09-10: the line beside it already names the
    /// application, and this row exists to be short.</para>
    /// </summary>
    [Fact]
    public void TheAnnouncementOffersTheRelaunch()
    {
        RelaunchRequest.Handler = () => Task.CompletedTask;
        var sink = new ActionSink();

        UpdateService.PostAnnouncement(sink, "1.0.0-beta.11", "1.0.0-beta.12");

        var (level, text, label) = Assert.Single(sink.Posted);
        Assert.Equal(MessageLevel.Info, level);
        Assert.Equal("Relaunch", label);
        Assert.Contains("1.0.0-beta.12", text);
        Assert.NotNull(sink.LastAction);
    }

    /// <summary>
    /// <b>The sentence stands on its own with no button.</b> harmonicaRF and wBond share this update
    /// machinery and install no handler; so does any headless sink. The line must therefore still
    /// tell the user what to do, which is why it says "Relaunch … to start using the version"
    /// whether or not there is something to press — the button removes a keystroke, it is not the
    /// instruction.
    /// </summary>
    [Fact]
    public void WithNoHandlerInstalled_TheLineSpellsOutTheInstruction()
    {
        RelaunchRequest.Handler = null;
        var sink = new ActionSink();

        UpdateService.PostAnnouncement(sink, "1.0.0-beta.11", "1.0.0-beta.12");

        var (level, text, label) = Assert.Single(sink.Posted);
        Assert.Equal(MessageLevel.Info, level);
        Assert.Null(label);
        Assert.Contains($"Relaunch {UpdateApp.Name} to start using the version", text);
    }

    /// <summary>
    /// <b>The line with the button is the OUTCOME and nothing else</b> (owner request, 2026-09-09 for
    /// the first shortening, 2026-09-10 for this one: once the install has finished there is nothing
    /// extra to explain, and the button is the instruction).
    ///
    /// <para>Asserted as an upper bound on the words rather than on the exact sentence, because what
    /// was asked for is a short row and not a particular phrasing. The version has to survive — it is
    /// the one fact the row carries that the button cannot — and the explanation must not creep back:
    /// no instruction clause, no mention of where the setting lives.</para>
    /// </summary>
    [Fact]
    public void TheLineWithTheButton_IsTheOutcomeAndNothingElse()
    {
        var withButton = new ActionSink();
        RelaunchRequest.Handler = () => Task.CompletedTask;
        UpdateService.PostAnnouncement(withButton, "1.0.0", "1.1.0");

        var without = new ActionSink();
        RelaunchRequest.Handler = null;
        UpdateService.PostAnnouncement(without, "1.0.0", "1.1.0");

        string withText    = withButton.Posted[0].Text;
        string withoutText = without.Posted[0].Text;

        Assert.True(withText.Length < withoutText.Length / 3,
                    $"the button's line is meant to fit beside it: \"{withText}\"");
        Assert.Contains("1.1.0", withText);
        Assert.DoesNotContain("Settings", withText);
        Assert.DoesNotContain("Relaunch", withText);   // that is the button, not the sentence
    }

    /// <summary>
    /// The word the row settles on is the one it was already showing while it worked — the download
    /// row reads "installing" during the stage and "installed" once it is done, on the same line, so
    /// the finished row is the live row with one word and one control changed.
    /// </summary>
    [Fact]
    public void TheSettledRowSaysInstalled_MatchingTheWordItShowedWhileInstalling()
    {
        RelaunchRequest.Handler = () => Task.CompletedTask;
        var row = new RecordingProgressMessage();

        UpdateService.PostAnnouncement(new ActionSink(), "1.0.0", "1.1.0", row);

        Assert.Contains("installed", row.CompletedWithAction[0].Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Owner request, 2026-09-09: the offer lands on the row the DOWNLOAD was drawn on, where the
    /// progress bar was — not on a row of its own underneath it. So when there is a live row, the
    /// announcement settles it and posts nothing at all to the sink.
    /// </summary>
    [Fact]
    public void WithALiveDownloadRow_TheAnnouncementSettlesThatRow_AndPostsNoSecondOne()
    {
        RelaunchRequest.Handler = () => Task.CompletedTask;
        var sink = new ActionSink();
        var row  = new RecordingProgressMessage();

        UpdateService.PostAnnouncement(sink, "1.0.0-beta.11", "1.0.0-beta.12", row);

        Assert.Empty(sink.Posted);
        var (level, text, label) = Assert.Single(row.CompletedWithAction);
        Assert.Equal(MessageLevel.Info, level);
        Assert.Equal("Relaunch", label);
        Assert.Contains("1.0.0-beta.12", text);
        Assert.NotNull(row.LastAction);

        // Settled through the WITH-ACTION path only: a plain Complete as well would mean the row
        // was written twice, and which write landed last would decide whether the button exists.
        Assert.Empty(row.Completed);
    }

    /// <summary>
    /// The same row, in a build with no handler installed — harmonicaRF, wBond. It settles into the
    /// sentence with no button, which is the point of the sentence standing on its own.
    /// </summary>
    [Fact]
    public void WithALiveRowAndNoHandler_TheRowSettlesIntoTheSentenceWithNoButton()
    {
        RelaunchRequest.Handler = null;
        var sink = new ActionSink();
        var row  = new RecordingProgressMessage();

        UpdateService.PostAnnouncement(sink, "1.0.0", "1.1.0", row);

        Assert.Empty(sink.Posted);
        Assert.Empty(row.CompletedWithAction);
        var (level, text) = Assert.Single(row.Completed);
        Assert.Equal(MessageLevel.Info, level);
        Assert.Contains($"Relaunch {UpdateApp.Name} to start using the version", text);
    }

    /// <summary>
    /// <b>The BUTTON decides the wording, not the row.</b> A line carrying the button reads the same
    /// whether it settles a live row or is posted on its own — the row is where it lands, not what
    /// it says.
    /// </summary>
    [Fact]
    public void TheSentenceIsTheSameOnALiveRowAsOnItsOwn()
    {
        RelaunchRequest.Handler = () => Task.CompletedTask;

        var posted = new ActionSink();
        UpdateService.PostAnnouncement(posted, "1.0.0", "1.1.0");

        var row = new RecordingProgressMessage();
        UpdateService.PostAnnouncement(new ActionSink(), "1.0.0", "1.1.0", row);

        Assert.Equal(posted.Posted[0].Text, row.CompletedWithAction[0].Text);
    }

    /// <summary>
    /// A sink that has not heard of actions loses the button and keeps the message. This is the
    /// interface default, and it is what lets the message model gain an action without every
    /// existing sink changing.
    /// </summary>
    [Fact]
    public void ASinkWithNoActionSupport_StillGetsTheMessage()
    {
        RelaunchRequest.Handler = () => Task.CompletedTask;
        var plain = new PlainSink();

        UpdateService.PostAnnouncement(plain, "1.0.0", "1.1.0");

        // The short line, since a button was offered. It no longer carries the instruction (owner
        // request, 2026-09-10) — what it must still do is arrive, and name the version, so the user
        // is not left with silence where an announcement was made.
        string posted = Assert.Single(plain.Posted);
        Assert.Contains("1.1.0", posted);
    }

    private sealed class PlainSink : IMessageSink
    {
        public List<string> Posted { get; } = [];
        public void Post(MessageLevel level, string text, string? filePath = null) => Posted.Add(text);
        public void Clear() => Posted.Clear();
    }

    // ── the message row itself ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The view's only visibility test. A caption with no callback, or a callback with no caption, is
    /// half a button and must not render as one.
    /// </summary>
    [Fact]
    public void ARowShowsAnActionButtonOnlyWhenItHasBothHalves()
    {
        Assert.True(new MessageEntry(MessageLevel.Info, "x", null, DateTime.Now,
                                     "Do it", () => Task.CompletedTask).HasAction);

        Assert.False(new MessageEntry(MessageLevel.Info, "x", null, DateTime.Now).HasAction);
        Assert.False(new MessageEntry(MessageLevel.Info, "x", null, DateTime.Now,
                                      "Do it", null).HasAction);
        Assert.False(new MessageEntry(MessageLevel.Info, "x", null, DateTime.Now,
                                      "", () => Task.CompletedTask).HasAction);
    }

    /// <summary>
    /// Owner report, 2026-09-16 (macOS): hovering the Relaunch button showed the text I-BEAM, not the
    /// arrow.
    ///
    /// <para>The cause is not macOS and not the Button. Avalonia's <c>Cursor</c> property INHERITS
    /// down the visual tree, and this button is hosted in an <c>InlineUIContainer</c> inside the
    /// row's <c>SelectableTextBlock</c>, which sets the I-beam so its text can be selected — so with
    /// no cursor of its own the button reads as text to the pointer, on every platform. The fix is
    /// the button STATING <c>Cursor="Arrow"</c>, exactly as the file-path link beside it states
    /// <c>Hand</c>; <c>StandardCursorType.Arrow</c> is what each backend maps to its own default
    /// pointer, so one declaration covers macOS, Windows and Linux.</para>
    ///
    /// <para>Scanned rather than rendered because this project has no headless Avalonia: what can be
    /// checked without a display is that the declaration is there and on the button itself, since
    /// inheritance means putting it anywhere else would not reach it.</para>
    /// </summary>
    [Fact]
    public void TheActionButton_DeclaresTheArrowCursor_RatherThanInheritingTheRowsIBeam()
    {
        string xaml = StripXamlComments(ReadRepoFile("src/Ui/Views/Messages/MessagesView.axaml"));

        int start = xaml.IndexOf("<Button Content=\"{Binding ActionLabel}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "The Messages row's action button was not found in MessagesView.axaml.");

        int end = xaml.IndexOf("/>", start, StringComparison.Ordinal);
        Assert.True(end > start, "The action button's element was not closed.");

        string button = xaml[start..end];
        Assert.Contains("Cursor=\"Arrow\"", button);
    }

    /// <summary>
    /// The quit that a relaunch rides on ends the process ON ITS OWN PASS, rather than leaving the
    /// exit to the <c>NotifyWindowCountChanged</c> that <c>WorkspaceWindow.OnClosed</c> posts.
    ///
    /// <para><b>Owner report, Windows.</b> After an automatic update, Relaunch closed circuitRF and
    /// started nothing — the new version was installed, but only the user's own next launch ever
    /// showed it. <c>ExitProcess</c> is the one place that starts the successor, and with a workspace
    /// window open it was reachable only through that posted callback, which runs at
    /// <c>DispatcherPriority.Background</c> — a dispatcher pass later. macOS sets
    /// <c>ShutdownMode.OnExplicitShutdown</c>, so the loop is still running with no windows and that
    /// pass always comes. Windows and Linux keep Avalonia's default <c>OnLastWindowClose</c>: closing
    /// the last window ends the lifetime synchronously, the main loop is told to stop while
    /// <c>QuitAsync</c> is still on the stack, and the queued callback is abandoned. The process left
    /// by returning out of <c>Main</c> with the relaunch never attempted.</para>
    ///
    /// <para><b>It was invisible everywhere else</b>, which is why a scan is worth having: everything
    /// <c>ExitProcess</c> does beyond exiting IS the relaunch, so a quit that skipped it looked
    /// completely normal on both platforms.</para>
    ///
    /// <para>Scanned rather than run, for the reason the cursor gate above gives — this project has
    /// no headless Avalonia, so there is no lifetime to close a window against. What can be checked
    /// without a display is that the call is unconditional and in the method, not queued from it.</para>
    /// </summary>
    [Fact]
    public void TheQuitPathExitsOnItsOwnPass_RatherThanThroughADispatcherPost()
    {
        string body = MethodBody(StripCsharpComments(ReadRepoFile("src/Ui/App.axaml.cs")),
                                 "private async Task QuitAsync(");

        Assert.Contains("ExitProcess();", body);

        // Not queued: a Post here is the defect, whatever priority it carries.
        Assert.DoesNotContain("Dispatcher", body);

        // Unconditional: the exit used to be guarded by `if (windows.Count == 0)`, which is false on
        // every launch that has a workspace open — that is, on every relaunch anyone would press.
        // So the call must sit at the method's own nesting level, not inside a branch.
        int call  = body.LastIndexOf("ExitProcess();", StringComparison.Ordinal);
        int depth = 0;
        for (int i = 0; i < call; i++)
        {
            if (body[i] == '{') depth++;
            else if (body[i] == '}') depth--;
        }

        Assert.True(depth == 1,
            $"ExitProcess() sits {depth - 1} block(s) deep in QuitAsync — it must run on every quit.");
    }

    /// <summary>The body of <paramref name="signature"/>'s method, brace-matched from its first
    /// <c>{</c>. Comments are the caller's to strip first: a rule stated in one would otherwise
    /// satisfy a scan the code itself failed, which is H8's own recorded trap.</summary>
    private static string MethodBody(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{signature}' was not found in the source.");

        int open = source.IndexOf('{', at);
        Assert.True(open > at, $"'{signature}' has no body.");

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }

        Assert.Fail($"'{signature}' body is unterminated.");
        return string.Empty;
    }

    /// <summary>
    /// Line and block comments out, string and char literals left alone — so a <c>"{"</c> in a
    /// message cannot unbalance the brace match above, and a comment describing the exit cannot
    /// stand in for it.
    /// </summary>
    private static string StripCsharpComments(string source)
    {
        var sb = new System.Text.StringBuilder(source.Length);

        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];

            if (c == '"' || c == '\'')
            {
                char quote = c;
                bool verbatim = quote == '"' && i > 0 && source[i - 1] == '@';
                sb.Append(c);
                for (i++; i < source.Length; i++)
                {
                    if (!verbatim && source[i] == '\\' && i + 1 < source.Length) { sb.Append(source[i]).Append(source[i + 1]); i++; continue; }
                    sb.Append(source[i]);
                    if (source[i] == quote) break;
                }
                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                sb.Append('\n');
                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                int close = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0) break;
                i = close + 1;
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static string RepoRoot([CallerFilePath] string here = "")
    {
        string? dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root walking up from this test file.");
        return dir!;
    }

    private static string ReadRepoFile(string relative)
        => File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>
    /// A comment ABOUT the declaration would pass a scan that the markup itself failed — the trap H8's
    /// own source-scan gate recorded, in XAML's comment syntax.
    /// </summary>
    private static string StripXamlComments(string xaml)
    {
        var sb = new System.Text.StringBuilder(xaml.Length);
        for (int i = 0; i < xaml.Length; i++)
        {
            if (string.CompareOrdinal(xaml, i, "<!--", 0, 4) == 0)
            {
                int close = xaml.IndexOf("-->", i + 4, StringComparison.Ordinal);
                if (close < 0) break;
                i = close + 2;
                continue;
            }
            sb.Append(xaml[i]);
        }
        return sb.ToString();
    }
}
