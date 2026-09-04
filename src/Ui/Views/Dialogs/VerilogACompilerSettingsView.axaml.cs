using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CircuitRF.Core.Devices.External;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// Names the Verilog-A compiler circuitRF runs to build a <c>.va</c> — implemented once and hosted by
/// both settings dialogs, like the two controls above it on the same tab.
///
/// <para><b>Blank is the default and means "look on PATH".</b> It is never seeded with a discovered
/// path: a seeded value would freeze today's PATH answer into <c>preferences.json</c> and stop
/// tracking a compiler the user later installs or upgrades — the same "absence IS the default" rule
/// every nullable preference beside it follows.</para>
/// </summary>
public partial class VerilogACompilerSettingsView : UserControl
{
    /// <summary>The populate guard — see <see cref="ExternalWorkerSettingsView"/>, same reason:
    /// opening and closing Settings without touching anything must write nothing.</summary>
    private bool _loading;

    public VerilogACompilerSettingsView()
    {
        InitializeComponent();
        Load();
    }

    /// <summary>Hides the section header, for a host that supplies its own (a tab title).</summary>
    public bool ShowSectionHeader
    {
        get => SectionHeader.IsVisible;
        set => SectionHeader.IsVisible = value;
    }

    public void Load()
    {
        _loading = true;
        try { CompilerBox.Text = AppPreferencesIo.Load().VerilogACompiler ?? ""; }
        finally { _loading = false; }
    }

    /// <summary>
    /// Writes the named compiler. Blank clears it back to "look on PATH", stored as null rather than
    /// as an empty string so "never chosen" and "deliberately cleared" stay the same state — which is
    /// what the default rests on.
    /// </summary>
    private void OnCompilerCommitted(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;

        string typed = CompilerBox.Text?.Trim() ?? "";
        AppPreferencesIo.Update(p => p.VerilogACompiler = typed.Length == 0 ? null : typed);

        // A settings change that only mutates JSON is incomplete — the rule the consent checkbox
        // beside this one already follows. A model resolved through the OLD compiler is still loaded,
        // and the whole point of naming a different one is to get a different artefact; dropping the
        // resolved providers means the next Run compiles through the compiler just named, rather than
        // the change appearing to take effect only after a restart.
        try { ExternalDeviceRegistry.EndResolvedProviders(); } catch { /* nothing here may fail Settings */ }

        ShowStatus("");
    }

    private async void OnBrowseForCompiler(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        try
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title         = "Choose Verilog-A Compiler",
                AllowMultiple = false,
            });

            if (files.Count != 1 || files[0].TryGetLocalPath() is not { Length: > 0 } path) return;

            CompilerBox.Text = path;
            OnCompilerCommitted(sender, e);
        }
        catch (Exception) { /* a cancelled or unavailable picker is not an error */ }
    }

    /// <summary>
    /// Runs whatever is currently resolved and reports what it says it is.
    ///
    /// <para>It goes through the SAME discovery the compile step uses rather than probing the text box
    /// directly, so a blank box genuinely answers "here is what PATH would give you" — the question a
    /// user with nothing configured actually has, and one a direct probe could not answer at all.</para>
    /// </summary>
    private void OnTestCompiler(object? sender, RoutedEventArgs e)
    {
        try
        {
            var found = VerilogACompilerDiscovery.Find(out var rejected);
            ShowStatus(found is null
                ? VerilogACompilerDiscovery.DescribeFailure(rejected)
                : $"Found, {found.HowFound}: {found.Identity}");
        }
        catch (Exception ex) { ShowStatus(ex.Message); }
    }

    private void ShowStatus(string text)
    {
        CompilerStatusText.Text      = text;
        CompilerStatusText.IsVisible = text.Length > 0;
    }
}
