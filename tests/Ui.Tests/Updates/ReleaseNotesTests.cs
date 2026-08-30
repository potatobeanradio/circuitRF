using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Ui;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.Updates;
using Xunit;

namespace CircuitRF.Ui.Tests.Updates;

/// <summary>
/// The Release Notes dialog's three testable halves — when it opens, what it is handed, and how the
/// text is read.
///
/// <para>The window itself is not under test: <c>Ui.Tests</c> calls no Avalonia runtime API, which is
/// exactly why the gate, the fetch and the parser are separate framework-free types rather than
/// private methods of the dialog.</para>
/// </summary>
public class ReleaseNotesGateTests
{
    private const string Current = "1.0.0-beta.5";

    /// <summary>
    /// The owner's hardest rule: a machine on which circuitRF has never run must not open with the
    /// release notes for the version it has just installed — even with the setting on, which is the
    /// shipped default and therefore the case that would actually happen.
    /// </summary>
    [Fact]
    public void CleanSystem_NeverShows_EvenWithThePreferenceOn()
    {
        ReleaseNotesDecision d = ReleaseNotesGate.Decide(
            installationExisted: false, shownFor: null, Current, showPreference: true);

        Assert.Equal(ReleaseNotesDecision.RecordSilently, d);
    }

    /// <summary>
    /// ...and it records the version while showing nothing, so the SECOND launch on that clean system
    /// does not become the first showing. Without this, "never on a clean install" would only hold for
    /// one launch.
    /// </summary>
    [Fact]
    public void CleanSystem_RecordsSoTheSecondLaunchIsAlsoSilent()
    {
        ReleaseNotesDecision second = ReleaseNotesGate.Decide(
            installationExisted: true, shownFor: Current, Current, showPreference: true);

        Assert.Equal(ReleaseNotesDecision.None, second);
    }

    /// <summary>An existing installation running a version it has not seen the notes for: show them.</summary>
    [Fact]
    public void ExistingInstallation_ShowsOncePerVersion()
    {
        Assert.Equal(ReleaseNotesDecision.Show, ReleaseNotesGate.Decide(true, "1.0.0-beta.4", Current, true));
        Assert.Equal(ReleaseNotesDecision.None, ReleaseNotesGate.Decide(true, Current, Current, true));
    }

    /// <summary>
    /// An existing installation with nothing recorded — the launch of the very first build to carry
    /// this feature. It shows, and that is the point of tracking "did the installation exist" at all:
    /// a null record alone cannot tell this case from a clean install.
    /// </summary>
    [Fact]
    public void ExistingInstallation_WithNothingRecorded_Shows()
        => Assert.Equal(ReleaseNotesDecision.Show, ReleaseNotesGate.Decide(true, null, Current, true));

    /// <summary>
    /// Turned off: nothing is shown, but the version is still RECORDED. Turning the setting back on
    /// must offer the next version's notes, not replay every version skipped in between.
    /// </summary>
    [Fact]
    public void PreferenceOff_RecordsRatherThanAccumulatingABacklog()
    {
        Assert.Equal(ReleaseNotesDecision.RecordSilently,
                     ReleaseNotesGate.Decide(true, "1.0.0-beta.4", Current, showPreference: false));

        // ...and with it back on, the version just recorded is not re-offered.
        Assert.Equal(ReleaseNotesDecision.None,
                     ReleaseNotesGate.Decide(true, Current, Current, showPreference: true));
    }

    /// <summary>A build with no readable version has nothing to look up; it does nothing at all.</summary>
    [Fact]
    public void NoVersion_DoesNothing()
        => Assert.Equal(ReleaseNotesDecision.None, ReleaseNotesGate.Decide(true, null, "", true));
}

/// <summary>
/// The preference itself, against a real redirected state directory — the same collection and the
/// same reasoning as <c>UpdatePolicyAndPreferencesTests</c>, since <see cref="AppDataRoot"/> is
/// process-global.
/// </summary>
[Collection(AppDataRootCollection.Name)]
public sealed class ReleaseNotesPreferenceTests : IDisposable
{
    private readonly string _root;

    public ReleaseNotesPreferenceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-relnotes-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        AppDataRoot.RedirectTo(_root);
    }

    public void Dispose()
    {
        AppDataRoot.RedirectTo(null);
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Absence IS the default here too: no preferences.json at all reads ON, with no first-run
    /// seeding. Same idiom as <c>AutomaticUpdates</c>, and pinned for the same reason — a seeded file
    /// would make "never chosen" and "chosen on" indistinguishable.
    /// </summary>
    [Fact]
    public void FreshInstall_ShowReleaseNotesIsOn_WithNoFileAtAll()
    {
        Assert.False(AppPreferencesIo.FileExists);
        Assert.True(ReleaseNotesGate.ShowPreference);
    }

    [Fact]
    public void TheSettingRoundTrips()
    {
        ReleaseNotesGate.SetShowPreference(false);
        Assert.False(ReleaseNotesGate.ShowPreference);

        ReleaseNotesGate.SetShowPreference(true);
        Assert.True(ReleaseNotesGate.ShowPreference);
    }

    /// <summary>
    /// The shown-for mark is in the updater's STATE file, not in preferences.json. It is bookkeeping,
    /// and <c>UpdateState</c>'s own doc-comment is emphatic about which of the two files that goes in.
    /// </summary>
    [Fact]
    public void TheShownMarkGoesInStateJson_NotPreferences()
    {
        ReleaseNotesGate.MarkShown("1.0.0-beta.5");

        Assert.Equal("1.0.0-beta.5", UpdateStateIo.Load().ReleaseNotesShownFor);
        Assert.Contains("release_notes_shown_for", File.ReadAllText(UpdatePaths.StateFile));

        if (AppPreferencesIo.FileExists)
        {
            string prefs = File.ReadAllText(Path.Combine(_root, "preferences.json"));
            Assert.DoesNotContain("release_notes_shown_for", prefs);
        }
    }

    /// <summary>
    /// The release notes are an outbound network call, so the update subsystem's OVERRIDES bind them
    /// too. An administrator who dropped a <c>no-auto-update</c> file said this binary does not contact
    /// the update host; a second path that fetches from it anyway is the "override honoured in one
    /// place and forgotten in another" failure <c>UpdatePolicy</c> exists to prevent.
    /// </summary>
    [Fact]
    public void AnAdministratorKillSwitch_StopsTheFetchAsWellAsTheUpdate()
    {
        try
        {
            Environment.SetEnvironmentVariable(UpdatePolicy.EnvironmentVariable, "1");

            Assert.False(ReleaseNotesGate.NetworkPermitted);

            // ...and it is RECORDED rather than left pending, so lifting the policy later offers the
            // next version rather than replaying every version installed while it was in force.
            Assert.Equal(ReleaseNotesDecision.RecordSilently, ReleaseNotesGate.Resolve());
        }
        finally { Environment.SetEnvironmentVariable(UpdatePolicy.EnvironmentVariable, null); }
    }

    /// <summary>
    /// The plain preference is NOT an override. A user who turned automatic updates off still installs
    /// versions by hand, and the Settings checkbox promises them the notes by not being a sub-item of
    /// that one.
    /// </summary>
    [Fact]
    public void TurningAutomaticUpdatesOff_DoesNotSuppressReleaseNotes()
    {
        AppPreferencesIo.Update(p => p.AutomaticUpdates = false);
        Assert.True(ReleaseNotesGate.NetworkPermitted);
    }

    /// <summary>
    /// The whole clean-install rule rests on this probe answering "no" on a directory nothing has
    /// written to yet, and "yes" once either file exists.
    /// </summary>
    [Fact]
    public void CaptureAtStartup_SeesAnEmptyStateDirectoryAsAFreshInstall()
    {
        Assert.False(AppPreferencesIo.FileExists);
        Assert.False(File.Exists(UpdatePaths.StateFile));

        // The real capture is one-shot per process, so the predicate it uses is what is asserted here.
        Assert.False(AppPreferencesIo.FileExists || File.Exists(UpdatePaths.StateFile));

        UpdateStateIo.Update(s => s.LastCheckUtc = DateTime.UtcNow);
        Assert.True(AppPreferencesIo.FileExists || File.Exists(UpdatePaths.StateFile));
    }
}

/// <summary>Which release's notes the dialog is handed, and what happens when there are none.</summary>
public class ReleaseNotesFetcherTests
{
    private const string Browse = "https://github.com/x/y/releases";

    private static ReleaseInfo Release(string tag, string body, bool draft = false)
    {
        Assert.True(SemanticVersion.TryParse(tag, out SemanticVersion? v));
        return new ReleaseInfo(tag, v!, v!.IsPreRelease, draft, [], body);
    }

    /// <summary>
    /// The RUNNING version, not the newest one. The feed's newest release may be one this machine has
    /// not been offered yet, and its notes would describe an application the user is not running.
    /// </summary>
    [Fact]
    public void PicksTheRunningVersion_NotTheNewest()
    {
        ReleaseNotesResult r = ReleaseNotesFetcher.Select(
            [Release("v1.0.0", "newest"), Release("v1.0.0-beta.4", "mine")],
            "1.0.0-beta.4", Browse);

        Assert.Equal(ReleaseNotesOutcome.Found, r.Outcome);
        Assert.Equal("mine", r.Markdown);
    }

    /// <summary>
    /// Matched on parsed version rather than tag text, so a <c>v</c> prefix and a <c>1.0</c> spelling
    /// both resolve — the same normalisation trap <c>ReleaseInfo.VersionText</c> documents.
    /// </summary>
    [Theory]
    [InlineData("v1.0.0-beta.4", "1.0.0-beta.4")]
    [InlineData("1.0.0-beta.4",  "1.0.0-beta.4")]
    [InlineData("v1.0",          "1.0.0")]
    public void MatchesOnVersion_NotOnTagText(string tag, string running)
        => Assert.Equal(ReleaseNotesOutcome.Found,
                        ReleaseNotesFetcher.Select([Release(tag, "notes")], running, Browse).Outcome);

    /// <summary>
    /// <b>Any release, not just a recent one.</b> The version asked about is whichever one the user has
    /// installed, which need not be near the top of the list — someone can install an older build by
    /// hand. Verified against the live feed at the time of writing (all four published releases resolve,
    /// in both their bare and <c>v</c>-prefixed spellings); pinned here on the shape rather than on the
    /// network, since a test must not depend on what is published today.
    /// </summary>
    [Fact]
    public void FindsAReleaseDeepInTheList_NotOnlyTheNewest()
    {
        ReleaseInfo[] feed =
        [
            Release("v2.0.0",        "newest"),
            Release("1.9.0",         "next"),
            Release("v1.1.0-beta.2", "beta two"),
            Release("v1.1.0-beta.1", "beta one"),
            Release("1.0",           "the oldest"),
        ];

        foreach ((string running, string expected) in new[]
                 {
                     ("2.0.0",        "newest"),
                     ("1.9.0",        "next"),
                     ("1.1.0-beta.2", "beta two"),
                     ("1.1.0-beta.1", "beta one"),
                     ("1.0.0",        "the oldest"),   // tagged "1.0"; SemanticVersion normalises it
                 })
        {
            ReleaseNotesResult r = ReleaseNotesFetcher.Select(feed, running, Browse);
            Assert.Equal(ReleaseNotesOutcome.Found, r.Outcome);
            Assert.Equal(expected, r.Markdown);
        }
    }

    /// <summary>
    /// ...and the list actually asked for is the 100-entry page, not GitHub's silent 30-entry default,
    /// which is what makes "any release" true rather than "any of the last thirty".
    /// </summary>
    [Fact]
    public void TheFeedIsAskedForAFullPage()
    {
        string paged = ReleaseNotesFetcher.Paged(GitHubReleasesFeed.DefaultApiUrl);

        Assert.Equal(GitHubReleasesFeed.DefaultApiUrl + "?per_page=100", paged);

        // Same scheme and host, so the allow-list is unaffected — the paged URL is what gets fetched.
        Assert.True(FeedUrlAllowList.IsAcceptable(paged));

        // Idempotent, and it appends rather than replacing a query a re-pointed feed already carries.
        Assert.Equal(paged, ReleaseNotesFetcher.Paged(paged));
        Assert.Equal("https://api.github.com/repos/a/b/releases?x=1&per_page=100",
                     ReleaseNotesFetcher.Paged("https://api.github.com/repos/a/b/releases?x=1"));
    }

    /// <summary>A draft is visible only to its publisher, so matching one would show unpublished text.</summary>
    [Fact]
    public void SkipsDrafts()
        => Assert.Equal(ReleaseNotesOutcome.NotPublished,
                        ReleaseNotesFetcher.Select([Release("v1.0.0", "hidden", draft: true)],
                                                   "1.0.0", Browse).Outcome);

    [Fact]
    public void AnEmptyBodyIsNotPublishedNotes()
        => Assert.Equal(ReleaseNotesOutcome.NotPublished,
                        ReleaseNotesFetcher.Select([Release("v1.0.0", "   ")], "1.0.0", Browse).Outcome);

    [Fact]
    public void NoMatchingRelease_IsNotPublished()
        => Assert.Equal(ReleaseNotesOutcome.NotPublished,
                        ReleaseNotesFetcher.Select([Release("v0.9.0", "old")], "1.0.0", Browse).Outcome);

    /// <summary>
    /// The link offered on a failure is DERIVED from the feed URL, so the page the user is sent to and
    /// the endpoint we failed to read can never name different repositories.
    /// </summary>
    [Fact]
    public void BrowseUrl_IsDerivedFromTheFeedUrl()
        => Assert.Equal("https://github.com/potatobeanradio/circuitRF/releases",
                        ReleaseNotesFetcher.BrowseUrl(GitHubReleasesFeed.DefaultApiUrl));

    /// <summary>A feed a manifest re-pointed elsewhere has no derivable web page; offer it as-is.</summary>
    [Fact]
    public void BrowseUrl_LeavesANonGitHubFeedAlone()
        => Assert.Equal("https://example.test/feed.json",
                        ReleaseNotesFetcher.BrowseUrl("https://example.test/feed.json"));

    /// <summary>The feed carries the body through; nothing else in the updater has to know it exists.</summary>
    [Fact]
    public void TheFeedParsesTheBody()
    {
        IReadOnlyList<ReleaseInfo> releases = GitHubReleasesFeed.ParseReleases(
            """[{"tag_name":"v1.0.0","body":"## Fixed\n- a thing","assets":[]}]""");

        Assert.Equal("## Fixed\n- a thing", Assert.Single(releases).Body);
    }

    /// <summary>...and a release with no body at all is empty rather than null.</summary>
    [Fact]
    public void AMissingBodyIsEmpty()
        => Assert.Equal("", Assert.Single(
               GitHubReleasesFeed.ParseReleases("""[{"tag_name":"v1.0.0","assets":[]}]""")).Body);
}

/// <summary>The four constructs the parser supports, and the ways real release bodies break a naive one.</summary>
public class ReleaseNotesMarkdownTests
{
    private static string Flat(ReleaseNoteLine line)
        => string.Concat(line.Runs.Select(r => r.Text));

    private static IReadOnlyList<ReleaseNoteLine> Parse(string md) => ReleaseNotesMarkdown.Parse(md);

    [Fact]
    public void NullAndEmpty_ParseToNothing()
    {
        Assert.Empty(Parse(""));
        Assert.Empty(ReleaseNotesMarkdown.Parse(null));
    }

    [Fact]
    public void BoldAndItalic_LoseTheirDelimiters_AndKeepTheirWeight()
    {
        IReadOnlyList<ReleaseNoteRun> runs = ReleaseNotesMarkdown.ParseInline("a **b** c *d* e");

        Assert.Equal("a b c d e", string.Concat(runs.Select(r => r.Text)));
        Assert.Contains(runs, r => r.Text == "b" && r.Bold && !r.Italic);
        Assert.Contains(runs, r => r.Text == "d" && r.Italic && !r.Bold);
    }

    /// <summary>Triple delimiters are both at once, and the CLOSING run must close rather than print.</summary>
    [Fact]
    public void TripleDelimiters_AreBoldAndItalic()
    {
        IReadOnlyList<ReleaseNoteRun> runs = ReleaseNotesMarkdown.ParseInline("***both*** after");

        Assert.Equal("both after", string.Concat(runs.Select(r => r.Text)));
        Assert.Contains(runs, r => r.Text == "both" && r is { Bold: true, Italic: true });
        Assert.Contains(runs, r => r.Text.Contains("after") && r is { Bold: false, Italic: false });
    }

    /// <summary>
    /// The underscore exemption. <c>snake_case_names</c> are constant in release notes and every one
    /// of them would otherwise open an italic span that runs to the end of the line.
    /// </summary>
    [Fact]
    public void UnderscoresInsideAWord_AreText()
    {
        IReadOnlyList<ReleaseNoteRun> runs = ReleaseNotesMarkdown.ParseInline("set last_check_utc now");

        Assert.Equal("set last_check_utc now", string.Concat(runs.Select(r => r.Text)));
        Assert.All(runs, r => Assert.False(r.Italic));
    }

    /// <summary>...but a bounded pair still italicises.</summary>
    [Fact]
    public void UnderscoresAroundAWord_AreItalic()
        => Assert.Contains(ReleaseNotesMarkdown.ParseInline("an _emphasis_ here"),
                           r => r.Text == "emphasis" && r.Italic);

    /// <summary>
    /// An unmatched delimiter prints. Obeying it would italicise the rest of the line, which is a far
    /// more visible failure than showing the asterisk the author typed.
    /// </summary>
    [Fact]
    public void AnUnmatchedDelimiter_IsPrinted()
    {
        IReadOnlyList<ReleaseNoteRun> runs = ReleaseNotesMarkdown.ParseInline("2 * 3 = 6");

        Assert.Equal("2 * 3 = 6", string.Concat(runs.Select(r => r.Text)));
        Assert.All(runs, r => Assert.False(r.Italic));
    }

    [Fact]
    public void AnEscapedDelimiter_IsPrinted()
        => Assert.Equal("a *b* c", string.Concat(
               ReleaseNotesMarkdown.ParseInline(@"a \*b\* c").Select(r => r.Text)));

    [Fact]
    public void InlineCode_LosesItsBackticks()
        => Assert.Equal("edit the .clay file", string.Concat(
               ReleaseNotesMarkdown.ParseInline("edit the `.clay` file").Select(r => r.Text)));

    /// <summary>A link keeps its text and drops its target: nothing here can follow a URL.</summary>
    [Fact]
    public void ALink_KeepsItsLabel()
        => Assert.Equal("see the docs today", string.Concat(
               ReleaseNotesMarkdown.ParseInline("see [the docs](https://example.test) today")
                                   .Select(r => r.Text)));

    /// <summary>An image reduces to its alt text — with the leading '!' consumed, not left behind.</summary>
    [Fact]
    public void AnImage_ReducesToItsAltText()
        => Assert.Equal("a screenshot here", string.Concat(
               ReleaseNotesMarkdown.ParseInline("a ![screenshot](x.png) here").Select(r => r.Text)));

    [Fact]
    public void BulletsAreMarked_AndNested_ByIndentation()
    {
        IReadOnlyList<ReleaseNoteLine> lines = Parse("- top\n  - nested\n    - deeper");

        Assert.Equal(3, lines.Count);
        Assert.All(lines, l => Assert.Equal(ReleaseNotesMarkdown.Bullet, l.Bullet));
        Assert.Equal([0, 1, 2], lines.Select(l => l.Indent));
        Assert.Equal(["top", "nested", "deeper"], lines.Select(Flat));
    }

    /// <summary>All three bullet spellings, and an ordered item — numbering is not in this vocabulary.</summary>
    [Theory]
    [InlineData("- a")]
    [InlineData("* a")]
    [InlineData("+ a")]
    [InlineData("1. a")]
    [InlineData("2) a")]
    public void EveryListSpelling_BecomesABullet(string source)
    {
        ReleaseNoteLine line = Assert.Single(Parse(source));
        Assert.Equal(ReleaseNotesMarkdown.Bullet, line.Bullet);
        Assert.Equal("a", Flat(line));
    }

    /// <summary>A heading is a bold line — the strongest weight this vocabulary has.</summary>
    [Fact]
    public void AHeading_BecomesABoldLine()
    {
        ReleaseNoteLine line = Assert.Single(Parse("### Layout Editor"));

        Assert.Null(line.Bullet);
        Assert.Equal(0, line.Indent);
        Assert.True(Assert.Single(line.Runs).Bold);
        Assert.Equal("Layout Editor", Flat(line));
    }

    /// <summary>
    /// The LEVEL is carried, not just the boldness. A section heading has to be visibly larger than
    /// the body it introduces, and release bodies are full of bold lead-ins mid-paragraph that would
    /// otherwise be indistinguishable from one (owner, 2026-08-29).
    /// </summary>
    [Theory]
    [InlineData("# One",     1)]
    [InlineData("## Two",    2)]
    [InlineData("### Three", 3)]
    public void AHeadingCarriesItsLevel(string source, int level)
        => Assert.Equal(level, Assert.Single(Parse(source)).HeadingLevel);

    /// <summary>An ordinary line — bold lead-in and all — is not a heading and must not be enlarged.</summary>
    [Fact]
    public void ABoldLeadIn_IsNotAHeading()
    {
        ReleaseNoteLine line = Assert.Single(Parse("**Rulers.** New ruler measures the artwork."));

        Assert.Equal(0, line.HeadingLevel);
        Assert.True(line.Runs[0].Bold);
    }

    /// <summary>Seven hashes is not a heading; it is text that starts with hashes.</summary>
    [Fact]
    public void TooManyHashes_IsNotAHeading()
        => Assert.False(Assert.Single(Parse("####### not a heading")).Runs[0].Bold);

    /// <summary>
    /// Blank lines collapse to one and are trimmed at both ends. A body typed into a web form is full
    /// of double spacing, and reproducing it faithfully scrolls a short note off screen.
    /// </summary>
    [Fact]
    public void BlankLines_CollapseAndAreTrimmed()
    {
        IReadOnlyList<ReleaseNoteLine> lines = Parse("\n\n\nfirst\n\n\n\nsecond\n\n\n");

        Assert.Equal(3, lines.Count);
        Assert.Equal("first", Flat(lines[0]));
        Assert.True(lines[1].IsBlank);
        Assert.Equal("second", Flat(lines[2]));
    }

    /// <summary>A horizontal rule has no glyph here, so it becomes the gap it was drawing.</summary>
    [Fact]
    public void AThematicBreak_BecomesAGap()
    {
        IReadOnlyList<ReleaseNoteLine> lines = Parse("one\n\n---\n\ntwo");

        Assert.Equal(3, lines.Count);
        Assert.True(lines[1].IsBlank);
    }

    /// <summary>Windows and old-Mac line endings read the same as Unix ones.</summary>
    [Fact]
    public void EveryLineEnding_ReadsTheSame()
    {
        Assert.Equal(2, Parse("a\r\nb").Count);
        Assert.Equal(2, Parse("a\rb").Count);
        Assert.Equal(2, Parse("a\nb").Count);
    }

    /// <summary>A tab indents like four spaces, so one measure serves both spellings.</summary>
    [Fact]
    public void ATabIndentsLikeFourSpaces()
        => Assert.Equal(2, Assert.Single(Parse("\t- deep")).Indent);

    /// <summary>Indentation is capped rather than unbounded: a deeply nested list still fits the width.</summary>
    [Fact]
    public void IndentIsCapped()
        => Assert.Equal(ReleaseNotesMarkdown.MaxIndent,
                        Assert.Single(Parse(new string(' ', 200) + "- far")).Indent);

    /// <summary>
    /// The shape a real release body actually has: a heading, bold lead-ins, backticked file names and
    /// bullets, all in one pass. No delimiter survives into the rendered text.
    /// </summary>
    [Fact]
    public void ARealisticBody_LeavesNoDelimitersOnScreen()
    {
        const string body = """
            Fourth public beta.

            ### Layout Editor Improvements

            **Rulers.** New ruler measures the artwork and saves in the `.clay`.

            - **Alt** turns a drag into a *duplicate*
              - in both editors
            """;

        IReadOnlyList<ReleaseNoteLine> lines = Parse(body);
        string rendered = string.Join("\n", lines.Select(Flat));

        Assert.DoesNotContain("**", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("`", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("###", rendered, StringComparison.Ordinal);

        Assert.Contains(lines, l => l.Bullet is not null && l.Indent == 1);
        Assert.Contains(lines, l => l.Runs.Any(r => r is { Text: "Rulers.", Bold: true }));
        Assert.Contains(lines, l => l.Runs.Any(r => r is { Text: "duplicate", Italic: true }));
    }
}

/// <summary>
/// Structural properties of the wiring, asserted against the source — the same form, and for the same
/// reason, as <c>UpdateSettingsWiringTests</c>: this project calls no Avalonia runtime API, so the
/// window and the entry points cannot be driven.
/// </summary>
public class ReleaseNotesWiringTests
{
    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!;
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine([RepoRoot().FullName, .. parts]));

    /// <summary>
    /// The capture must precede <c>RunBeforeUi</c>, which writes state.json on every path that applies
    /// an update. After it, a fresh installation reads as an existing one and a clean system opens
    /// with release notes — the one failure the whole gate exists to prevent.
    /// </summary>
    [Fact]
    public void TheCapture_RunsBeforeTheUpdaterWritesState()
    {
        string program = UpdateInstallSiteTests.StripComments(Read("src", "Ui", "Program.cs"));

        int capture = program.IndexOf("ReleaseNotesGate.CaptureAtStartup", StringComparison.Ordinal);
        int swap    = program.IndexOf("UpdateStartup.RunBeforeUi", StringComparison.Ordinal);

        Assert.True(capture >= 0, "circuitRF's Main must capture whether this installation already existed.");
        Assert.True(swap >= 0);
        Assert.True(capture < swap, "The capture must run BEFORE RunBeforeUi, which writes state.json.");
    }

    /// <summary>
    /// circuitRF alone captures and shows. One preferences.json and one state.json serve all three
    /// applications, so a harmonicaRF or wBond launch that recorded a version as seen would silently
    /// consume the one showing circuitRF is entitled to — and neither has a workspace window to open
    /// the dialog over.
    /// </summary>
    [Fact]
    public void OnlyCircuitRfCapturesAndShows()
    {
        foreach (string file in new[] { "ProgramHarmonica.cs", "ProgramWBond.cs",
                                        "HarmonicaApp.axaml.cs", "WBondApp.axaml.cs" })
        {
            string code = UpdateInstallSiteTests.StripComments(Read("src", "Ui", file));
            Assert.DoesNotContain("ReleaseNotesGate", code, StringComparison.Ordinal);
            Assert.DoesNotContain("ReleaseNotesDialog", code, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The checkbox exists in BOTH places the owner asked for: the dialog's own bottom-left corner and
    /// Settings ▸ Security &amp; Permissions ▸ Updates. The Updates section is a shared control, so
    /// adding it there is what puts it on that tab.
    /// </summary>
    [Fact]
    public void TheSettingIsReachableFromBothTheDialogAndSettings()
    {
        Assert.Contains("Always Show New Release Notes",
                        Read("src", "Ui", "Views", "Dialogs", "ReleaseNotesDialog.axaml"));

        Assert.Contains("ReleaseNotesCheck",
                        Read("src", "Ui", "Views", "Dialogs", "UpdateSettingsView.axaml"));

        // ...and that control is what the Security & Permissions tab hosts.
        Assert.Contains("UpdateSettingsView", Read("src", "Ui", "Views", "Dialogs", "SettingsView.axaml"));
    }

    /// <summary>
    /// One accessor for the preference, exactly as <c>UpdatePolicy</c> is for automatic updates. A key
    /// read in two places is a default that is right in one of them.
    /// </summary>
    [Fact]
    public void OnlyTheGateReadsThePreferenceKey()
    {
        string[] readers = Directory
            .EnumerateFiles(Path.Combine(RepoRoot().FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("AppPreferences.cs", StringComparison.Ordinal))
            // The KEY, not the word: `.ShowReleaseNotes` is the property access, where
            // ShowReleaseNotesIfDue and OnShowReleaseNotesChanged are method names that merely
            // contain it.
            .Where(f => UpdateInstallSiteTests.StripComments(File.ReadAllText(f))
                                              .Contains(".ShowReleaseNotes", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.Equal(["ReleaseNotesGate.cs"], readers);
    }

    /// <summary>
    /// The notes are ONE selectable block. The owner's requirement is a selection dragged across many
    /// lines and copied in one go, and a selection cannot cross two controls — so a future change that
    /// splits the lines into an ItemsControl silently breaks the feature with nothing on screen to say
    /// so.
    /// </summary>
    [Fact]
    public void TheNotesAreASingleSelectableBlockInsideAScrollViewer()
    {
        string xaml = Read("src", "Ui", "Views", "Dialogs", "ReleaseNotesDialog.axaml");

        Assert.Equal(1, xaml.Split("<SelectableTextBlock").Length - 1);
        Assert.Contains("ScrollViewer", xaml);
        Assert.DoesNotContain("ItemsControl", xaml);
        Assert.DoesNotContain("ListBox", xaml);
    }
}
