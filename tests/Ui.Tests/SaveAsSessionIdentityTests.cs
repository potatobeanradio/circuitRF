using System.Collections.Generic;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Design.Layout;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Owner report, 2026-09-01: save a scratch schematic as <c>01.csch</c>, "Save As" it to
/// <c>02.csch</c>, then open <c>01.csch</c> again — and moving a component in the 02 tab moved it in
/// the 01 tab too. They were not two documents sharing a bug; they were ONE session behind two tabs.
///
/// <para>A Save As re-registers a LIVE session under its new path
/// (<c>WorkspaceViewModel.RegisterSession</c> / <c>RegisterLayoutSession</c>), but nothing unbound
/// the path it used to answer to. The old key still pointed at the same view model, so the
/// get-or-create funnel — which is deliberately a CACHE, so that a cell open as a tab and pushed
/// into elsewhere stays coherent — handed the re-opened file the other document's model instead of
/// loading it from disk.</para>
///
/// <para>Both registries have the same shape, so both are pinned here: the owner asked specifically
/// whether <c>.clay</c> could do this too, and it could.</para>
/// </summary>
public sealed class SaveAsSessionIdentityTests
{
    private sealed class StubCommand : IUiCommand
    {
        public string Description => "stub";
        public void Execute() { }
        public void Undo()    { }
    }

    private const string Path01 = "/ws/01/schematic/01.csch";
    private const string Path02 = "/ws/02/schematic/02.csch";

    // ── Schematic ─────────────────────────────────────────────────────────────

    [Fact]
    public void SchematicSaveAs_UnbindsTheOldPath_SoAReopenIsNotHandedThisSession()
    {
        var registry = new SchematicSessionRegistry();
        var vm       = new SchematicViewModel(new SchematicEditModel());

        registry.Register(Path01, vm, _ => { });   // saved as 01.csch
        registry.Register(Path02, vm, _ => { });   // …then Save As 02.csch — SAME session

        Assert.False(registry.TryGet(Path01, out _));   // a re-open of 01 must load from disk
        Assert.True(registry.TryGet(Path02, out var found));
        Assert.Same(vm, found);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void SchematicSaveAs_MovesTheDirtyFlagToTheNewPath()
    {
        var registry = new SchematicSessionRegistry();
        var vm       = new SchematicViewModel(new SchematicEditModel());
        var notified = new List<string>();

        registry.Register(Path01, vm, notified.Add);
        registry.Register(Path02, vm, notified.Add);

        vm.UndoRedo.Execute(new StubCommand());

        Assert.False(registry.IsDirty(Path01));   // 01 on disk is untouched by an edit to 02
        Assert.True(registry.IsDirty(Path02));
        Assert.Equal(new[] { Path02 }, notified); // and the old path's handler is gone, not doubled
    }

    [Fact]
    public void SchematicSaveAs_CarriesAnAlreadyDirtySessionOver()
    {
        // Save As is reached with unsaved edits on the board more often than not; the new path has
        // to inherit the dirty state rather than start out looking clean.
        var registry = new SchematicSessionRegistry();
        var vm       = new SchematicViewModel(new SchematicEditModel());

        registry.Register(Path01, vm, _ => { });
        vm.UndoRedo.Execute(new StubCommand());
        Assert.True(registry.IsDirty(Path01));

        registry.Register(Path02, vm, _ => { });

        Assert.False(registry.IsDirty(Path01));
        Assert.True(registry.IsDirty(Path02));
    }

    [Fact]
    public void SchematicRegister_TwoDistinctSessions_AreUntouchedByEachOther()
    {
        // The unbinding is keyed on the VM instance, so the ordinary case — two files, two sessions
        // — must keep both registrations.
        var registry = new SchematicSessionRegistry();
        var a = new SchematicViewModel(new SchematicEditModel());
        var b = new SchematicViewModel(new SchematicEditModel());

        registry.Register(Path01, a, _ => { });
        registry.Register(Path02, b, _ => { });

        Assert.True(registry.TryGet(Path01, out var gotA));
        Assert.True(registry.TryGet(Path02, out var gotB));
        Assert.Same(a, gotA);
        Assert.Same(b, gotB);
    }

    [Fact]
    public void ARetiredSchematicSession_NoLongerReportsDirtyPaths()
    {
        // The per-Register closure also outlived retirement: an edit to a session nothing referred
        // to any more put its path straight back into the dirty set, where the leave-workspace
        // prompt would find it.
        var registry = new SchematicSessionRegistry();
        var vm       = new SchematicViewModel(new SchematicEditModel());

        registry.Register(Path01, vm, _ => { });
        registry.RetireIfUnreferenced(Path01, _ => false);
        Assert.Equal(0, registry.Count);

        vm.UndoRedo.Execute(new StubCommand());

        Assert.False(registry.IsDirty(Path01));
        Assert.Equal(0, registry.DirtyCount);
    }

    // ── Layout — same registry shape, same defect ─────────────────────────────

    private const string Clay01 = "/ws/01/layout/01.clay";
    private const string Clay02 = "/ws/02/layout/02.clay";

    [Fact]
    public void LayoutSaveAs_UnbindsTheOldPath_SoAReopenIsNotHandedThisSession()
    {
        var registry = new LayoutSessionRegistry();
        var vm       = new LayoutEditorViewModel(new LayoutView());

        registry.Register(Clay01, vm, _ => { });
        registry.Register(Clay02, vm, _ => { });

        Assert.False(registry.TryGet(Clay01, out _));
        Assert.True(registry.TryGet(Clay02, out var found));
        Assert.Same(vm, found);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void LayoutSaveAs_MovesTheDirtyFlagToTheNewPath()
    {
        var registry = new LayoutSessionRegistry();
        var vm       = new LayoutEditorViewModel(new LayoutView());
        var notified = new List<string>();

        registry.Register(Clay01, vm, notified.Add);
        registry.Register(Clay02, vm, notified.Add);

        vm.IsDirty = true;

        Assert.False(registry.IsDirty(Clay01));
        Assert.True(registry.IsDirty(Clay02));
        Assert.Equal(new[] { Clay02 }, notified);
    }

    [Fact]
    public void LayoutRegister_TwoDistinctSessions_AreUntouchedByEachOther()
    {
        var registry = new LayoutSessionRegistry();
        var a = new LayoutEditorViewModel(new LayoutView());
        var b = new LayoutEditorViewModel(new LayoutView());

        registry.Register(Clay01, a, _ => { });
        registry.Register(Clay02, b, _ => { });

        Assert.True(registry.TryGet(Clay01, out var gotA));
        Assert.True(registry.TryGet(Clay02, out var gotB));
        Assert.Same(a, gotA);
        Assert.Same(b, gotB);
    }
}
