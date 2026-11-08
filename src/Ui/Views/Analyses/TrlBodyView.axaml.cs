using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Views.Analyses;

public partial class TrlBodyView : UserControl
{
    public TrlBodyView() => InitializeComponent();

    private void OnBrowseAClick(object? sender, RoutedEventArgs e) => Browse(a: true);
    private void OnBrowseBClick(object? sender, RoutedEventArgs e) => Browse(a: false);

    // A save picker: the fixture is a file the run writes.
    private async void Browse(bool a)
    {
        if (DataContext is not TrlBodyViewModel vm) return;
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } sp) return;
        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = a ? "Write fixture A to" : "Write fixture B to",
            SuggestedFileName = a ? vm.FixtureAHint : vm.FixtureBHint,
            DefaultExtension  = "s2p",
            FileTypeChoices   = [new FilePickerFileType("Touchstone two-port (*.s2p)") { Patterns = ["*.s2p"] }],
        });
        if (file?.TryGetLocalPath() is { Length: > 0 } path) vm.ApplyPickedFixture(a, path);
    }
}
