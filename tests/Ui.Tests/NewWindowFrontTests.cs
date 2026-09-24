using System;
using CircuitRF.Ui.Views;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// A railRF window opened from a project-tree double-click came up BEHIND the workspace the first
/// time (field report, 2026-09-23). <see cref="NewWindowFront"/> undoes the opener's activation that
/// follows the gesture, and nothing the user does afterwards.
/// </summary>
public sealed class NewWindowFrontTests
{
    private static readonly DateTime Shown = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void OnlyTheFirstUntouchedActivationInsideTheGrace_IsUndone()
    {
        var hold = new NewWindowFront.Hold(Shown);
        Assert.True(hold.OpenerActivated(Shown.AddMilliseconds(300)));
        Assert.False(hold.OpenerActivated(Shown.AddMilliseconds(400)));   // once, never a tug of war

        Assert.False(new NewWindowFront.Hold(Shown).OpenerActivated(Shown + NewWindowFront.Grace + TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public void APressInEitherWindow_IsAChoice_AndIsNeverOverridden()
    {
        var hold = new NewWindowFront.Hold(Shown);
        hold.Pressed();
        Assert.False(hold.OpenerActivated(Shown.AddMilliseconds(100)));
    }
}
