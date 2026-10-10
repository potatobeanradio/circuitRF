// brief-img-6-entry-points.md §4 — the Project Tree: .png/.jpg/.webp Known Files and in-folder OtherFiles get Create
// Schematic / Layout from Image…; a .tif does not; a broken Known File does not. Extension only — nothing is decoded.

using System;
using System.IO;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels.ProjectTree;
using Xunit;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class ProjectTreeImageRowsTests : IDisposable
{
    private readonly string _ws = Directory.CreateTempSubdirectory("crf-im6-tree-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_ws, true); } catch { /* best effort */ }
    }

    /// <summary>A file of that name holding no picture at all — the rows must not read it to decide.</summary>
    private string File_(string name)
    {
        string path = Path.Combine(_ws, name);
        File.WriteAllText(path, "not decoded");
        return path;
    }

    private static ProjectTreeNodeViewModel Node(NodeKind kind, string abs, string rel, string? warning = null) =>
        new(new ProjectTreeNode(kind, Path.GetFileName(abs), abs, rel, warningReason: warning), new ProjectTreeFilterState());

    [Theory]
    [InlineData("board.png")]
    [InlineData("board.JPG")]
    [InlineData("board.webp")]
    public void PictureKnownFilesAndInFolderFiles_GetTheRows(string name)
    {
        string path = File_(name);
        Assert.True(Node(NodeKind.KnownFile, path, name).IsImageFile);
        Assert.True(Node(NodeKind.OtherFile, path, name).IsImageFile);
    }

    [Fact]
    public void ATif_ABrokenKnownFile_AndAFileOutsideTheFolder_DoNot()
    {
        Assert.False(Node(NodeKind.KnownFile, File_("board.tif"), "board.tif").IsImageFile);
        string gone = Path.Combine(_ws, "gone.png");
        Assert.False(Node(NodeKind.KnownFile, gone, "gone.png", warning: "Known File path not found").IsImageFile);
        Assert.False(Node(NodeKind.OtherFile, File_("elsewhere.png"), "../elsewhere.png").IsImageFile);
    }
}
