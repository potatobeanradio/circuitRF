// The result's own header line per port (what it is, its net, what it returns through, calibrated or not) and its DC line, in the
// one form a Planar and a 3D result both write.

using CircuitRF.Design.Layout.Em;
using Xunit;

namespace CircuitRF.Ui.Tests.Em;

public sealed class EmPortHeaderTests
{
    [Fact]
    public void EachPort_IsOneAlignedLine_ThenTheDcRow()
    {
        var rows = new EmPortHeaderRow[]
        {
            new(5, "Q1.B", "b", "ground", false),
            new(1, "in", "rf_in", "ground", true),
            new(11, "L1.LB", null, "L1.LA", false),
        };
        Assert.Equal(
        [
            "Port 1 = P01  in     net=rf_in  ref=ground  calibrated",
            "Port 5 = P05  Q1.B   net=b      ref=ground  uncalibrated",
            "Port 11 = P11  L1.LB  net=?      ref=L1.LA  uncalibrated",
            "DC: none, the lowest row is 4 GHz; a circuit's DC solve through this file takes that row as DC",
        ], EmPortHeader.Lines(rows, [4e9, 10e9, 40e9]));
        Assert.Equal("DC: yes, the first row is 0 Hz", EmPortHeader.Lines(rows, [0, 1e9])[^1]);
        Assert.Empty(EmPortHeader.Lines([], [1e9]));
    }
}
