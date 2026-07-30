// ================================================================
//  DataSourceEntryViewModel.cs  —  one data-source entry in the library panel
// ================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

/// <summary>Distinguishes how this entry was loaded.</summary>
public enum SourceKind { Touchstone, Npy, Spl, Lpcwave }

public partial class DataSourceEntryViewModel : ViewModelBase
{
    private SNP?     _snp;   // null for cube-only .npy (no S cube); broken entries use SNP.CreateBroken
    private DataSet? _data;  // null for broken entries
    private string?  _filePath;

    /// <summary>
    /// The loaded SNP.  Non-null for Touchstone and .npy-with-S sources;
    /// null for cube-only .npy (no S cube).
    /// IsEmpty is true for broken entries.
    /// </summary>
    public SNP? Snp => _snp;

    /// <summary>Unified DataSet payload.  Null for broken entries.</summary>
    public DataSet? Data => _data;

    private SNP? _networkView;
    private bool _networkViewBuilt;

    /// <summary>
    /// An SNP view of this source for NETWORK-METRIC purposes (stability, passivity, MaxGain) —
    /// <see cref="Snp"/> when there is one, otherwise built on demand from a grouped run's own
    /// <c>"SP1.S"</c> cube.
    ///
    /// <para><b>Why this is separate from <see cref="Snp"/>, and must stay separate.</b> A simulated
    /// run's S cube deliberately does NOT become <see cref="Snp"/> (brief-sparam-run-add-trace): it
    /// is offered through the CUBE path, which can carry axes an SNP structurally cannot — a swept
    /// S cube is rank 4. Making it an SNP would both break swept sources and offer S(1,1) twice,
    /// once per path. But the 2-port metric formulae need matrices, and only matrices — so they get
    /// this narrow view, gated on <see cref="RfCore.Data.NetworkMetrics.IsNetworkShaped"/> so a
    /// swept cube is correctly refused rather than silently flattened to one arbitrary slice.</para>
    /// </summary>
    public SNP? NetworkView
    {
        get
        {
            if (_snp is not null) return _snp;
            if (_networkViewBuilt) return _networkView;
            _networkViewBuilt = true;
            if (_data is { } ds && NetworkMetrics.IsNetworkShaped(ds)
                                && NetworkMetrics.FindSCubeSpec(ds) is { } spec)
            {
                try { _networkView = DataSetBuilder.ToSnp(ds, spec); }
                catch { _networkView = null; }
            }
            return _networkView;
        }
    }

    /// <summary>Single path authority — works regardless of Snp null state.</summary>
    public string? FilePath => _filePath;

    /// <summary>File name derived from FilePath.</summary>
    public string? FileName => Path.GetFileName(_filePath);

    /// <summary>Whether the source was loaded from Touchstone or .npy.</summary>
    public SourceKind Kind { get; }

    /// <summary>
    /// Display name: just the file name when unique across the library;
    /// a minimum-unique path suffix when two files share the same name.
    /// Updated by DataSourceLibraryViewModel.UpdateDisplayNames().
    /// </summary>
    [ObservableProperty]
    private string _displayName = "";

    /// <summary>
    /// Short display alias for this source (R-res-4) — defaults to the file stem, user-editable,
    /// unique within the library (enforced by <see cref="DataSourceLibraryViewModel.TrySetAlias"/>,
    /// the only mutator). Stored in the .cdd, not re-derived at load time: which alias a source
    /// carries is a display decision the user made ("baseline" vs "tuned") and must survive reload.
    /// </summary>
    [ObservableProperty]
    private string _alias = "";

    /// <summary>True when the underlying file is missing (entry has no usable data).</summary>
    public bool IsBroken => _snp?.IsEmpty ?? false;

    // ---- Z0 classification (Phase 7.2e) ------------------------------------

    /// <summary>Reference-impedance kind of this source's S data, or null when there is no Z0 cube
    /// (no S data / cube-only non-S source). Computed on load and refresh.</summary>
    public Z0Kind? Z0Kind { get; private set; }

    /// <summary>True when S results from this source are referenced to a non-uniform or complex Z0
    /// (the value the user must be reminded about). False for plain uniform-real 50 Ω-style sources.</summary>
    public bool HasUnusualZ0 => Z0Kind is RfCore.Data.Z0Kind.NonUniform or RfCore.Data.Z0Kind.UniformComplex;

    /// <summary>Per-port reference impedances (index k = port k+1); empty when no Z0 cube.</summary>
    public IReadOnlyList<Complex> Z0PerPort { get; private set; } = Array.Empty<Complex>();

    private void ClassifyZ0FromData()
    {
        // Group-aware for the same reason the S lookup is: a simulated run's Z0 lives at "SP1.Z0",
        // and a bare lookup left Z0PerPort EMPTY for every simulation — silently discarding the
        // per-port/complex reference the stability and passivity maths depend on.
        if (_data is not null && DataSetBuilder.FindCubeSpec(_data, "Z0") is { } z0Spec)
        {
            Z0Kind    = DataSetBuilder.ClassifyZ0(_data[z0Spec]);
            Z0PerPort = _data[z0Spec].ComplexValues;
        }
        else
        {
            Z0Kind    = null;
            Z0PerPort = Array.Empty<Complex>();
        }
    }

    // ---- Commands ----------------------------------------------------------

    public IAsyncRelayCommand RefreshCommand          { get; private set; } = null!;
    public IRelayCommand      RemoveCommand           { get; private set; } = null!;
    public IRelayCommand      RevealInExplorerCommand { get; private set; } = null!;
    public IAsyncRelayCommand CopyPathCommand         { get; private set; } = null!;
    public IAsyncRelayCommand CopyPathRelativeCommand { get; private set; } = null!;

    // ---- Touchstone constructor --------------------------------------------

    public DataSourceEntryViewModel(SNP snp, DataSourceLibraryViewModel library)
    {
        Kind       = SourceKind.Touchstone;
        _snp       = snp;
        _filePath  = snp.FilePath;
        _data      = snp.IsEmpty ? null : DataSetBuilder.FromSnp(snp);
        _displayName = FileName ?? "";
        _alias       = DefaultAlias(FileName);

        InitCommands(library);
        ClassifyZ0FromData();
    }

    // ---- .npy / loadpull constructor (DataSet loaded; Snp may be null for cube-only) --

    internal DataSourceEntryViewModel(string path, DataSet? data, SNP? snp, DataSourceLibraryViewModel library,
                                      SourceKind kind = SourceKind.Npy)
    {
        Kind       = kind;
        _filePath  = path;
        _data      = data;
        _snp       = snp;
        _displayName = FileName ?? "";
        _alias       = DefaultAlias(FileName);

        InitCommands(library);
        ClassifyZ0FromData();
    }

    // ---- Shared command wiring ---------------------------------------------

    private void InitCommands(DataSourceLibraryViewModel library)
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

    // ---- In-place refresh (called by DataSourceLibraryViewModel) -----------

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
        ClassifyZ0FromData();
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
        _networkView = null; _networkViewBuilt = false;   // rebuilt lazily against the new DataSet

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
        ClassifyZ0FromData();
    }

    /// <summary>
    /// Refreshes a loadpull entry (.spl or .lpcwave) in place after reload.
    /// Loadpull DataSets carry no S cube, so Snp stays null.
    /// </summary>
    internal void RefreshLoadpull(DataSet data, string newPath)
    {
        _filePath = newPath;
        _data     = data;
        _snp      = null;
        NotifyBrokenStateChanged();
        ClassifyZ0FromData();
    }

    /// <summary>Called by DataSourceLibraryViewModel after an in-place restore.</summary>
    internal void NotifyBrokenStateChanged() => OnPropertyChanged(nameof(IsBroken));

    /// <summary>The alias a source gets before the user (or a loaded .cdd) ever renames it: its own
    /// file stem, falling back to "source" for a nameless/broken placeholder.</summary>
    internal static string DefaultAlias(string? fileName) =>
        string.IsNullOrEmpty(fileName) ? "source" : Path.GetFileNameWithoutExtension(fileName);
}
