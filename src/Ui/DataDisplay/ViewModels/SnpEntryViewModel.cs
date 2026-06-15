// ================================================================
//  SnpEntryViewModel.cs  —  one data-source entry in the library panel
//
//  NAMING DEBT: This class now holds a DataSet and loads .npy files;
//  the SNP property is one (S-param) facet of a general DataSet source.
//  Rename to DataSourceEntryViewModel in 7.2c when the cube-native trace
//  path + identity components + class rename all ship together.
// ================================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

/// <summary>Distinguishes how this entry was loaded.</summary>
public enum SourceKind { Touchstone, Npy }

public partial class SnpEntryViewModel : ViewModelBase
{
    private SNP?     _snp;   // null for cube-only .npy (no S cube); broken entries use SNP.CreateBroken
    private DataSet? _data;  // null for broken entries
    private string?  _filePath;

    /// <summary>
    /// The loaded SNP.  Non-null for Touchstone and .npy-with-S sources;
    /// null for cube-only .npy (no S cube — not pickable until 7.2c).
    /// IsEmpty is true for broken entries.
    /// </summary>
    public SNP? Snp => _snp;

    /// <summary>Unified DataSet payload.  Null for broken entries.</summary>
    public DataSet? Data => _data;

    /// <summary>Single path authority — works regardless of Snp null state.</summary>
    public string? FilePath => _filePath;

    /// <summary>File name derived from FilePath.</summary>
    public string? FileName => Path.GetFileName(_filePath);

    /// <summary>Whether the source was loaded from Touchstone or .npy.</summary>
    public SourceKind Kind { get; }

    /// <summary>
    /// Display name: just the file name when unique across the library;
    /// a minimum-unique path suffix when two files share the same name.
    /// Updated by SnpLibraryViewModel.UpdateDisplayNames().
    /// </summary>
    [ObservableProperty]
    private string _displayName = "";

    /// <summary>True when the underlying file is missing (entry has no usable data).</summary>
    public bool IsBroken => _snp?.IsEmpty ?? false;

    public IAsyncRelayCommand RefreshCommand          { get; private set; } = null!;
    public IRelayCommand      RemoveCommand           { get; private set; } = null!;
    public IRelayCommand      RevealInExplorerCommand { get; private set; } = null!;
    public IAsyncRelayCommand CopyPathCommand         { get; private set; } = null!;
    public IAsyncRelayCommand CopyPathRelativeCommand { get; private set; } = null!;

    // ---- Touchstone constructor (as before) --------------------------------

    public SnpEntryViewModel(SNP snp, SnpLibraryViewModel library)
    {
        Kind       = SourceKind.Touchstone;
        _snp       = snp;
        _filePath  = snp.FilePath;
        _data      = snp.IsEmpty ? null : DataSetBuilder.FromSnp(snp);
        _displayName = FileName ?? "";

        InitCommands(library);
    }

    // ---- .npy constructor (DataSet loaded; Snp may be null for cube-only) --

    internal SnpEntryViewModel(string path, DataSet? data, SNP? snp, SnpLibraryViewModel library)
    {
        Kind       = SourceKind.Npy;
        _filePath  = path;
        _data      = data;
        _snp       = snp;
        _displayName = FileName ?? "";

        InitCommands(library);
    }

    // ---- Shared command wiring ---------------------------------------------

    private void InitCommands(SnpLibraryViewModel library)
    {
        RefreshCommand = new AsyncRelayCommand(() => library.ReloadAsync(this));
        RemoveCommand  = new RelayCommand(() => library.Remove(this));

        RevealInExplorerCommand = new RelayCommand(() =>
        {
            if (_filePath is not string path || IsBroken) return;
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
            if (_filePath is string path && library.CopyToClipboardFunc is not null)
                await library.CopyToClipboardFunc(path);
        });

        CopyPathRelativeCommand = new AsyncRelayCommand(async () =>
        {
            if (_filePath is not string path) return;
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

    // ---- In-place refresh (called by SnpLibraryViewModel) ------------------

    /// <summary>
    /// Refreshes a Touchstone entry in place after reload.
    /// The SNP instance identity is preserved so existing trace bindings survive.
    /// </summary>
    internal void RefreshTouchstone(SNP newSnp, string newPath)
    {
        _snp!.FilePath = newPath;
        _snp.RefreshFrom(newSnp);
        _filePath = newPath;
        _data = DataSetBuilder.FromSnp(_snp);
        NotifyBrokenStateChanged();
    }

    /// <summary>
    /// Refreshes a .npy entry in place after reload.
    /// If the file now contains an S cube, the SNP is refreshed in place (preserving
    /// existing trace bindings); if not, the SNP is set to null.
    /// </summary>
    internal void RefreshNpy(DataSet data, string newPath)
    {
        _filePath = newPath;
        _data     = data;

        if (data.Contains("S"))
        {
            var newSnp = DataSetBuilder.ToSnp(data);
            newSnp.FilePath = newPath;

            if (_snp is not null && !_snp.IsEmpty)
            {
                // Refresh the existing instance so trace bindings survive.
                _snp.FilePath = newPath;
                _snp.RefreshFrom(newSnp);
            }
            else
            {
                // Was broken or cube-only; safe to replace reference (no traces bound).
                _snp = newSnp;
            }
        }
        else
        {
            // No S cube — cube-only source; set Snp to null.
            _snp = null;
        }

        NotifyBrokenStateChanged();
    }

    /// <summary>Called by SnpLibraryViewModel after an in-place restore.</summary>
    internal void NotifyBrokenStateChanged() => OnPropertyChanged(nameof(IsBroken));
}
