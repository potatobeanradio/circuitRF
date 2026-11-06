using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.Viewer3D;

/// <summary>When a 3D view presents through the compositor, draws in software, or refuses.</summary>
public class SoftwarePresentChoiceTests
{
    [Theory]
    // Working interop never reads a frame back, whatever else is true.
    [InlineData(false, true,  false, true,  Viewer3DPane.PresentChoice.Interop)]
    // No usable interop: software where the backend can and the setting allows.
    [InlineData(true,  true,  false, true,  Viewer3DPane.PresentChoice.Software)]
    [InlineData(true,  true,  false, false, Viewer3DPane.PresentChoice.Refuse)]
    [InlineData(true,  false, false, true,  Viewer3DPane.PresentChoice.Refuse)]
    // The compositor on another GPU keeps the refusal, whose Reconnect gets the GPU back.
    [InlineData(true,  true,  true,  true,  Viewer3DPane.PresentChoice.Refuse)]
    public void ChoosePresent(bool refused, bool canSoftware, bool moved, bool allowed, Viewer3DPane.PresentChoice expected) =>
        Assert.Equal(expected, Viewer3DPane.ChoosePresent(refused, canSoftware, moved, allowed));
}
