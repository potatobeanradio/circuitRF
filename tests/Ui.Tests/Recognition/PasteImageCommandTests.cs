// brief-img-6-entry-points.md §4 — Edit ▸ Paste Image as Schematic… / as Layout…: enabled only when the clipboard holds
// a picture, and the clipboard is asked only when the window is activated or the Edit menu opens — never polled.

using System;
using System.IO;
using System.Threading.Tasks;
using CircuitRF.Design.Imaging;
using CircuitRF.Ui.Recognition;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.Recognition;

internal sealed class FakePictureClipboard : IPictureClipboard
{
    public PictureClipboardContents Contents { get; set; } = PictureClipboardContents.Nothing;
    public bool Throws { get; set; }
    public int Peeks { get; private set; }

    public Task<PictureClipboardContents> PeekAsync()
    {
        Peeks++;
        if (Throws) throw new InvalidOperationException("the clipboard is busy");
        return Task.FromResult(Contents);
    }

    public Task<ImageSourceResult?> ReadAsync() => Task.FromResult<ImageSourceResult?>(null);
}

/// <summary>The two IM-6 classes that build a <see cref="WorkspaceViewModel"/>, which builds Avalonia menu items:
/// that type's static registration is not thread-safe, so they run one at a time (src/Ui/RESOLVED.md, the
/// StackupDeleteKeyTests note).</summary>
[CollectionDefinition(Name)]
public sealed class ImageEntryPointsCollection
{
    public const string Name = "Image entry points";
}

[Collection(ImageEntryPointsCollection.Name)]
public sealed class PasteImageCommandTests : IDisposable
{
    private readonly string _ws = Directory.CreateTempSubdirectory("crf-im6-paste-").FullName;
    private readonly FakePictureClipboard _clipboard = new();
    private readonly WorkspaceViewModel _vm;

    public PasteImageCommandTests()
    {
        File.WriteAllText(Path.Combine(_ws, ".cws"), "{}");
        _vm = new WorkspaceViewModel { CurrentWorkspacePath = Path.Combine(_ws, ".cws") };
        _vm.PictureClipboard = () => _clipboard;
    }

    public void Dispose()
    {
        try { Directory.Delete(_ws, true); } catch { /* best effort */ }
    }

    private bool Enabled => _vm.PasteImageAsSchematicCommand.CanExecute(null) && _vm.PasteImageAsLayoutCommand.CanExecute(null);
    private bool Disabled => !_vm.PasteImageAsSchematicCommand.CanExecute(null) && !_vm.PasteImageAsLayoutCommand.CanExecute(null);

    [Fact]
    public async Task TheRowsFollowWhatTheClipboardHolds()
    {
        _clipboard.Contents = new PictureClipboardContents(true, []);
        await _vm.RefreshPasteImage();
        Assert.True(Enabled);   // a bitmap

        _clipboard.Contents = PictureClipboardContents.Nothing;
        await _vm.RefreshPasteImage();
        Assert.True(Disabled);  // text alone: neither a bitmap nor a file

        _clipboard.Contents = new PictureClipboardContents(false, [Path.Combine(_ws, "board.png")]);
        await _vm.RefreshPasteImage();
        Assert.True(Enabled);   // one picture file, copied in a file manager

        // Two files disable it — and so does the folder icon a file manager puts beside them as a bitmap.
        _clipboard.Contents = new PictureClipboardContents(true, [Path.Combine(_ws, "a.png"), Path.Combine(_ws, "b.png")]);
        await _vm.RefreshPasteImage();
        Assert.True(Disabled);

        _clipboard.Throws = true;
        await _vm.RefreshPasteImage();
        Assert.True(Disabled);  // a failed read leaves them disabled, and does not throw
    }

    [Fact]
    public async Task TheClipboardIsAskedOnActivationAndMenuOpen_NotOtherwise()
    {
        _clipboard.Contents = new PictureClipboardContents(true, []);
        Assert.True(Disabled);   // building the commands and querying them asks nothing
        Assert.Equal(0, _clipboard.Peeks);

        await _vm.RefreshPasteImage();   // the window activated
        await _vm.RefreshPasteImage();   // the Edit menu opened
        _ = _vm.PasteImageAsSchematicCommand.CanExecute(null);

        Assert.Equal(2, _clipboard.Peeks);
        Assert.True(Enabled);

        // And the window asks at exactly those two moments: its Activated handler, the native Edit menu's NeedsUpdate
        // and the in-window Edit menu's SubmenuOpened — nowhere else in the application.
        string root = RepoRoot();
        string window = Code(File.ReadAllText(Path.Combine(root, "src/Ui/Views/WorkspaceWindow.axaml.cs")));
        Assert.Equal(3, Count(window, "RefreshPasteImage()"));
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src/Ui"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.EndsWith("WorkspaceWindow.axaml.cs")
                || file.EndsWith("WorkspaceViewModel.ImageRecognition.cs")) continue;
            Assert.DoesNotContain("RefreshPasteImage", Code(File.ReadAllText(file)));
        }
    }

    private static int Count(string text, string what)
    {
        int n = 0;
        for (int i = text.IndexOf(what, StringComparison.Ordinal); i >= 0; i = text.IndexOf(what, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }

    /// <summary>The source with its comments removed — a call named in a comment is not a call.</summary>
    private static string Code(string source) =>
        System.Text.RegularExpressions.Regex.Replace(source, @"//[^\n]*|/\*.*?\*/", "", System.Text.RegularExpressions.RegexOptions.Singleline);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
