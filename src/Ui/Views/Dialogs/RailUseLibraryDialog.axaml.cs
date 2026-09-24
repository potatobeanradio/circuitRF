using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>What <b>Use existing library…</b> does with the library that was picked.</summary>
public enum RailLibraryUse
{
    /// <summary>Nothing.</summary>
    Cancel,

    /// <summary>Name it where it is — shared with every other design that names it.</summary>
    Reference,

    /// <summary>Copy its rows into a new library in this workspace.</summary>
    Copy,

    /// <summary>Merge its rows into the library this design already has.</summary>
    Merge,
}

/// <summary>
/// railRF's <b>Use existing library…</b>, when there is a choice to make (owner, 2026-09-24): a
/// library OUTSIDE this workspace can be referenced where it is or copied in, and a design that
/// already has a library can merge the picked one's rows or name the picked one instead.
/// </summary>
/// <remarks>
/// <b>Referencing is what a shared library is for</b> — a team buying the same part numbers board
/// after board keeps one library, and a correction made there reaches every design. The price is
/// that the design depends on a file it does not hold, which the question says, together with what
/// answers it: Archive Workspace offers the library and its model files, ticked by default.
/// <para>Returns the choice via <c>ShowDialog&lt;RailLibraryUse&gt;</c>.</para>
/// </remarks>
public partial class RailUseLibraryDialog : Window
{
    private RailLibraryUse _first, _second;

    public RailUseLibraryDialog() => InitializeComponent();

    /// <param name="picked">The library picked, absolute.</param>
    /// <param name="current">The file name of the design's present library, or null where it has none.</param>
    /// <param name="pickedIsOutside">Whether <paramref name="picked"/> lies outside the workspace.</param>
    public RailUseLibraryDialog(string picked, string? current, bool pickedIsOutside) : this()
    {
        MessageText.Text = Question(picked, current, pickedIsOutside);
        (_first, _second) = Choices(current);
        FirstButton.Content  = Label(_first, current);
        SecondButton.Content = Label(_second, current);
        Opened += (_, _) => FirstButton.Focus();
    }

    /// <summary>The two answers, default first. <b>Referencing is ALWAYS the default</b> (owner,
    /// 2026-09-24): the expected use is a team keeping one large <c>.crlib</c> for every project, and
    /// referencing is the one answer that leaves no second copy to fall out of step with it.</summary>
    public static (RailLibraryUse First, RailLibraryUse Second) Choices(string? current) =>
        current is null
            ? (RailLibraryUse.Reference, RailLibraryUse.Copy)
            : (RailLibraryUse.Reference, RailLibraryUse.Merge);

    /// <summary>A button's caption.</summary>
    public static string Label(RailLibraryUse use, string? current) => use switch
    {
        RailLibraryUse.Reference => current is null ? "Use It Where It Is" : "Use It Instead",
        RailLibraryUse.Copy      => "Copy Into Workspace",
        RailLibraryUse.Merge     => $"Merge Into {current}",
        _                        => "Cancel",
    };

    /// <summary>The prompt, built here so it is assertable with no window.</summary>
    public static string Question(string picked, string? current, bool pickedIsOutside)
    {
        string name = System.IO.Path.GetFileName(picked);
        string shared = pickedIsOutside
            ? $"'{name}' is outside this workspace. Named where it is, it is SHARED: a part added or " +
              "corrected there reaches every design that uses it. Archive Workspace offers to include " +
              "it, with its model files, ticked by default."
            : "";

        if (current is null)
            return shared + "\n\n" +
                   "Use It Where It Is — this design names that library.\n\n" +
                   "Copy Into Workspace — this design gets a library of its own, seeded with its part " +
                   $"numbers and filled from '{name}', which nothing else will change.";

        return $"This design already uses '{current}'.\n\n" +
               $"Use It Instead — the design names '{name}' in place of '{current}', which is left on disk " +
               "as it is.\n\n" +
               $"Merge Into {current} — '{name}''s new part numbers are added and blank fields filled; " +
               $"where the two disagree, '{current}' keeps its value. One undoable edit, which you then save." +
               (shared.Length > 0 ? "\n\n" + shared : "");
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(RailLibraryUse.Cancel);
    private void OnFirstClick(object? sender, RoutedEventArgs e)  => Close(_first);
    private void OnSecondClick(object? sender, RoutedEventArgs e) => Close(_second);
}
