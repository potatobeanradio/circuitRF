// brief-lcp-4 — a .cem's component ports, drawn on the open layout it analyses.
//
// R-lcp4-1: the .cem pushes; the layout holds no opinion. One .cem's marks show on a layout at a time — the most recently
// active one on it (its "owner" here). A .cem that has never been active on a layout with no owner takes it when its marks
// are built, so opening one .cem beside its layout shows its marks without a click. Closing the owner clears them.
//
// R-lcp4-4: the marks are cleared by the layout's own edit (LayoutEditorViewModel's Model.Changed) and come back when the
// .cem's debounced build lands. A build copied before an edit is refused at the layout (LayoutView.EditCount), so it never
// lands over the edit.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Threading;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;

namespace CircuitRF.Ui.ViewModels;

public partial class WorkspaceViewModel
{
    /// <summary>Per open layout (absolute path): the <c>.cem</c> whose component ports it shows. Held by its view model, not its
    /// path, which Save As changes.</summary>
    private readonly Dictionary<string, EmSetupEditorViewModel> _componentPortMarksOwner = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The open layout editor <paramref name="vm"/> analyses, and that layout's path; null when it is not open.</summary>
    private (LayoutEditorViewModel Layout, string Path)? OpenLayoutOfEmSetup(EmSetupEditorViewModel vm)
    {
        if (vm.IsEmbedded || vm.Working.LayoutRef is not { Length: > 0 } layoutRef
            || ResolveEmLayoutPath(vm.FilePath, layoutRef) is not { } target)
            return null;
        foreach (var open in _openDocsByPath.Values.OfType<LayoutDocument>())
            if (open.FilePath is { } fp && string.Equals(Path.GetFullPath(fp), target, StringComparison.OrdinalIgnoreCase))
                return (open.ViewModel, target);
        return null;
    }

    private bool IsOpenEmSetup(EmSetupEditorViewModel vm)
        => _openDocsByPath.Values.OfType<EmSetupDocument>().Any(d => ReferenceEquals(d.ViewModel, vm));

    /// <summary>The <c>.cem</c> built its marks: they show when it owns its layout, or nothing does.</summary>
    private void PushComponentPortMarks(EmSetupEditorViewModel vm)
    {
        if (!IsOpenEmSetup(vm)) return;
        var target = OpenLayoutOfEmSetup(vm);
        LeaveOtherLayouts(vm, target?.Path);
        if (target is not var (layout, path)) return;
        if (_componentPortMarksOwner.TryGetValue(path, out var owner) && !ReferenceEquals(owner, vm))
            return;
        _componentPortMarksOwner[path] = vm;
        // brief-icp-7 R-icp7-6 — the layout's solver-geometry toggle asks the .cem speaking for it.
        layout.SolverGeometryRequested = wanted => vm.SolverGeometryWanted = wanted;
        vm.SolverGeometryWanted = layout.ShowSolverGeometry;
        // Built from another copy of the layout (the .cem resolved it before the editor opened): build again from the editor's.
        if ((vm.ComponentPortMarks.Count > 0 || vm.ComponentCutMarks.Count > 0 || vm.SolverGeometry is not null)
            && !ReferenceEquals(vm.ComponentPortMarksView, layout.Model)) return;
        layout.ShowComponentPortMarks(Path.GetFileName(vm.FilePath), vm.ComponentPortMarks, vm.ComponentPortMarksEditCount,
                                      vm.ComponentCutMarks, vm.SolverGeometry);
    }

    /// <summary>A <c>.cem</c> became the active document: its marks replace any other setup's on its layout.</summary>
    private void ClaimComponentPortMarks(EmSetupEditorViewModel vm)
    {
        var target = OpenLayoutOfEmSetup(vm);
        LeaveOtherLayouts(vm, target?.Path);
        if (target is not var (layout, path)) return;
        _componentPortMarksOwner[path] = vm;
        if (!ReferenceEquals(vm.ComponentSourceView, layout.Model))
        {
            // The .cem read the layout from disk before the editor was open; it reads the editor's now, and its build pushes.
            Dispatcher.UIThread.Post(vm.Refresh, DispatcherPriority.Background);
            layout.ShowComponentPortMarks(null, [], layout.Model.EditCount);
            return;
        }
        PushComponentPortMarks(vm);
    }

    /// <summary>A layout became the active document: a <c>.cem</c> on it that read the layout before the editor opened reads
    /// the editor's model now, so its marks have somewhere to go.</summary>
    private void RefreshComponentPortMarksFor(LayoutDocument layoutDoc)
    {
        // brief-icp-7 R-icp7-6 — the solver-geometry toggle reaches the .cem that owns this layout's marks.
        if (layoutDoc.FilePath is { } lp && _componentPortMarksOwner.TryGetValue(Path.GetFullPath(lp), out var owner))
        {
            layoutDoc.ViewModel.SolverGeometryRequested = wanted => owner.SolverGeometryWanted = wanted;
            owner.SolverGeometryWanted = layoutDoc.ViewModel.ShowSolverGeometry;
        }
        foreach (var cem in _openDocsByPath.Values.OfType<EmSetupDocument>().ToList())
            if (cem.ViewModel.Working.ComponentPorts.Count > 0
                && OpenLayoutOfEmSetup(cem.ViewModel) is var (layout, _)
                && ReferenceEquals(layout, layoutDoc.ViewModel)
                && !ReferenceEquals(cem.ViewModel.ComponentSourceView, layout.Model))
                Dispatcher.UIThread.Post(cem.ViewModel.Refresh, DispatcherPriority.Background);
    }

    /// <summary>A <c>.cem</c> closed: nothing it built may land, and its marks leave its layout (R-lcp4-1).</summary>
    private void ReleaseComponentPortMarks(EmSetupEditorViewModel vm)
    {
        vm.CancelComponentPortMarks();
        LeaveOtherLayouts(vm, null);
    }

    /// <summary>Every layout <paramref name="vm"/> owns other than <paramref name="keep"/> is released and its marks cleared: a
    /// setup pointed at another layout (Change Layout, or its undo) no longer speaks for the one it left.</summary>
    private void LeaveOtherLayouts(EmSetupEditorViewModel vm, string? keep)
    {
        foreach (var (path, owner) in _componentPortMarksOwner.ToList())
        {
            if (!ReferenceEquals(owner, vm) || string.Equals(path, keep, StringComparison.OrdinalIgnoreCase)) continue;
            _componentPortMarksOwner.Remove(path);
            foreach (var open in _openDocsByPath.Values.OfType<LayoutDocument>())
                if (open.FilePath is { } fp && string.Equals(Path.GetFullPath(fp), path, StringComparison.OrdinalIgnoreCase))
                    open.ViewModel.ShowComponentPortMarks(null, [], open.ViewModel.Model.EditCount);
        }
    }
}
