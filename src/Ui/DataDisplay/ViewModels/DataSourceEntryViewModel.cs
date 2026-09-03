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
    public DataSet? Data
    {
        get
        {
            EnsureNetworkParamCubesMaterialized();
            return _data;
        }
    }

    private volatile bool _networkParamCubesMaterialized;

    /// <summary>Serialises the lazy materialisation below. See its remarks for why a flag alone is not
    /// enough, and why this is hardening rather than a fix for anything observed.</summary>
    private readonly object _materializeGate = new();

    /// <summary>
    /// Materializes virtual "Z" and "Y" cubes into every NAMED analysis group that carries both
    /// "S" and "Z0" — a simulated S-parameter run has neither cube today
    /// (brief-dd-network-params-and-stability.md §2). Lazy and memoized (built once per load, like
    /// <see cref="NetworkView"/>'s own pattern), and re-armed on reload since a refreshed DataSet
    /// may carry different cubes.
    ///
    /// <para><b>Named groups only</b> — never the default group. A flat/Touchstone-shaped DataSet's
    /// "S" already lives in the default group AND is exposed as <see cref="Snp"/>, so it is offered
    /// through the NETWORK path (matrix-element picker items), not the cube path; materializing "Z"
    /// and "Y" there too would offer the same values a second time, as cube items.</para>
    ///
    /// <para>Once materialized, "SP1.Z"/"SP1.Y" are ordinary cubes — every consumer that already
    /// understands "SP1.S" (the spec parser, TraceExpression, the Table, export, .cdd persistence)
    /// understands them with no further change, because <c>DataSet.Contains</c>/<c>this[spec]</c>
    /// now genuinely resolve them.</para>
    /// </summary>
    private void EnsureNetworkParamCubesMaterialized()
    {
        if (_networkParamCubesMaterialized) return;

        // The flag used to be raised BEFORE the work, which makes a second caller worse off than no
        // flag at all: it is told the cubes are materialised and goes on to read a DataSet that is
        // still being written, and DataSet's group maps are ordinary Dictionaries. Driving that shape
        // directly — several threads racing this insertion against readers resolving "SP1.S" out of
        // the same DataSet — produces sporadic KeyNotFoundException for a key that is certainly
        // present (6 of 300 trials), which is the mild end of what a torn Dictionary does; the same
        // corruption also surfaces as IndexOutOfRangeException, or as a read that never returns.
        //
        // Nothing in the Data Display is known to touch Data off the UI thread, so this is not a
        // diagnosis of the field report — it is the removal of a hazard that would be indistinguishable
        // from one, on the exact object that report is about. Raising the flag only once the cubes are
        // actually there is the substance; the lock is what makes that safe to do.
        lock (_materializeGate)
        {
            if (_networkParamCubesMaterialized) return;
            MaterializeNetworkParamCubes();
            _networkParamCubesMaterialized = true;
        }
    }

    private void MaterializeNetworkParamCubes()
    {
        if (_data is not { } ds) return;

        foreach (var group in ds.Groups)
        {
            if (group == RfCore.Data.DataSet.DefaultGroup) continue;

            var cubes = ds.CubesIn(group);
            if (!cubes.TryGetValue(NetworkMetrics.SCubeName, out var sCube)) continue;
            if (!cubes.TryGetValue(NetworkMetrics.Z0CubeName, out var z0Cube)) continue;
            if (cubes.ContainsKey("Z") || cubes.ContainsKey("Y")) continue;   // don't clobber
            if (sCube.Rank < 3) continue;                                     // not [.., i, j]-shaped

            int nPorts = sCube.Axes[sCube.Rank - 1].Length;
            var z0PerPort = z0Cube.ComplexValues;
            if (z0PerPort.Length != nPorts) continue;   // not a genuine per-port Z0 for this S cube

            ds.AddToGroup(group, "Z", NetworkMetrics.ConvertSCube(sCube, z0PerPort, MatrixType.Z));
            ds.AddToGroup(group, "Y", NetworkMetrics.ConvertSCube(sCube, z0PerPort, MatrixType.Y));
        }
    }

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
        _data      = SetData(snp.IsEmpty ? null : DataSetBuilder.FromSnp(snp), "touchstone load");
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
        _data      = SetData(data, "npy load");
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
                // ArgumentList, never the single-string overload: on Unix .NET parses that string
                // into argv itself, so a Touchstone file whose NAME contains a double quote closes
                // ours and the rest becomes further arguments to `open` — which takes
                // `-a <application>` (security review, 2026-08-25).
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    Process.Start(new ProcessStartInfo("open", ["-R", path])
                        { UseShellExecute = false });
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    // Explorer wants `/select,<path>` as ONE argument.
                    Process.Start(new ProcessStartInfo("explorer.exe", [$"/select,{path}"])
                        { UseShellExecute = false });
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
    /// Every assignment of <see cref="_data"/> goes through here so that a source's cubes are
    /// INVENTORIED once, as they enter, and the trail says so.
    ///
    /// <para><b>Why at the load and not at the read.</b> <c>DataCube.Slice</c> already refuses a cube
    /// whose buffer and axes disagree, but it does that on the cube a trace happens to touch, in a
    /// crash note, after something has gone wrong. The five-round field report
    /// (<c>src/RfCore/RESOLVED.md</c>) has spent four rounds asking for the <c>.npy</c> behind it
    /// precisely because nothing records what a source FILE contained. This does, in one line, for
    /// every cube — declared shape against real buffer length — and it costs one multiply per cube on
    /// a path that has just parsed a file off disk.</para>
    ///
    /// <para>The line is written whether or not anything is wrong. "4 cubes, all consistent" is the
    /// answer that lets the next report rule the file out, which is worth as much as naming a bad one.</para>
    /// </summary>
    private static DataSet? SetData(DataSet? data, string where)
    {
        try
        {
            if (data is null) { Diagnostics.CrashReporter.Note($"source {where}: no DataSet (broken entry)"); return null; }

            int cubes = 0;
            var bad   = new System.Collections.Generic.List<string>();
            foreach (string group in data.Groups)
            {
                foreach (var kv in data.CubesIn(group))
                {
                    cubes++;
                    long expect = 1;
                    foreach (var a in kv.Value.Axes) expect *= a.Length;
                    if (kv.Value.BufferLength != expect)
                        bad.Add($"{group}.{kv.Key} axes claim {expect}, buffer holds {kv.Value.BufferLength}");
                }
            }

            Diagnostics.CrashReporter.Note(bad.Count == 0
                ? $"source {where}: {cubes} cube(s), every one shape-consistent"
                : $"source {where}: {cubes} cube(s), MALFORMED: {string.Join("; ", bad)}");
        }
        catch (Exception ex)
        {
            Diagnostics.CrashReporter.Note($"source {where}: inventory unreadable ({ex.GetType().Name})");
        }
        return data;
    }

    /// <summary>
    /// Refreshes a Touchstone entry in place after reload.
    /// The SNP instance identity is preserved so existing trace bindings survive.
    /// </summary>
    internal void RefreshTouchstone(SNP newSnp, string newPath)
    {
        _snp!.FilePath = newPath;
        _snp.RefreshFrom(newSnp);
        _filePath = newPath;
        _data = SetData(DataSetBuilder.FromSnp(_snp), "touchstone reload");
        _networkParamCubesMaterialized = false;
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
        var boundView = _networkView;                     // may be held by live derived traces
        _filePath = newPath;
        _data     = SetData(data, "npy reload");
        _networkView = null; _networkViewBuilt = false;   // rebuilt lazily against the new DataSet
        _networkParamCubesMaterialized = false;           // re-armed against the new DataSet

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

        RefreshNetworkViewPreservingIdentity(boundView);
        NotifyBrokenStateChanged();
        ClassifyZ0FromData();
    }

    /// <summary>
    /// Re-points an already-handed-out <see cref="NetworkView"/> at the reloaded DataSet
    /// <b>without replacing the instance</b> — the same guarantee <see cref="RefreshTouchstone"/>
    /// makes for <see cref="Snp"/>, and for the same reason: derived traces hold this object, and
    /// both the plot's stale-trace sweep and the picker's "already applied" test are reference
    /// comparisons. Handing out a fresh instance on every re-run is indistinguishable from the
    /// source having left the library.
    ///
    /// <para>Called only after <c>_data</c> and <c>_snp</c> are settled, since the lazy getter
    /// reads both. A rebuild that comes back null means the reloaded source is no longer
    /// network-shaped (its S cube is gone, or a sweep axis has made it rank 4) — then the view is
    /// legitimately gone and the traces bound to it are genuinely stale.</para>
    /// </summary>
    private void RefreshNetworkViewPreservingIdentity(SNP? bound)
    {
        if (bound is null) return;      // nothing was ever handed out
        if (_snp is not null) return;   // Snp IS the view here, and it was refreshed in place above

        _networkView = null; _networkViewBuilt = false;
        var rebuilt = NetworkView;      // lazy build against the new DataSet
        if (rebuilt is null) return;    // no longer network-shaped — leave the view null

        bound.RefreshFrom(rebuilt);
        _networkView      = bound;
        _networkViewBuilt = true;
    }

    /// <summary>
    /// Refreshes a loadpull entry (.spl or .lpcwave) in place after reload.
    /// Loadpull DataSets carry no S cube, so Snp stays null.
    /// </summary>
    internal void RefreshLoadpull(DataSet data, string newPath)
    {
        _filePath = newPath;
        _data     = SetData(data, "npy reload");
        _snp      = null;
        _networkParamCubesMaterialized = false;
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
