using Avalonia.Controls;
using Avalonia.Headless;
using CircuitRF.Ui.Diagnostics;

/// <summary>
/// Writes a captured top level by the output's extension: <c>.svg</c> is vector, drawn through
/// <see cref="UiArtworkGenerator.RenderShownWindow"/> (the docs figures' SVG device, post-pass and lints);
/// anything else is the headless frame as a PNG, as before.
/// </summary>
static class SnapshotSave
{
    public static bool IsSvg(string path) => path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);

    public static void Save(TopLevel window, string path)
    {
        if (!IsSvg(path)) { window.CaptureRenderedFrame()!.Save(path); return; }
        foreach (string note in UiArtworkGenerator.RenderShownWindow(window, path))
            Console.WriteLine("lint: " + note);
    }
}
