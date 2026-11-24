// ================================================================
//  TraceRowViewModel.FreqLinks.cs  —  a contour's and an expression's
//  frequency, following a marker (brief-ddl-4 R-ddl4-3, R-ddl4-4)
//
//  The model is ContourData.FreqLink and Trace.ExpressionFreqLink; the
//  resolution is LinkResolve's, as for a pinned axis. This file only
//  writes the user's choice, asks the display to follow, and shows what
//  the link resolved to.
// ================================================================

using System;
using System.Linq;
using CircuitRF.Render.DataDisplay;
using RfCore.Data;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

public partial class TraceRowViewModel
{
    private FreqLinkViewModel? _contourFreqLink, _expressionFreqLink;

    /// <summary>The link toggle beside the contour's frequency picker.</summary>
    public FreqLinkViewModel ContourFreqLink => _contourFreqLink ??= NewFreqLink(LinkGraph.ContourFreqSlot);

    /// <summary>The link toggle beside an expression's frequency picker.</summary>
    public FreqLinkViewModel ExpressionFreqLink => _expressionFreqLink ??= NewFreqLink(LinkGraph.ExpressionFreqSlot);

    /// <summary>Whether a frequency here can follow a marker: the card is in a Data Display.</summary>
    public bool CanLinkFreq => IsInDisplay;

    private FreqLinkViewModel NewFreqLink(int slot)
    {
        var vm = new FreqLinkViewModel(slot, (id, name) => CommitFreqLink(slot, id, name), () => RefreshFreqLink(slot),
                                       FindSourceNamed);
        var (id, name) = FreqLinkOf(slot);
        vm.Load(id, name);
        if (vm.IsLinked) RefreshFreqLink(slot, vm);
        return vm;
    }

    private (string? Id, string? Name) FreqLinkOf(int slot)
        => slot == LinkGraph.ContourFreqSlot
            ? (_trace.ContourData?.FreqLink, _trace.ContourData?.FreqLinkName)
            : (_trace.ExpressionFreqLink, _trace.ExpressionFreqLinkName);

    /// <summary>Writes a frequency link (or none) and has the display follow it, as one edit.</summary>
    private void CommitFreqLink(int slot, string? id, string? name)
    {
        if (slot == LinkGraph.ContourFreqSlot)
        {
            if (_trace.ContourData is not { } cd) return;
            cd.FreqLink = id; cd.FreqLinkName = name;
        }
        else { _trace.ExpressionFreqLink = id; _trace.ExpressionFreqLinkName = name; }

        if (id is null)
        {
            // Unlinked: the frequency stays where the link last put it, and the picker shows it.
            _trace.LinkNote = null;
            _trace.LinkFrozen = false;
            _trace.FrozenLinks.Clear();
        }
        _parent.FollowLinksForEdit?.Invoke();
        SyncFreqLinks();
        _parent.Notify();
    }

    /// <summary>The frequency list a link on <paramref name="slot"/> chooses from, or null before it is known.</summary>
    private Axis? FreqAxisOf(int slot)
    {
        if (slot == LinkGraph.ExpressionFreqSlot) return _trace.ExpressionFreqPin?.Freq;
        return _loadpullSurface is { } s
            ? new Axis(s.LeadingAxisName, [.. s.Frequencies], s.LeadingAxisUnit)
            : null;
    }

    private void RefreshFreqLink(int slot) => RefreshFreqLink(slot, slot == LinkGraph.ContourFreqSlot ? ContourFreqLink : ExpressionFreqLink);

    /// <summary>The picker's items and the reading: <c>@m1 → 2.4 GHz</c>, or the chip for a source that is gone.</summary>
    private void RefreshFreqLink(int slot, FreqLinkViewModel vm)
    {
        if (_parent.LinkDocumentProvider?.Invoke() is not { } doc) return;
        var sources = new LibraryDataSources(_parent.Library);
        var items = LinkCatalog.For(doc, _trace, slot, FreqAxisOf(slot), sources);
        int index = slot == LinkGraph.ContourFreqSlot ? _trace.ContourData?.FreqIndex ?? 0 : Math.Max(0, _trace.ExpressionFreqIndex);
        var labels = slot == LinkGraph.ContourFreqSlot ? (System.Collections.Generic.IReadOnlyList<string>)AvailableFrequencies
                                                       : ExpressionFrequencies;
        string at = index < labels.Count ? labels[index] : index.ToString();

        string text = "", gone = "";
        bool dangling = false, canNew = false;
        if (vm.LinkId is { } id)
        {
            if (doc.FindControl(id) is { } control) text = $"@{control.Name} → {at}";
            else if (doc.FindMarker(id) is { } site) text = $"@{site.Marker.Name} → {at}";
            else
            {
                dangling = true;
                text = $"@{vm.LinkName} → {at}";
                gone = $"{vm.LinkName} is gone";
                canNew = FindSourceNamed(vm.LinkName) is not null;
            }
        }
        vm.Show(items, text, dangling, gone, canNew);
    }

    /// <summary>
    /// After a follow: both toggles show the model, each picker shows the frequency the link chose, and the contour's
    /// status line follows its refit. Never treated as an edit.
    /// </summary>
    internal void SyncFreqLinks()
    {
        if (_trace.ContourData is { } cd)
        {
            bool saved = _suppressContourCallback;
            _suppressContourCallback = true;
            ContourFreqIndex = Math.Clamp(cd.FreqIndex, 0, Math.Max(0, AvailableFrequencies.Count - 1));
            _suppressContourCallback = saved;
            RaiseContourStatus();
            if (_contourFreqLink is { } c)
            {
                c.Load(cd.FreqLink, cd.FreqLinkName);
                if (c.IsLinked) RefreshFreqLink(LinkGraph.ContourFreqSlot, c);
            }
        }
        if (_expressionFreqLink is { } e)
        {
            e.Load(_trace.ExpressionFreqLink, _trace.ExpressionFreqLinkName);
            if (e.IsLinked) RefreshFreqLink(LinkGraph.ExpressionFreqSlot, e);
        }
        OnPropertyChanged(nameof(ExpressionFrequencies));
        OnPropertyChanged(nameof(ExpressionFreqIndex));
        OnPropertyChanged(nameof(LinkNote));
        OnPropertyChanged(nameof(ShowLinkNote));
    }
}
