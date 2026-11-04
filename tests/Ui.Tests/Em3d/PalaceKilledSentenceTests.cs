using CircuitRF.Design.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// Designer feedback round 34 — a Palace run in the Linux subsystem was OOM-killed (exit 137, the wrapper's
// "Killed" line in the log) and the failure said only "Palace failed … (exit code 137)", quoting a GMRES residual.
public sealed class PalaceKilledSentenceTests
{
    [Fact]
    public void Exit137_IsReportedAsOutOfMemory_NamingWslConfig_InTheSubsystem()
    {
        string? s = PalaceRun.KilledSentence(137, ["palace: line 179:   980 Killed   $PALACE $CONFIG"], 32_000_000_000, wsl: true);
        Assert.NotNull(s);
        Assert.Contains("out of memory", s);
        Assert.Contains("32.0 GB", s);
        Assert.Contains(".wslconfig", s);
    }

    [Fact]
    public void AnOrdinaryFailure_IsNotCalledAKill()
        => Assert.Null(PalaceRun.KilledSentence(1, ["Error: mesh file not found"], null, wsl: false));
}
