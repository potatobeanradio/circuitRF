using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// Single-field name-input dialog.  Returns the validated name via ShowDialog,
/// or null if the user cancels.  Validates with NameValidator on OK.
/// </summary>
public partial class InputNameDialog : Window
{
    public InputNameDialog() => InitializeComponent();

    public InputNameDialog(string title, string prompt, string initialText = "") : this()
    {
        Title            = title;
        PromptLabel.Text = prompt;
        NameBox.Text     = initialText;
        // Pre-selects whatever initialText supplied (brief-cell-first-and-ui-fixes.md R-cc-3: a
        // suggested name is pre-selected so typing replaces it outright) — a no-op when initialText
        // is empty, since SelectAll on empty text selects nothing.
        Opened += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    /// <summary>
    /// The template the user chose, or null for "(Empty)" / a dialog that offered none. Read AFTER
    /// <c>ShowDialog&lt;string?&gt;</c> returns a non-null name — the dialog's return contract is
    /// still the name alone, so every existing caller is untouched.
    /// </summary>
    public ShippedSchematicTemplate? SelectedTemplate { get; private set; }

    /// <summary>
    /// Offers a template picker above the buttons. Call this ONLY where a new schematic is actually
    /// being created; a picker on New Symbol or Duplicate Cell would offer a choice that does
    /// nothing. Passing an empty list shows nothing, so a build that somehow shipped no templates
    /// degrades to the plain name prompt rather than an empty combo.
    /// </summary>
    public void OfferSchematicTemplates(IReadOnlyList<ShippedSchematicTemplate> templates)
    {
        if (templates.Count == 0) return;

        // "(Empty)" is first and pre-selected: a blank schematic is what New Cell has always
        // produced, so a template is an opt-in and never a surprise.
        var items = new List<TemplateChoice> { new("(Empty)", null) };
        foreach (var t in templates) items.Add(new TemplateChoice(t.DisplayName, t));

        TemplateBox.ItemsSource   = items;
        TemplateBox.SelectedIndex = 0;
        TemplateRow.IsVisible     = true;
    }

    // ToString is what the ComboBox renders — a record's own generated ToString would print the
    // whole shape, which is why the label is carried rather than the template alone.
    private sealed record TemplateChoice(string Label, ShippedSchematicTemplate? Template)
    {
        public override string ToString() => Label;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e) => TryCommit();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return || e.Key == Key.Enter)
        {
            TryCommit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close(null);
            e.Handled = true;
        }
    }

    private void TryCommit()
    {
        var name   = NameBox.Text?.Trim() ?? "";
        var reason = NameValidator.Validate(name);
        if (reason is not null)
        {
            ValidationMessage.Text      = reason;
            ValidationMessage.IsVisible = true;
            return;
        }
        SelectedTemplate = (TemplateBox.SelectedItem as TemplateChoice)?.Template;
        Close(name);
    }
}
