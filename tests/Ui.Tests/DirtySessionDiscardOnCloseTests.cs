using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Closing a workspace and declining to save must discard the unsaved state, not merely close the
/// window over it.
///
/// <para><b>The reported bug.</b> Close a workspace holding a dirty document, answer Don't Save, then
/// open another workspace — and you are prompted to save that document again, for a workspace that no
/// longer exists. The session registry keeps a dirty session alive on purpose (so unsaved work is
/// never silently dropped), and nothing told it the user had already said no.</para>
///
/// <para><c>WorkspaceViewModel</c> needs an Avalonia host and cannot be constructed here, so these
/// drive the registry directly — which is where the decision lives and where the bug was.</para>
/// </summary>
public sealed class DirtySessionDiscardOnCloseTests
{
    private sealed class TouchCommand : IUiCommand
    {
        public string Description => "edit";
        public void Execute() { }
        public void Undo() { }
    }

    /// <summary>A registered session with an unsaved edit on it.</summary>
    private static (SchematicSessionRegistry Registry, string Path) DirtySession(
        string path = "/ws/cell/schematic/a.csch")
    {
        var registry = new SchematicSessionRegistry();
        var vm = new SchematicViewModel(new SchematicEditModel());

        registry.Register(path, vm, _ => { });
        vm.Execute(new TouchCommand());

        Assert.True(registry.IsDirty(path), "fixture is not dirty, so it proves nothing");
        return (registry, path);
    }

    /// <summary>Nothing is open — the state after a workspace's documents have been force-closed.</summary>
    private static bool NothingReferencesIt(string _) => false;

    // ── The rule that was right, and stays right ──────────────────────────────

    [Fact]
    public void RetiringNeverDropsADirtySession()
    {
        // Unchanged on purpose. Retire runs on ordinary paths — closing one tab, popping out of a
        // frame — where nobody has been asked anything, and silently dropping unsaved work there
        // would be the worse bug.
        var (registry, path) = DirtySession();

        registry.RetireIfUnreferenced(path, NothingReferencesIt);

        Assert.True(registry.IsDirty(path));
        Assert.True(registry.HasOrphanedDirtySession(NothingReferencesIt));
    }

    // ── The fix ───────────────────────────────────────────────────────────────

    [Fact]
    public void DiscardingDropsTheSessionAndItsUnsavedState()
    {
        // The user has already been prompted and declined. Keeping the flag past that point is what
        // made the next workspace open ask about a document from the workspace just closed.
        var (registry, path) = DirtySession();

        registry.DiscardIfUnreferenced(path, NothingReferencesIt);

        Assert.False(registry.IsDirty(path));
        Assert.False(registry.HasOrphanedDirtySession(NothingReferencesIt));
        Assert.False(registry.TryGet(path, out _));
    }

    [Fact]
    public void DiscardingLeavesASessionSomethingStillRefersTo()
    {
        // A torn-off document from the closing workspace survives the switch by design, and its
        // session is still live. Discarding it would tear down a window the user is looking at.
        var (registry, path) = DirtySession();

        registry.DiscardIfUnreferenced(path, p => p == path);

        Assert.True(registry.IsDirty(path));
        Assert.True(registry.TryGet(path, out _));
    }

    /// <summary>
    /// The reported sequence, at the level the bug lived: close with a dirty document, decline, then
    /// ask the question the next workspace open asks.
    /// </summary>
    [Fact]
    public void AfterDecliningToSave_TheNextWorkspaceOpenHasNothingToPromptAbout()
    {
        var (registry, path) = DirtySession();

        // What leaving a workspace now does for every session nothing open still refers to.
        foreach (string p in registry.GetOrphanedDirtyPaths(NothingReferencesIt).ToList())
            registry.DiscardIfUnreferenced(p, NothingReferencesIt);

        Assert.False(registry.HasOrphanedDirtySession(NothingReferencesIt),
            "the closed workspace's unsaved document is still being tracked, so opening another " +
            "workspace will prompt to save a document belonging to one that is gone");
    }

    /// <summary>
    /// A dirty session with no document of its OWN — a sub-cell that was pushed into, edited, and
    /// popped out of. The close path walks open documents, so this one is reached only by the sweep
    /// over orphaned dirty sessions; without that sweep it survives the close untouched.
    /// </summary>
    [Fact]
    public void ADirtySessionWithNoDocumentOfItsOwn_IsAlsoDiscarded()
    {
        var (registry, subCell) = DirtySession("/ws/sub/schematic/sub.csch");

        var orphaned = registry.GetOrphanedDirtyPaths(NothingReferencesIt);
        Assert.Contains(subCell, orphaned);

        foreach (string p in orphaned.ToList())
            registry.DiscardIfUnreferenced(p, NothingReferencesIt);

        Assert.False(registry.IsDirty(subCell));
    }

    // ── The close path calls Discard, not Retire ──────────────────────────────

    /// <summary>
    /// Pinned by source scan: <c>ResetToBlankShell</c> lives on <c>WorkspaceViewModel</c>, which needs
    /// an Avalonia host. Reverting either call to the retiring form restores the bug exactly, and does
    /// so silently — nothing else would fail.
    /// </summary>
    [Fact]
    public void LeavingAWorkspace_DiscardsRatherThanRetires()
    {
        string src = ReadRepoFile(System.IO.Path.Combine(
            "src", "Ui", "ViewModels", "WorkspaceViewModel.cs"));

        int reset = src.IndexOf("private void ResetToBlankShell", StringComparison.Ordinal);
        Assert.True(reset >= 0, "ResetToBlankShell was renamed; re-point this scan.");

        // Bounded to the method body, so an unrelated Retire elsewhere cannot satisfy it.
        int end  = src.IndexOf("\n    /// <summary>Empty the Recent Workspaces list", reset, StringComparison.Ordinal);
        string body = end > reset ? src[reset..end] : src[reset..];

        Assert.Contains("DiscardSessionIfUnreferenced",       body, StringComparison.Ordinal);
        Assert.Contains("DiscardLayoutSessionIfUnreferenced", body, StringComparison.Ordinal);
        Assert.Contains("DiscardUnreferencedDirtySessions",   body, StringComparison.Ordinal);
        Assert.DoesNotContain("RetireSessionIfUnreferenced",  body, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(
        string relativePath, [System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        var dir = System.IO.Path.GetDirectoryName(here);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir, "CLAUDE.md")))
            dir = System.IO.Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root.");
        return System.IO.File.ReadAllText(System.IO.Path.Combine(dir!, relativePath));
    }
}
