// ================================================================
//  SetupPanelSectionsTests.cs
//
//  The EM setup panel's Notes and Component ports sections open and close, and the setup keeps which, in a .cem and in a
//  .c3d's embedded setup alike. Notes is open by default; Component ports is open by default only when the setup names a
//  component port. No run reads either, so neither may make a result look out of date.
// ================================================================

using System.Linq;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.Layout.Em;
using Xunit;

namespace CircuitRF.Ui.Tests.Em;

public sealed class SetupPanelSectionsTests
{
    [Fact]
    public void TheDefaults_FollowTheSetup_AndAFileThatStatesNeitherSavesNeither()
    {
        var plain = new EmSetupEditorViewModel("x.cem", new EmSetup());
        Assert.True(plain.NotesExpanded);
        Assert.False(plain.ComponentPortsExpanded);

        var withPorts = new EmSetupEditorViewModel("x.cem", new EmSetup { ComponentPorts = [new EmComponentPort("R1")] });
        Assert.True(withPorts.ComponentPortsExpanded);

        string json = EmSetupPersistence.Serialize(new EmSetup());
        Assert.DoesNotContain("NotesExpanded", json);
        Assert.DoesNotContain("ComponentPortsExpanded", json);
    }

    [Fact]
    public void AToggle_IsAnEditOfTheSetup_AndRoundTripsThroughTheFile()
    {
        var vm = new EmSetupEditorViewModel("x.cem", new EmSetup());
        vm.NotesExpanded = false;
        vm.ComponentPortsExpanded = true;

        Assert.Equal(false, vm.Working.NotesExpanded);
        Assert.Equal(true, vm.Working.ComponentPortsExpanded);
        Assert.True(vm.UndoRedo.CanUndo);

        var back = EmSetupPersistence.Deserialize(EmSetupPersistence.Serialize(vm.Working.Clone()));
        Assert.Equal(false, back.NotesExpanded);
        Assert.Equal(true, back.ComponentPortsExpanded);
        var reopened = new EmSetupEditorViewModel("x.cem", back);
        Assert.False(reopened.NotesExpanded);
        Assert.True(reopened.ComponentPortsExpanded);
    }

    [Fact]
    public void A3dViewsRunIdentity_IgnoresThePanelsSections()
    {
        string Identity(bool? notes)
        {
            var doc = new C3dDocument();
            doc.Setups.Add(EmSetupPersistence.ToEmbedded(new EmSetup { Name = "EM1", NotesExpanded = notes, ComponentPortsExpanded = notes }));
            return C3dPersistence.SerializeForRun(doc);
        }
        Assert.Equal(Identity(null), Identity(false));
    }
}
