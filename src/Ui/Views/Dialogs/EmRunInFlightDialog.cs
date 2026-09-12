using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// The warning shown when a gesture that would REPLACE this window's workspace, close the window or
/// quit the application is made while an EM analysis started from this window is still running.
///
/// <para><b>A warning, not a refusal</b> (owner, 2026-09-11). An EM sweep is minutes to hours of work
/// that writes nothing until it finishes, so a workspace switch mid-run is a real loss — but it is
/// the user's loss to accept, and there are legitimate reasons to accept it. What the window owes
/// them is the fact that a run is in flight and what this particular gesture does to it, which is
/// different in the two cases: a workspace switch leaves the run going with no surface to report to
/// or be stopped from, while quitting ENDS it and writes nothing at all.</para>
///
/// <para>The alternative is offered as a BUTTON where one exists rather than only as a sentence: the
/// answer to "I want another workspace" during a long run is a second window, and making the user
/// find File ▸ New Window themselves after being warned is the part that would read as an
/// obstruction. Cancel is the default whenever there is no such button, because a gesture made by
/// accident — Ctrl+N, Cmd+Q — is exactly the case this window exists for.</para>
/// </summary>
public static class EmRunInFlightDialog
{
    public enum Choice
    {
        /// <summary>Do not do it. The run and the workspace are untouched.</summary>
        Cancel,

        /// <summary>Do it anyway, having been told what it costs.</summary>
        Continue,

        /// <summary>The user took the offered alternative (a second window) instead.</summary>
        Alternative,
    }

    /// <param name="work">
    /// What is still running, as a sentence fragment that reads before "is still running" — "the EM
    /// run 'LowpassFilter'". Built by <c>WorkspaceViewModel.DescribeEmWorkInFlight</c>.
    /// </param>
    /// <param name="consequence">What this gesture would do to that run, in the gesture's own words.</param>
    /// <param name="continueLabel">The button that does it anyway — "Create it anyway", "Quit anyway".</param>
    /// <param name="alternativeSentence">What to do instead, or null when there is nothing to offer
    /// beyond waiting.</param>
    /// <param name="alternativeLabel">The button that does it. Ignored when the sentence is null.</param>
    public static async Task<Choice> ShowAsync(
        Window  owner,
        string  work,
        string  consequence,
        string  continueLabel,
        string? alternativeSentence = null,
        string? alternativeLabel    = null)
    {
        var result = Choice.Cancel;

        var dialog = new Window
        {
            Title                 = "An EM analysis is still running",
            Width                 = 540,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize             = false,
        };

        var body = new StackPanel
        {
            Margin  = new Avalonia.Thickness(20),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text         = $"{Capitalize(work)} is still running",
                    FontWeight   = FontWeight.SemiBold,
                    FontSize     = 15,
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text         = consequence,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity      = 0.8,
                },
            },
        };

        if (alternativeSentence is not null)
            body.Children.Add(new TextBlock
            {
                Text         = alternativeSentence,
                TextWrapping = TextWrapping.Wrap,
                Opacity      = 0.8,
            });

        // Always said, and said last: waiting needs no explanation, and Stop vs Cancel is the
        // distinction a user mid-sweep actually needs — one keeps the points already solved, the
        // other writes nothing. Both live on the EM panel this window has, for now, kept open.
        body.Children.Add(new TextBlock
        {
            Text         = "Otherwise, let it finish — or use Stop on the EM panel to end it now and "
                         + "keep the points already solved, or Cancel there to end it and write nothing.",
            TextWrapping = TextWrapping.Wrap,
            Opacity      = 0.8,
        });

        bool hasAlternative = alternativeSentence is not null && alternativeLabel is not null;

        if (hasAlternative)
        {
            var altBtn = new Button
            {
                Content             = alternativeLabel,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsDefault           = true,
            };
            altBtn.Click += (_, _) => { result = Choice.Alternative; dialog.Close(); };
            body.Children.Add(altBtn);
        }

        var continueBtn = new Button
        {
            Content             = continueLabel,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        continueBtn.Click += (_, _) => { result = Choice.Continue; dialog.Close(); };
        body.Children.Add(continueBtn);

        var cancelBtn = new Button
        {
            Content             = "Cancel",
            HorizontalAlignment = HorizontalAlignment.Right,
            IsCancel            = true,
            IsDefault           = !hasAlternative,
        };
        cancelBtn.Click += (_, _) => { result = Choice.Cancel; dialog.Close(); };
        body.Children.Add(cancelBtn);

        dialog.Content = body;

        await dialog.ShowDialog(owner);
        return result;
    }

    private static string Capitalize(string s)
        => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
