// ================================================================
//  SnpEntryViewModel.cs  —  one SNP file entry in the library panel
// ================================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RfCore;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

public partial class SnpEntryViewModel : ViewModelBase
{
    public SNP Snp { get; }

    /// <summary>
    /// Display name: just the file name when unique across the library;
    /// a minimum-unique path suffix when two files share the same name.
    /// Updated by SnpLibraryViewModel.UpdateDisplayNames().
    /// </summary>
    [ObservableProperty]
    private string _displayName = "";

    /// <summary>True when the underlying file is missing (SNP has no data).</summary>
    public bool IsBroken => Snp.IsEmpty;

    public IAsyncRelayCommand RefreshCommand         { get; }
    public IRelayCommand      RemoveCommand          { get; }
    public IRelayCommand      RevealInExplorerCommand { get; }
    public IAsyncRelayCommand CopyPathCommand        { get; }
    public IAsyncRelayCommand CopyPathRelativeCommand { get; }

    public SnpEntryViewModel(SNP snp, SnpLibraryViewModel library)
    {
        Snp          = snp;
        _displayName = snp.FileName;

        RefreshCommand = new AsyncRelayCommand(() => library.ReloadAsync(this));
        RemoveCommand  = new RelayCommand(() => library.Remove(this));

        RevealInExplorerCommand = new RelayCommand(() =>
        {
            if (Snp.FilePath is not string path || Snp.IsEmpty) return;
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    Process.Start(new ProcessStartInfo("open", $"-R \"{path}\"")
                        { UseShellExecute = false });
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                        { UseShellExecute = true });
                else
                    Process.Start(new ProcessStartInfo("xdg-open",
                        Path.GetDirectoryName(path) ?? "")
                        { UseShellExecute = false });
            }
            catch { }
        });

        CopyPathCommand = new AsyncRelayCommand(async () =>
        {
            if (Snp.FilePath is string path && library.CopyToClipboardFunc is not null)
                await library.CopyToClipboardFunc(path);
        });

        CopyPathRelativeCommand = new AsyncRelayCommand(async () =>
        {
            if (Snp.FilePath is not string path) return;
            string displayPath = path;
            if (library.GetConfigDirectoryFunc?.Invoke() is string configDir)
            {
                string? sourceDir = Path.GetDirectoryName(path);
                if (string.Equals(configDir, sourceDir, StringComparison.OrdinalIgnoreCase))
                    displayPath = Path.GetFileName(path);
            }
            if (library.CopyToClipboardFunc is not null)
                await library.CopyToClipboardFunc(displayPath);
        });
    }

    /// <summary>Called by SnpLibraryViewModel after an in-place SNP restore.</summary>
    internal void NotifyBrokenStateChanged() => OnPropertyChanged(nameof(IsBroken));
}
