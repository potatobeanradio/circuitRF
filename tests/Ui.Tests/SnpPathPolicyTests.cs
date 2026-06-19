using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate tests for SnpPathPolicy.ToStored — the Browse relative/absolute decision logic.
/// </summary>
public class SnpPathPolicyTests
{
    // Build platform-appropriate paths so the tests run on Windows, macOS, and Linux.
    private static string Root  => Path.Combine(Path.GetTempPath(), "ws");
    private static string Abs(params string[] segs) => Path.Combine([Path.GetTempPath(), ..segs]);

    // ── T1: file inside the workspace subtree → relative (forward slashes) ────

    [Fact]
    public void InsideSubtree_Relative()
    {
        var root = Root;
        var file = Path.Combine(root, "touchstone", "amp.s2p");
        var result = SnpPathPolicy.ToStored(file, root);
        Assert.Equal("touchstone/amp.s2p", result);
    }

    // ── T2: file directly in the workspace root → bare filename ──────────────

    [Fact]
    public void RootItself_File_Relative()
    {
        var root = Root;
        var file = Path.Combine(root, "amp.s2p");
        var result = SnpPathPolicy.ToStored(file, root);
        Assert.Equal("amp.s2p", result);
    }

    // ── T3: one directory above the workspace root → "../amp.s2p" ────────────

    [Fact]
    public void OneUp_Relative()
    {
        var root = Root;
        var file = Path.Combine(Path.GetDirectoryName(root)!, "amp.s2p");
        var result = SnpPathPolicy.ToStored(file, root);
        Assert.Equal("../amp.s2p", result);
    }

    // ── T4: two directories above the workspace root → "../../amp.s2p" ───────

    [Fact]
    public void TwoUp_Relative()
    {
        var root = Root;
        var up2  = Path.GetDirectoryName(Path.GetDirectoryName(root)!)!;
        var file = Path.Combine(up2, "amp.s2p");
        var result = SnpPathPolicy.ToStored(file, root);
        Assert.Equal("../../amp.s2p", result);
    }

    // ── T5: three directories above → keep absolute ──────────────────────────

    [Fact]
    public void ThreeUp_Absolute()
    {
        var root = Root;
        var up3  = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(root)!)!)!;
        var file = Path.Combine(up3, "amp.s2p");
        var result = SnpPathPolicy.ToStored(file, root);
        Assert.Equal(file, result);
    }

    // ── T6: no workspace root → return absolute unchanged ────────────────────

    [Fact]
    public void NullRoot_Absolute()
    {
        var file = Abs("some", "dir", "amp.s2p");
        Assert.Equal(file, SnpPathPolicy.ToStored(file, null));
        Assert.Equal(file, SnpPathPolicy.ToStored(file, ""));
    }

    // ── T7: non-rooted input → returned unchanged (defensive) ────────────────

    [Fact]
    public void NotRooted_Input_Unchanged()
    {
        const string rel = "already/relative.s2p";
        Assert.Equal(rel, SnpPathPolicy.ToStored(rel, Root));
    }

    // ── T8: forward slashes in result (OS-agnostic) ───────────────────────────

    [Fact]
    public void ForwardSlashes_InRelativeResult()
    {
        var root   = Root;
        var file   = Path.Combine(root, "a", "b", "amp.s2p");
        var result = SnpPathPolicy.ToStored(file, root);
        Assert.DoesNotContain('\\', result);
        Assert.Equal("a/b/amp.s2p", result);
    }
}
