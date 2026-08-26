// The record moved, not the file (brief-cli-em-verb.md §5): EmSetupEditorViewModel is the `.cem`
// editor and stays in src/Ui — this four-field record just happened to be declared at the top of it,
// and EmRunService.Run takes one, so it had to cross with the run service.

namespace CircuitRF.Design.Layout.Em;

/// <summary>What the workspace hands back when a <c>.cem</c>'s <see cref="EmSetup.LayoutRef"/> is
/// resolved. R-em-10: the geometry is read HERE, at use time, never embedded in the <c>.cem</c> —
/// which is the whole reason re-running after a layout edit picks the edit up.</summary>
public sealed record EmLayoutSource(
    string      AbsolutePath,
    LayoutView  View,
    Technology? Technology,
    int         DbuPerMicron);
