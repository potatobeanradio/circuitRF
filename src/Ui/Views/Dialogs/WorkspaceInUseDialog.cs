using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// SL4 R-sl4-2 — the advisory notice that another session already has this workspace open, and the
/// two answers that always follow it.
///
/// <para><b>Both answers are available, always.</b> circuitRF cannot lock a network share reliably
/// across three platforms and must not pretend to (R-sl-8): a lock this product treated as
/// authoritative would become a stale file that locks out a team, which is a worse failure than the
/// one being prevented and is unfixable by anyone who does not know the file exists. So this window
/// has no "you cannot open this" state — it reports what was found, says plainly that circuitRF
/// cannot verify it, and lets the user decide.</para>
///
/// <para>Read-only is offered FIRST and is the default because it is the answer that cannot lose
/// anyone's work: it is exactly the state SL2 already built for a locked-down share, so the whole
/// workspace stays browsable, its schematics stay readable and editable, and nothing is written back
/// — including the <c>.cws</c>, which is the file a concurrent open actually endangers.</para>
/// </summary>
public static class WorkspaceInUseDialog
{
    public enum Choice
    {
        /// <summary>Open, but write nothing — the safe answer, and the default.</summary>
        ReadOnly,

        /// <summary>Open normally. The user has decided the other session is not real, or does not
        /// mind; last writer wins, which the notice said.</summary>
        OpenAnyway,

        /// <summary>Do not open at all.</summary>
        Cancel,
    }

    public static async Task<Choice> AskAsync(Window owner, string workspaceName, string notice)
    {
        Choice result = Choice.Cancel;

        var dialog = new Window
        {
            Title = "Workspace in use",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };

        var readOnlyBtn = new Button
        {
            Content = "Open _read-only",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsDefault = true,
        };
        var anywayBtn = new Button
        {
            Content = "Open _anyway",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var cancelBtn = new Button
        {
            Content = "Cancel",
            HorizontalAlignment = HorizontalAlignment.Right,
            IsCancel = true,
        };

        readOnlyBtn.Click += (_, _) => { result = Choice.ReadOnly;   dialog.Close(); };
        anywayBtn.Click   += (_, _) => { result = Choice.OpenAnyway; dialog.Close(); };
        cancelBtn.Click   += (_, _) => { result = Choice.Cancel;     dialog.Close(); };

        dialog.Content = new StackPanel
        {
            Margin  = new Avalonia.Thickness(20),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = $"'{workspaceName}' may be open elsewhere",
                    FontWeight = FontWeight.SemiBold,
                    FontSize = 15,
                },
                new TextBlock
                {
                    Text = notice,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.8,
                },
                new TextBlock
                {
                    Text = "Open read-only to browse and read it without writing anything back. " +
                           "Open anyway if you know the other session has ended.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.8,
                },
                readOnlyBtn,
                anywayBtn,
                cancelBtn,
            },
        };

        await dialog.ShowDialog(owner);
        return result;
    }
}
