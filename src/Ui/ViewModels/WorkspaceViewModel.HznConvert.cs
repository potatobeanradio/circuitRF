using Avalonia.Controls;
using CircuitRF.Design.Interchange.Hzn;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Diagnostics;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// HZN-6 R-hzn6-2 — Convert to .c3d… for one design of a Hertzon project. The project tree's design menu (HZN-3) and an
/// open design's Save As… (HZN-5) both call <see cref="ConvertHznDesignAsync"/>; it asks the dialog's three questions and then
/// hands them to <see cref="HznConvert.Convert"/>, the function <c>convert file.hzn</c> calls. No conversion logic lives here.
/// </summary>
public partial class WorkspaceViewModel
{
    /// <summary>Converts <paramref name="design"/> of <paramref name="hznPath"/> into a cell. The folder starts at this workspace's
    /// root. Returns the written <c>.c3d</c>, or null when cancelled or refused (the refusal is in Messages).</summary>
    public async Task<string?> ConvertHznDesignAsync(string hznPath, string design, Window? owner = null)
    {
        if (ResolveOwner(owner) is not { } window) return null;
        string start = (CurrentWorkspacePath is null ? null : Path.GetDirectoryName(CurrentWorkspacePath))
                       ?? Path.GetDirectoryName(Path.GetFullPath(hznPath))!;
        if (await HznConvertDialog.AskAsync(window, design, start) is not { } answer) return null;

        var result = await Task.Run(() => HznConvert.Convert(
            new HznConvertRequest(hznPath, design, answer.CellDir, answer.AddMaterials, answer.ReplaceView), GeometryKernel.Shared));
        return ReportHznConversion(result);
    }

    /// <summary>Posts a conversion's findings and its outcome to Messages; the written <c>.c3d</c>, or null when refused.</summary>
    private string? ReportHznConversion(HznConvertResult result)
    {
        foreach (var d in result.Findings)
        {
            if (d.Severity == DiagnosticSeverity.Warning) Messages.Warning(d.Render());
            else Messages.Info(d.Render());
        }
        if (result.Refusal is { } refusal) { Messages.Error(refusal.Render()); return null; }

        if (result.AddedMaterials.Count > 0)
            Messages.Info($"Added {result.AddedMaterials.Count} material(s) to the technology.", result.TechnologyPath);
        _factory.ProjectTreeTool?.Refresh();
        Messages.Success($"Converted design '{result.Design}' to cell '{Path.GetFileName(result.CellDir!)}'.", result.ViewPath);
        return result.ViewPath;
    }
}
