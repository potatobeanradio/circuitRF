using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Owner, 2026-08-17: the Schematic editor's context menu ▸ <b>Edit Parameters</b> "opens an inline
/// text editor. It is supposed to open the Component Parameters dialog box."
///
/// <para>It was a placeholder left from before that dialog existed — it called
/// <c>BeginInlineEdit</c> on the component's FIRST parameter, which is neither the dialog nor a choice
/// the user made: a component with several parameters silently offered exactly one of them, and one
/// with no parameters did nothing at all. Both entry points now route through a single
/// <c>OpenParameterEditorFor</c>.</para>
///
/// <para>View code-behind cannot be exercised headlessly (no Avalonia test host in this project), so
/// this is a SOURCE scan — the same technique the Harmonica view tests use for the same reason. It
/// gates the thing that actually regressed: a second, divergent implementation of "show me this
/// component's parameters".</para>
/// </summary>
public sealed class SchematicEditParametersMenuTests
{
    private static string Source() => ReadSource("src", "Ui", "Views", "Content", "SchematicView.axaml.cs");

    private static string ReadSource(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        string path = Path.Combine([dir!.FullName, .. parts]);
        Assert.True(File.Exists(path), $"source not found at {path}");
        return File.ReadAllText(path);
    }

    /// <summary>Comments are stripped before matching: this file's own comments NAME the bug and the
    /// method, so a scan over raw text would pass on the prose alone.</summary>
    private static string StrippedBody(string methodSignature)
    {
        string src = Source();
        int start = src.IndexOf(methodSignature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{methodSignature} not found");

        // To the next member at class-indent level.
        int end = src.IndexOf("\n    private ", start + methodSignature.Length, StringComparison.Ordinal);
        if (end < 0) end = src.Length;

        var sb = new System.Text.StringBuilder();
        foreach (string line in src[start..end].Split('\n'))
        {
            string t = line.TrimStart();
            if (t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("///", StringComparison.Ordinal))
                continue;
            int slashes = line.IndexOf("//", StringComparison.Ordinal);
            sb.AppendLine(slashes >= 0 ? line[..slashes] : line);
        }
        return sb.ToString();
    }

    [Fact]
    public void EditParametersMenuItem_OpensTheDialog_NotTheInlineEditor()
    {
        string body = StrippedBody("private void OnCtxEditParameters(");

        Assert.Contains("OpenParameterEditorFor(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("BeginInlineEdit", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowInlineEditBox", body, StringComparison.Ordinal);
    }

    /// <summary>The double-click path must be the SAME implementation, not a copy of it — two copies is
    /// how the menu came to answer differently from the double-click in the first place.</summary>
    [Fact]
    public void ComponentDoubleClick_RoutesToTheSameOpener()
    {
        string body = StrippedBody("private void OnComponentDoubleTapped(");

        Assert.Contains("OpenParameterEditorFor(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("new ParameterEditorDialog", body, StringComparison.Ordinal);
    }

    /// <summary>And the shared opener is what actually constructs the dialogs — including the VAR/MEAS
    /// special case and the Ground guard, which the menu previously had no version of at all.</summary>
    [Fact]
    public void TheSharedOpener_BuildsTheDialogs_AndKeepsTheGroundGuard()
    {
        string body = StrippedBody("private void OpenParameterEditorFor(");

        Assert.Contains("new ParameterEditorDialog", body, StringComparison.Ordinal);
        Assert.Contains("new VarEditorDialog", body, StringComparison.Ordinal);
        Assert.Contains("SymbolKind.Ground", body, StringComparison.Ordinal);
    }
}
