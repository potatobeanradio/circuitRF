// The preferred solvers for new setups (Settings ▸ Solvers): what is stored, and what an absent or unknown value means.

using CircuitRF.Design.Layout.Em;
using CircuitRF.Ui.Layout.Em;
using Xunit;

namespace CircuitRF.Ui.Tests.Em;

// In the 3D collection with every other class that reads the preference, so no two of them change its test store at once. The
// override is switched on and never off: a class running beside one that switched it off would write this machine's file.
[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class EmSolverPreferenceTests : IDisposable
{
    public EmSolverPreferenceTests() { EmSolverPreference.TestOverrideActive = true; Clear(); }

    public void Dispose() => Clear();

    private static void Clear() => (EmSolverPreference.TestLayoutStore, EmSolverPreference.TestThreeDStore) = (null, null);

    /// <summary>Nothing stored is Planar for a .cem and Palace for a .c3d (what new setups started on before the setting), and
    /// choosing the default stores nothing; a chosen solver reads back; an unknown spelling is the default again.</summary>
    [Fact]
    public void Defaults_RoundTrip_AndAnUnknownValueIsTheDefault()
    {
        Assert.True(EmSolverPreference.Layout.IsNone);
        Assert.Equal(Em3dSolverId.Palace, EmSolverPreference.ThreeD);

        EmSolverPreference.Layout = Em3dSolverChoice.One(Em3dSolverId.Emerge);
        EmSolverPreference.ThreeD = Em3dSolverId.OpenEms;
        Assert.Equal((Em3dSolverChoice.One(Em3dSolverId.Emerge), Em3dSolverId.OpenEms), (EmSolverPreference.Layout, EmSolverPreference.ThreeD));
        Assert.Equal(("emerge", "openems"), (EmSolverPreference.TestLayoutStore, EmSolverPreference.TestThreeDStore));

        EmSolverPreference.Layout = Em3dSolverChoice.None;
        EmSolverPreference.ThreeD = Em3dSolverId.Palace;
        Assert.Equal((null, null), (EmSolverPreference.TestLayoutStore, EmSolverPreference.TestThreeDStore));

        EmSolverPreference.TestLayoutStore = "thermal";
        Assert.True(EmSolverPreference.Layout.IsNone);
    }

    /// <summary>Thermal as the 3D preference is stored as such; an EM setup that must be made anyway (from a Planar .cem's
    /// component ports) still gets Palace; choosing an EM solver again replaces it.</summary>
    [Fact]
    public void Thermal_IsA3DPreference_AndAnEmSetupStillGetsPalace()
    {
        EmSolverPreference.ThreeDIsThermal = true;
        Assert.Equal(("thermal", true, Em3dSolverId.Palace),
                     (EmSolverPreference.TestThreeDStore, EmSolverPreference.ThreeDIsThermal, EmSolverPreference.ThreeD));
        EmSolverPreference.ThreeD = Em3dSolverId.Emerge;
        Assert.False(EmSolverPreference.ThreeDIsThermal);
    }
}
