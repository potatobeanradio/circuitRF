using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace CircuitRF.Ui.Tests.Harmonica;

// ── R-h9a-1 — a NativeMenu instance is set on at most one AvaloniaObject at a time ─────────────────
//
// Tools ▸ harmonicaRF, drag the tab out: HarmonicaMenuView.axaml declares its NativeMenu on the
// UserControl itself (so its bindings have a DataContext to resolve against); AttachNativeMenuIfOwnWindow
// then handed that SAME instance to the hosting Window without ever detaching it from the control first —
// one NativeMenu owned by two AvaloniaObjects at once, and the second exporter's Update() throws
// ArgumentException("The menu being updated does not match.").
//
// HarmonicaMenuView is a UserControl and cannot be constructed headlessly in this suite (no Avalonia
// platform), so — per this repo's own InitializeComponentShadowingTests precedent — the invariant is
// pinned by a source scan rather than by driving the real attach/detach machinery.

public class HarmonicaMenuNativeAttachTests
{
    private static string RepoRoot([CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root (no CLAUDE.md found walking up from this test file).");
        return dir!;
    }

    /// <summary>Source with comments removed, matching InitializeComponentShadowingTests' own reason:
    /// this file's own doc comments describe the bug being fixed and would otherwise trip a naive scan
    /// for the absence of the crashing pattern.</summary>
    private static string CodeOnly()
    {
        string path = Path.Combine(RepoRoot(), "src", "Ui", "Views", "Harmonica", "HarmonicaMenuView.axaml.cs");
        string src = File.ReadAllText(path);
        src = Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline);
        src = Regex.Replace(src, @"//[^\r\n]*", "");
        return src;
    }

    [Fact]
    public void RecomputeAttachment_DetachesThePriorHolder_BeforeAttachingToTheDesiredTarget()
    {
        string src = CodeOnly();

        // The one call site that hands the menu to whichever AvaloniaObject it should now own (the
        // crashing operation, when that target is a Window already holding it elsewhere) must be
        // preceded, in the SAME method, by a null-out of whatever object held it before — proven here
        // by requiring both statements to appear, with the detach textually first.
        int detachIdx = src.IndexOf("NativeMenu.SetMenu(current, null);", StringComparison.Ordinal);
        int attachIdx = src.IndexOf("NativeMenu.SetMenu(desiredTarget, _ownMenu);", StringComparison.Ordinal);

        Assert.True(detachIdx >= 0, "Expected a detach-the-prior-holder call before attaching to the desired target.");
        Assert.True(attachIdx >= 0, "Expected the desired-target attach call this view actually performs.");
        Assert.True(detachIdx < attachIdx,
            "The prior holder must be detached BEFORE the menu is attached to the desired target, not after " +
            "(attaching first would recreate the exact 'owned by two AvaloniaObjects at once' crash).");
    }

    // ── owner-reported: closing a TORN-OFF window crashed the app ───────────────────────────────────
    //
    // A window's own native teardown can already be under way by the time DetachedFromVisualTree fires
    // on its content, so the SAME "menu being updated does not match" ArgumentException R-h9a-1's own
    // double-attach bug threw can also come out of the plain detach-on-close call
    // (DetachNativeMenuFromWindow's own NativeMenu.SetMenu(window, null)) — a different trigger of the
    // identical native exporter defect, uncaught, killing the whole application over a window that was
    // already closing.

    [Fact]
    public void DetachNativeMenuFromWindow_SwallowsTheNativeExporterException_SoAClosingWindowCannotCrashTheApp()
    {
        string src = CodeOnly();

        int methodStart = src.IndexOf("private void DetachNativeMenuFromWindow()", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "Expected to find DetachNativeMenuFromWindow.");
        int methodEnd = src.IndexOf("\n    }", methodStart, StringComparison.Ordinal);
        Assert.True(methodEnd >= 0, "Expected DetachNativeMenuFromWindow's closing brace.");
        string body = src[methodStart..methodEnd];

        Assert.Contains("NativeMenu.SetMenu(window, null);", body, StringComparison.Ordinal);
        // The detach call itself must be guarded — a closing window's native menu exporter can be in a
        // state where this throws, and an unhandled exception here takes the whole app down with it.
        Assert.Contains("try", body, StringComparison.Ordinal);
        Assert.Contains("catch", body, StringComparison.Ordinal);
    }

    [Fact]
    public void DetachedFromVisualTree_ReleasesTheMenuFromAClosingWindow()
    {
        string src = CodeOnly();

        Assert.Contains("DetachedFromVisualTree", src, StringComparison.Ordinal);
        // The cleanup path must itself call NativeMenu.SetMenu(..., null) — a closed window must not be
        // left holding the menu for a now-dead platform exporter (R-h9a-1's other half).
        Assert.Contains("NativeMenu.SetMenu(window, null);", src, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMenuInstance_IsCapturedOnce_NeverReReadOffTheControlAfterItMayHaveMoved()
    {
        string src = CodeOnly();

        // NativeMenu.GetMenu(this) may only appear where the instance is first captured (the
        // constructor) — every later consumer (RebuildNativeBandMenus, the attach/detach pair) must read
        // the captured field instead, since the instance may no longer be attached to `this` at all.
        var offenders = Regex.Matches(src, @"NativeMenu\.GetMenu\(this\)").Count;
        Assert.True(offenders <= 1,
            "NativeMenu.GetMenu(this) must be read at most once (the initial capture); every other " +
            "consumer must use the captured _ownMenu field, or it will silently find nothing once the " +
            "menu has moved to a hosting window.");
    }
}
