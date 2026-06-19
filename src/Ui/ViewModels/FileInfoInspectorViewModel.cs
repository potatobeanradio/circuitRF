using System;
using System.IO;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// Read-only view-model for a single file or directory in the Properties pane.
/// Populated when a Known File leaf or OtherFile node is selected in the Project Tree.
/// </summary>
public sealed class FileInfoInspectorViewModel
{
    public string Name         { get; }
    public string SizeText     { get; }
    public string ModifiedText { get; }

    public FileInfoInspectorViewModel(string absPath)
    {
        var fi     = new FileInfo(absPath);
        Name         = fi.Name;
        SizeText     = fi.Exists ? FormatSize(fi.Length) : "—";
        ModifiedText = fi.Exists
            ? fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm")
            : "—";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024L)             return $"{bytes} B";
        if (bytes < 1024L * 1024)      return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }
}
