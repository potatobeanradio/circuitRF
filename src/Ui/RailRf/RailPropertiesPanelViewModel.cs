// The Properties panel a selected .crail shows, and its Open railRF… button
// (brief-railrf-7-window.md R-rail7-10; railrf.md §11.4).

using System;
using System.Linq;
using CircuitRF.Design.RailRf;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.RailRf;

/// <summary>
/// A compact summary of a selected railRF document, and the button that opens it.
/// </summary>
/// <remarks>
/// <b>It edits nothing</b>, which is the Match and wBond panels' own rule: a rail set is an artwork
/// reference, a stackup, a rail list, four sub-lists and a target, none of which fits in a 420 px
/// column of text rows — and offering a half-editor beside a full one is how the two come to
/// disagree.
///
/// <para><b>Two traps already paid for in those panels apply here and are worth naming.</b> First,
/// the project-tree dirty mark is PUSHED onto a node that a window-activate rescan then rebuilds, so
/// it is set through the same path those panels use and never through a second one — this class
/// deliberately has no dirty mechanism of its own, and the window owns the document. Second, a combo
/// whose <c>ItemsSource</c> binding attaches after a selection was set drops that selection silently
/// (the wBond round-6 blank-Group-combo bug); this panel has no combo, and the window's own rail
/// selector is built ItemsSource-first for that reason.</para>
/// </remarks>
public sealed partial class RailPropertiesPanelViewModel : ObservableObject
{
    /// <summary>The <c>.crail</c> this panel is about, or null.</summary>
    [ObservableProperty]
    private string? _path;

    /// <summary>The document, where it could be read.</summary>
    [ObservableProperty]
    private RailDocument? _document;

    /// <summary>Why it could not be read, or empty. Said rather than shown as a blank panel — a file
    /// that will not open reads as circuitRF being broken unless the reason is on screen.</summary>
    [ObservableProperty]
    private string _readError = "";

    /// <summary>
    /// Where a live result comes from, when a railRF window for this document is open.
    /// </summary>
    /// <remarks>
    /// <b>The panel does not run anything.</b> Worst drop and worst margin are numbers a solve
    /// produced, so where nothing has been solved the panel says so rather than showing a blank that
    /// reads as zero.
    /// </remarks>
    public Func<string, RailResultView?>? ResultLookup { get; set; }

    /// <summary>Raised by <see cref="OpenRailRf"/>. The view opens the window: only it knows which
    /// <c>TopLevel</c> owns this panel, exactly as the Match panel does.</summary>
    public event Action<string>? OpenRailRfRequested;

    /// <summary>Points the panel at a document, or clears it.</summary>
    public void SetContext(string? crailPath)
    {
        Path = crailPath;
        Document = null;
        ReadError = "";

        if (crailPath is not { Length: > 0 }) { RefreshAll(); return; }

        try
        {
            Document = RailDocumentIo.LoadFromFile(crailPath);
        }
        catch (Exception ex)
        {
            ReadError = $"Could not read {System.IO.Path.GetFileName(crailPath)}: {ex.Message}";
        }

        RefreshAll();
    }

    /// <summary>The rail the summary is about — the first, which is the one the window opens on.</summary>
    public RailSpec? Rail => Document?.Rails.FirstOrDefault();

    /// <summary>"+1V8", or the empty case said.</summary>
    public string RailText => Rail?.Name ?? "no rails yet";

    /// <summary>The rail's reference, or the fact that it states none — which is the state that stops
    /// it running, so the panel says it rather than leaving the row blank.</summary>
    public string ReferenceText =>
        Rail?.ReferenceLayer is { } key
            ? $"layer {key.Layer}/{key.Datatype} · {ExtentText}"
            : "not set";

    private string ExtentText => Rail?.ReferenceExtent switch
    {
        RailReferenceExtent.FilledToOutline => "filled to outline",
        RailReferenceExtent.Infinite        => "infinite",
        _                                   => "as imported",
    };

    /// <summary>"2 sources · 5 loads".</summary>
    public string BranchesText =>
        Rail is { } r ? $"{r.Sources.Count} source(s) · {r.Loads.Count} load(s)" : "";

    /// <summary>The worst port drop on that rail, or the fact that nothing has been solved.</summary>
    public string WorstDropText
    {
        get
        {
            if (Result?.Result.Rail(Rail?.Name ?? "") is not { } result) return NotRun;

            var worst = result.Ports.Where(p => p.DropV is not null)
                                    .OrderByDescending(p => p.DropV)
                                    .FirstOrDefault();
            return worst?.DropV is { } d
                ? $"{d * 1e3:0.###} mV at {worst.Name}"
                : "no source stated a voltage, so there is no drop to report";
        }
    }

    /// <summary>The worst regulator headroom the chain found, or the fact that nothing has.</summary>
    public string WorstMarginText
    {
        get
        {
            if (Result is not { } view) return NotRun;

            var worst = view.Result.Regulators.Where(r => r.MarginV is not null)
                                              .OrderBy(r => r.MarginV)
                                              .FirstOrDefault();
            return worst?.MarginV is { } m
                ? $"{m * 1e3:0.###} mV at {worst.Refdes}"
                : "no regulator on this document states a minimum input voltage";
        }
    }

    /// <summary>Which model produced those two numbers. <b>On the panel</b>, because §2.9's first rule
    /// is that every result says which model produced it — a summary that dropped it would report a
    /// fast-model pass as a pass.</summary>
    public string ModelText => Result is { } v
        ? (v.Kind == CircuitRF.Design.Layout.Pdn.PdnModelKind.Fast ? "fast model" : "accuracy")
        : "";

    private const string NotRun = "not run";

    private RailResultView? Result =>
        Path is { Length: > 0 } p ? ResultLookup?.Invoke(p) : null;

    /// <summary>True once there is a document to summarise.</summary>
    public bool HasDocument => Document is not null;

    /// <summary>Opens the railRF window on this document — §11.4's own button.</summary>
    [RelayCommand]
    private void OpenRailRf()
    {
        if (Path is { Length: > 0 } p) OpenRailRfRequested?.Invoke(p);
    }

    private void RefreshAll()
    {
        OnPropertyChanged(nameof(Rail));
        OnPropertyChanged(nameof(RailText));
        OnPropertyChanged(nameof(ReferenceText));
        OnPropertyChanged(nameof(BranchesText));
        OnPropertyChanged(nameof(WorstDropText));
        OnPropertyChanged(nameof(WorstMarginText));
        OnPropertyChanged(nameof(ModelText));
        OnPropertyChanged(nameof(HasDocument));
    }
}
