// brief-dfr35-1 R-dfr35-1-7 — the panel says what can stop the mesher, as `em --dry-run` and `check` do. It builds the 3D problem,
// which the planar refresh does not, so it runs off the UI thread on a copy of the layout, and joins the panel's warnings when done.

using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;

namespace CircuitRF.Ui.Layout.Em;

public sealed partial class EmSetupEditorViewModel
{
    private int _geometryCheck;
    private EmLayoutSource? _geometryCheckSource;

    /// <summary>The check the last refresh started, for a caller that has to wait for it.</summary>
    internal Task GeometryCheck { get; private set; } = Task.CompletedTask;

    private async Task RefreshGeometryCheckAsync(EmLayoutSource source)
    {
        if (!Is3DSetup || source.Technology is not { } technology) return;
        int mine = _geometryCheck;
        var setup = Working.Clone();
        string layout = LayoutPersistence.Serialize(source.View);
        IReadOnlyList<string> said;
        try
        {
            said = await Task.Run(() =>
            {
                var copy = source with { View = LayoutPersistence.Deserialize(layout) };
                return Em3dGeometryCheck.Sentences(Em3dGenerator.Generate(setup, copy, technology).GeometryFindings);
            });
        }
        // A setup the generator cannot build says so when it runs; this only adds to the panel when it has something to add.
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or NullReferenceException) { return; }
        if (mine == _geometryCheck && said.Count > 0) Warnings = [.. Warnings, .. said];
    }
}
