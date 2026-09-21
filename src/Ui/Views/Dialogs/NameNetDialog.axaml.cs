using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>The name a user settled on, or null on cancel. Blank clears the field.</summary>
public sealed record NameNetResult(string? Net);

/// <summary>
/// "Name Net…" — brief-authored-board-2-net-identity.md R-ab2-3, shown from the layout canvas's
/// context menu on the shape under the click.
///
/// <para><b>It states its reach before it commits</b> (R-ab2-3c) and <b>offers the names already on
/// the board</b> (R-ab2-3b). Neither is decoration: the gesture names a whole connected piece, which
/// is the point of it, and a board that acquires both <c>+3V3</c> and <c>+3v3</c> has two nets that
/// read as one.</para>
///
/// <para>It writes nothing itself. <see cref="LayoutEditorViewModel.ApplyNetName"/> is the commit,
/// through the one writer both this and the Properties Inspector's Net row use — R-ab2-3a.</para>
/// </summary>
public partial class NameNetDialog : Window
{
    private readonly IReadOnlyList<string> _existing = [];

    public NameNetDialog() => InitializeComponent();

    public NameNetDialog(LayoutNetNameReach reach, IReadOnlyList<string> offered) : this()
    {
        _existing = reach.ExistingNames;

        ReachText.Text = reach.Sentence;
        NetBox.ItemsSource = offered;

        // Pre-filled with what this piece already says where it says one thing — the ordinary edit
        // is a correction, and a blank box over a named pour invites clearing it by accident.
        if (_existing.Count == 1) NetBox.SelectedItem = _existing[0];

        Opened += (_, _) => NetBox.Focus();

        // R-ab2-3d. Stated ON OPENING rather than as the user types: the question is "this already
        // says something — did you mean to replace it?", and it is the same question whatever they
        // go on to type. A warning that appears and disappears mid-keystroke is one people learn to
        // ignore.
        ExistingWarning.IsVisible = _existing.Count > 0;
        ExistingWarning.Text = _existing.Count switch
        {
            0 => "",
            1 => $"This already carries the name '{_existing[0]}', which will be replaced.",
            _ => $"This spans pieces already named {string.Join(", ", _existing.Select(n => $"'{n}'"))} " +
                 "— naming it makes them one name.",
        };
    }

    private string? Typed()
    {
        string text = (NetBox.Text ?? "").Trim();
        return text.Length == 0 ? null : text;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e) => Close(new NameNetResult(Typed()));

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Enter) { Close(new NameNetResult(Typed())); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close(null); e.Handled = true; }
    }
}
