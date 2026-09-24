using System;
using System.IO;
using CircuitRF.Ui.Views.Dialogs;
using Xunit;

namespace CircuitRF.Ui.Tests;

// Field report 2026-09-24: the loose-layout technology prompt listed only the workspace's tech/
// folder, so a Gerber-minted technology beside its import — just made the workspace default — was
// not offered at all. Every .ctech in the workspace is listed now, the default first.
public sealed class OrphanTechnologyListTests : IDisposable
{
    private readonly string _ws = Path.Combine(Path.GetTempPath(), "crf_orphantech_" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_ws, recursive: true); } catch { }
    }

    [Fact]
    public void TheWorkspaceDefault_IsListedFirst_WhereverItLives()
    {
        Directory.CreateDirectory(Path.Combine(_ws, "tech"));
        Directory.CreateDirectory(Path.Combine(_ws, "Gerber"));
        File.WriteAllText(Path.Combine(_ws, "tech", "a.ctech"), "{}");
        File.WriteAllText(Path.Combine(_ws, "Gerber", "Gerber.ctech"), "{}");
        WorkspacePersistence.SaveToFile(Path.Combine(_ws, ".cws"),
                                        new CwsFile { DefaultTechRef = "Gerber/Gerber.ctech" });

        var listed = OrphanTechnologyDialog.WorkspaceTechnologies(Path.Combine(_ws, ".cws"));

        Assert.Equal(2, listed.Count);
        Assert.Equal(Path.GetFullPath(Path.Combine(_ws, "Gerber", "Gerber.ctech")), Path.GetFullPath(listed[0]));
    }
}
