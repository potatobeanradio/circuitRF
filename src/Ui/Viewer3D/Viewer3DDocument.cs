// brief-em3d-28 R-em3d28-5 — the 3D view as a Dock document, opened from a 3D .cem's panel (Show 3D)
// or from a Palace/openEMS run directory in the Project Tree. Read only: nothing here is saved, so it
// is never dirty and carries no undo. Its camera is saved in the workspace's window state (the
// .cwsuser), not in the .cem.

using Dock.Model.Mvvm.Controls;

namespace CircuitRF.Ui.Viewer3D;

public sealed class Viewer3DDocument : Document
{
    /// <summary>The key a 3D view is registered under among the open documents — distinct from the
    /// .cem's own, so the setup and its view are two tabs.</summary>
    public static string KeyFor(string cemPath) => "3d:" + Path.GetFullPath(cemPath);

    public Viewer3DViewModel ViewModel { get; }
    public string CemPath => ViewModel.CemPath;

    public Viewer3DDocument(Viewer3DViewModel vm)
    {
        ViewModel = vm;
        Id = KeyFor(vm.CemPath);
        Title = vm.Title;
    }
}
