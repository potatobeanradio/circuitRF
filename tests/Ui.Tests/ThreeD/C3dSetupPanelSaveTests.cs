// Save pressed in a .c3d's setup panel: the panel's FilePath names the container, and writing the setup there replaced the
// whole document with one setup, which the file watcher then reported back as someone else's change.

using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.ThreeD;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class C3dSetupPanelSaveTests : IDisposable
{
    private readonly string _ws = Directory.CreateTempSubdirectory("crf-setup-save-").FullName;

    public void Dispose() { try { Directory.Delete(_ws, true); } catch (IOException) { } }

    [Fact]
    public void SaveInASetupPanel_SavesTheWholeC3d_WithTheFieldBeingTyped()
    {
        string path = Path.Combine(_ws, "cell.c3d");
        C3dPersistence.SaveToFile(path, new C3dDocument
        {
            Objects = [new C3dBox { Name = "keep", Material = "Copper", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(1000, 1000, 1000) }],
            Setups = [EmSetupPersistence.ToEmbedded(new EmSetup { Name = "S1", Solver3D = Em3dSolverId.Palace })],
        });
        using var c3d = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => new PatchRecordingBackend(),
                                               () => Path.Combine(_ws, ".cws"), a => a());
        c3d.RestoreActiveSetup(null);
        c3d.SaveRequested = () => { Assert.Null(c3d.Save()); return Task.CompletedTask; };

        var panel = c3d.SetupEditor!.ViewModel;
        panel.PalaceGradingText = "1.7";                                           // typed, the focus still in the field
        panel.CommitFocusedField = () => panel.CommitPalaceField("Palace.Grading");
        panel.SaveCommand.Execute(null);

        var saved = C3dPersistence.LoadFromFile(path);
        Assert.Equal("keep", Assert.Single(saved.Objects).Name);
        Assert.Equal(1.7, C3dSetups.Read(saved).Single().Setup!.Palace?.Grading);
        Assert.False(c3d.IsDirty);
    }
}
