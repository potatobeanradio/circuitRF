using System.Text.RegularExpressions;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Every document view whose document can request activation focus must actually take it.
///
/// <para><b>The failure this exists for is silent and only shows on FIRST open.</b> A document view
/// that handles a keystroke does it with a handler tunnelling from the view itself, which only ever
/// sees a key that is already routing through that view — so something inside it has to hold focus.
/// A tab is activated before its view is bound, so on first open focus is still wherever it was and
/// the keystroke routes somewhere else entirely. Click any field and the same key starts working,
/// which is why this reads as "sometimes the keyboard does nothing" rather than as a missing
/// subscription.</para>
///
/// <para>Reported against the .ctech editor: Page Up / Page Down did nothing on the Layers tab until
/// something in the editor had been clicked. It was the only one of the four document views that
/// never subscribed. Scanned rather than exercised because there is no headless Avalonia host in this
/// suite — the same reason <c>TechEditorScrollKeys</c> exists as a framework-free type beside its
/// view.</para>
/// </summary>
public sealed class ActivatableDocumentViewFocusTests
{
    /// <summary>Each activatable document, and the view that is supposed to answer for it.</summary>
    public static TheoryData<string, string> DocumentViews => new()
    {
        { "SchematicDocument",    Path.Combine("src", "Ui", "Views", "Content", "SchematicView.axaml.cs") },
        { "SymbolEditorDocument", Path.Combine("src", "Ui", "Views", "Content", "SymbolEditorView.axaml.cs") },
        { "LayoutDocument",       Path.Combine("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml.cs") },
        { "TechDocument",         Path.Combine("src", "Ui", "Views", "Layout", "TechEditorView.axaml.cs") },
    };

    [Theory]
    [MemberData(nameof(DocumentViews))]
    public void EveryActivatableDocumentsView_SubscribesToActivationFocus(string document, string viewPath)
    {
        string src = Src(viewPath);

        Assert.True(src.Contains("ActivationFocusRequested", StringComparison.Ordinal),
            $"{viewPath} hosts {document}, which raises ActivationFocusRequested, but never subscribes. " +
            "On first open the tab is activated before the view binds, so nothing inside the view has " +
            "focus and its own keyboard handling is dead until something is clicked.");

        // Subscribing is not enough on its own: the request that fires BEFORE the view binds is the
        // first-open case, and it is only reachable by consuming the pending flag at bind time.
        Assert.True(src.Contains("ConsumeActivationFocus", StringComparison.Ordinal),
            $"{viewPath} subscribes to ActivationFocusRequested but never calls ConsumeActivationFocus. " +
            "The activation that matters happens before the view exists to hear it, so the pending " +
            "flag is the only way that first one is ever seen.");
    }

    /// <summary>
    /// The .ctech editor's own half of it: focus has somewhere to land.
    ///
    /// <para>Its scroll handler tunnels from the view root, so the view root is what takes focus —
    /// which requires it to be focusable, and requires it NOT to be a tab stop, or a control that
    /// exists only as a programmatic focus target joins the Tab cycle the user walks through.</para>
    /// </summary>
    [Fact]
    public void TheTechEditorRoot_IsFocusableButNotATabStop()
    {
        string axaml = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Ui", "Views", "Layout", "TechEditorView.axaml"));

        // The root element only — a Focusable further in would satisfy a naive Contains.
        string root = axaml[..axaml.IndexOf('>', axaml.IndexOf("<UserControl", StringComparison.Ordinal))];

        Assert.Contains("Focusable=\"True\"", root, StringComparison.Ordinal);
        Assert.Contains("IsTabStop=\"False\"", root, StringComparison.Ordinal);
    }

    /// <summary>
    /// The undock route into the same dead keyboard.
    ///
    /// <para>Floating the editor builds a NEW window around the view. A new window's activation is
    /// not the dock's activation, so no <c>IActivatableDocument</c> request fires and the
    /// subscription above never runs — the keys are dead again with nothing having changed in the
    /// code that handles them. Attaching to a visual tree is the one event both routes share, which
    /// is why the focus is taken there as well as on activation.</para>
    ///
    /// <para>The guard matters as much as the call: an attach also happens on an ordinary dock
    /// rearrangement, and taking focus unconditionally would pull the caret out of whatever panel the
    /// user was typing in.</para>
    /// </summary>
    [Fact]
    public void TheTechEditor_TakesFocusOnAttach_ButOnlyIfNothingElseHoldsIt()
    {
        string src = Src(Path.Combine("src", "Ui", "Views", "Layout", "TechEditorView.axaml.cs"));

        Assert.True(src.Contains("OnAttachedToVisualTree", StringComparison.Ordinal),
            "TechEditorView takes focus only on document activation, so undocking it into its own " +
            "window leaves Page Up/Down dead — a floating window's activation is not the dock's.");

        Assert.True(src.Contains("GetFocusedElement", StringComparison.Ordinal),
            "TechEditorView takes focus on attach without checking whether anything already holds it. " +
            "An attach also happens on a plain dock rearrangement, so this yanks the caret out of " +
            "whatever panel the user was typing in.");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>One source file with its comments stripped — this is about what the code does, and
    /// the comments here name the very symptom being scanned for.</summary>
    private static string Src(string relative)
    {
        string raw = File.ReadAllText(Path.Combine(RepoRoot(), relative));
        raw = Regex.Replace(raw, @"/\*.*?\*/", "", RegexOptions.Singleline);
        raw = Regex.Replace(raw, @"//[^\n]*", "");
        return raw;
    }
}
