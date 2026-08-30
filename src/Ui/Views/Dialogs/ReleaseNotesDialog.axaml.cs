using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Media;
using CircuitRF.Ui.Updates;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// The <b>Release Notes</b> window — what changed in the version that has just been installed.
///
/// <para>It renders a <see cref="ReleaseNotesResult"/> and does nothing else: it does not decide
/// whether to open (<see cref="ReleaseNotesGate"/>), does not fetch (<see cref="ReleaseNotesFetcher"/>)
/// and does not parse (<see cref="ReleaseNotesMarkdown"/>). All three of those are testable without
/// Avalonia, which is the point of the split — <c>Ui.Tests</c> calls no Avalonia runtime API, so
/// anything decided in this file is decided where no test can reach it.</para>
/// </summary>
public partial class ReleaseNotesDialog : Window
{
    /// <summary>Indentation for one nesting level, in non-breaking spaces.</summary>
    private const int SpacesPerIndent = 4;

    /// <summary>
    /// The one character indentation is built from. An ordinary space is collapsible, and a run of
    /// them at the start of a wrapped line is exactly what a text layout is entitled to drop.
    /// </summary>
    private const char Nbsp = ' ';

    /// <summary>
    /// The body size, matching the block's own <c>FontSize</c> in the XAML — every run states a size,
    /// because a heading has to set one and a run that inherits beside runs that do not is a size that
    /// depends on which branch last touched the markup.
    /// </summary>
    private const double BodyFontSize = 12;

    /// <summary>
    /// Section headings, by Markdown level: visibly larger than the body, visibly smaller than the
    /// window's own <c>circuitRF &lt;version&gt;</c> title at 18. Two sizes rather than six — past
    /// <c>###</c> a release body is not really nesting sections, and a ladder of near-identical sizes
    /// reads as inconsistency rather than as structure.
    /// </summary>
    private static double HeadingFontSize(int level) => level <= 2 ? 15 : 14;

    private string _browseUrl = "";

    /// <summary>
    /// The populate guard, for the same reason <c>UpdateSettingsView</c> has one: setting
    /// <c>IsChecked</c> raises <c>IsCheckedChanged</c>, so without it opening the dialog would write
    /// the preference it just read.
    /// </summary>
    private bool _loading;

    public ReleaseNotesDialog() : this(new ReleaseNotesResult(
        ReleaseNotesOutcome.Unavailable, AppVersion.Display, "",
        ReleaseNotesFetcher.BrowseUrl(GitHubReleasesFeed.DefaultApiUrl)))
    {
        // Parameterless for the XAML previewer only; nothing in the application uses it.
    }

    public ReleaseNotesDialog(ReleaseNotesResult result)
    {
        InitializeComponent();
        Render(result);
    }

    private void Render(ReleaseNotesResult result)
    {
        _browseUrl = result.BrowseUrl;

        HeadingText.Text = $"{UpdateApp.Name} {result.Version}";

        _loading = true;
        try { AlwaysShowCheck.IsChecked = ReleaseNotesGate.ShowPreference; }
        finally { _loading = false; }

        NotesText.Inlines?.Clear();

        switch (result.Outcome)
        {
            case ReleaseNotesOutcome.Found:
                AppendLines(ReleaseNotesMarkdown.Parse(result.Markdown));
                break;

            case ReleaseNotesOutcome.NotPublished:
                BrowseButton.IsVisible = true;
                AppendFailure(
                    $"No release notes have been published for {UpdateApp.Name} {result.Version}.",
                    result.BrowseUrl);
                break;

            default:
                BrowseButton.IsVisible = true;
                AppendFailure(
                    $"The release notes for {UpdateApp.Name} {result.Version} could not be downloaded. "
                    + "The repository may be unreachable from this network.",
                    result.BrowseUrl);
                break;
        }
    }

    /// <summary>
    /// Turns the parsed lines into inlines of one block. Every line ends in a <see cref="LineBreak"/>
    /// rather than a newline character, because a <c>Run</c>'s own text is laid out as a single
    /// paragraph and a <c>\n</c> inside one is not a break.
    /// </summary>
    private void AppendLines(IReadOnlyList<ReleaseNoteLine> lines)
    {
        InlineCollection? inlines = NotesText.Inlines;
        if (inlines is null) return;

        for (int i = 0; i < lines.Count; i++)
        {
            ReleaseNoteLine line = lines[i];

            if (!line.IsBlank)
            {
                double size = line.HeadingLevel > 0 ? HeadingFontSize(line.HeadingLevel) : BodyFontSize;

                string lead = new(Nbsp, line.Indent * SpacesPerIndent);
                if (line.Bullet is not null) lead += line.Bullet + new string(Nbsp, 2);
                if (lead.Length > 0) inlines.Add(new Run(lead) { FontSize = size });

                foreach (ReleaseNoteRun run in line.Runs)
                    inlines.Add(new Run(run.Text)
                    {
                        FontSize   = size,
                        FontWeight = run.Bold   ? FontWeight.Bold   : FontWeight.Normal,
                        FontStyle  = run.Italic ? FontStyle.Italic  : FontStyle.Normal,
                    });
            }

            // No trailing break after the last line: it would add an empty row the scroll bar counts.
            if (i < lines.Count - 1) inlines.Add(new LineBreak());
        }
    }

    /// <summary>
    /// The two failure forms. The URL is written into the selectable block as well as being behind the
    /// button, because a user on a machine with no browser association still has to be able to read
    /// and copy it — which is the whole reason this text is selectable.
    /// </summary>
    private void AppendFailure(string message, string url)
    {
        InlineCollection? inlines = NotesText.Inlines;
        if (inlines is null) return;

        inlines.Add(new Run(message));
        inlines.Add(new LineBreak());
        inlines.Add(new LineBreak());
        inlines.Add(new Run("They can be read at:"));
        inlines.Add(new LineBreak());
        inlines.Add(new Run(url) { FontWeight = FontWeight.Bold });
    }

    private void OnAlwaysShowChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ReleaseNotesGate.SetShowPreference(AlwaysShowCheck.IsChecked == true);
    }

    /// <summary>
    /// Opens the releases page in the default browser. Mirrors <c>DocLauncher</c>'s cross-platform
    /// open, and swallows everything for the same reason it does: a dialog offering a link must not be
    /// able to take the application down with it.
    /// </summary>
    private void OnBrowseClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start(new ProcessStartInfo("open", _browseUrl) { UseShellExecute = false });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo(_browseUrl) { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("xdg-open", _browseUrl) { UseShellExecute = false });
        }
        catch (Exception) { /* no browser, no association — the URL is on screen either way */ }
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
