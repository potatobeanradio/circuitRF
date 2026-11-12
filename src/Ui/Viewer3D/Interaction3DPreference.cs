// Which mouse and key scheme the 3D views use: circuitRF's own, or Hertzon's (Settings ▸ General ▸ 3D View). Per user, in
// AppPreferences, never in the .c3d — how someone steers a camera is a working habit, and a design from someone else must not
// change it. The shape of Realistic3DPreference, test seam included. Read on every press, so a change takes effect at once in
// every open view.

using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Viewer3D;

public static class Interaction3DPreference
{
    internal static bool? TestOverrideStore;
    internal static bool TestOverrideActive;

    private static bool? _cached;

    /// <summary>Settings ▸ General ▸ 3D View ▸ Use Hertzon interactivity. On by default (owner, 2026-11-12).</summary>
    public static bool Hertzon
    {
        get
        {
            if (TestOverrideActive) return TestOverrideStore ?? true;
            return _cached ??= AppPreferencesIo.Load().View3DHertzonInteractivity ?? true;
        }
        set
        {
            if (TestOverrideActive) { TestOverrideStore = value; return; }
            _cached = value;
            AppPreferencesIo.Update(p => p.View3DHertzonInteractivity = value ? null : false);
        }
    }
}
