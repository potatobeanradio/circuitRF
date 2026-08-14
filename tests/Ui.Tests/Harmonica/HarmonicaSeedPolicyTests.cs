// ================================================================
//  HarmonicaSeedPolicyTests.cs — brief-harmonicarf-r4 §5.2/§5.3
//
//  Policy C: PinSearch.Sweep's `priorLevelSpectra` (R-h9r2-19's own "lever 1") is only read when the
//  termination has not moved past HarmonicaSolver.LeverOneDeltaGammaThreshold since the previous
//  frame — measured directly in tests/Harmonica.Tests/DragSeedPolicyTests.cs (lever 1 wins clearly up
//  to |ΔΓ| ≈ 0.15, ties through ~0.25, loses from ~0.30). This file is the gate for the SOLVER-level
//  wiring (does HarmonicaSolver.Solve actually gate it correctly across a sequence of frames), not the
//  policy comparison itself — a counter, not a stopwatch, per this repo's own convention.
// ================================================================

using System.Numerics;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using Xunit;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaSeedPolicyTests
{
    private static (HarmonicaSolver Solver, HarmonicaContext Ctx, HarmonicaViewModel Vm) Fixture()
    {
        var vm = new HarmonicaViewModel();
        vm.Terminations.Set(TerminationSide.Source, 1, new Complex(25, 0));
        vm.Terminations.Set(TerminationSide.Load,   1, new Complex(80, 10));
        var solver = new HarmonicaSolver();
        var ctx    = HarmonicaContext.Create(vm.Model);
        return (solver, ctx, vm);
    }

    private static HarmonicaSolver.Options DragOptions => new() { SkipContours = true };

    [Fact]
    public void SmallTerminationMoves_NeverDisableLever1()
    {
        var (solver, ctx, vm) = Fixture();

        // Frame 0 always disables it (no prior frame to compare against) — establish the baseline.
        _ = solver.Solve(ctx, vm.Terminations, [.. vm.Markers], DragOptions);
        int disabledAfterFirst = solver.Lever1DisabledCount;

        // A sequence of small moves, each well under HarmonicaSolver.LeverOneDeltaGammaThreshold.
        var loadZ = new Complex(80, 10);
        for (int i = 1; i <= 5; i++)
        {
            loadZ += new Complex(0.3 * i, 0.1); // a few tenths of an ohm — a sub-0.01-Γ move at 50 Ω
            vm.Terminations.Set(TerminationSide.Load, 1, loadZ);
            _ = solver.Solve(ctx, vm.Terminations, [.. vm.Markers], DragOptions);
        }

        Assert.Equal(disabledAfterFirst, solver.Lever1DisabledCount);
    }

    [Fact]
    public void ALargeTerminationJump_DisablesLever1ForThatFrameOnly()
    {
        var (solver, ctx, vm) = Fixture();
        _ = solver.Solve(ctx, vm.Terminations, [.. vm.Markers], DragOptions);
        int disabledAfterFirst = solver.Lever1DisabledCount;

        // A small move — must not disable it.
        vm.Terminations.Set(TerminationSide.Load, 1, new Complex(80.3, 10.1));
        _ = solver.Solve(ctx, vm.Terminations, [.. vm.Markers], DragOptions);
        Assert.Equal(disabledAfterFirst, solver.Lever1DisabledCount);

        // A large jump — well past HarmonicaSolver.LeverOneDeltaGammaThreshold in Γ.
        vm.Terminations.Set(TerminationSide.Load, 1, new Complex(10, -40));
        _ = solver.Solve(ctx, vm.Terminations, [.. vm.Markers], DragOptions);
        Assert.Equal(disabledAfterFirst + 1, solver.Lever1DisabledCount);

        // A small move again, relative to the NEW position — must not disable it again.
        vm.Terminations.Set(TerminationSide.Load, 1, new Complex(10.2, -39.9));
        _ = solver.Solve(ctx, vm.Terminations, [.. vm.Markers], DragOptions);
        Assert.Equal(disabledAfterFirst + 1, solver.Lever1DisabledCount);
    }

    [Fact]
    public void AFreshlyMarkedBand_DisablesLever1EvenIfOtherBandsHeldStill()
    {
        // §5.2's own note: a band with no prior-frame entry counts as an infinite jump, so a just-
        // added marker never reads a spectrum meant for a termination it was never at.
        var (solver, ctx, vm) = Fixture();
        _ = solver.Solve(ctx, vm.Terminations, [.. vm.Markers], DragOptions);
        int disabledAfterFirst = solver.Lever1DisabledCount;

        // Load band 1 holds perfectly still; band 2 is marked for the FIRST time this frame.
        vm.Terminations.Set(TerminationSide.Load, 2, new Complex(60, -5));
        _ = solver.Solve(ctx, vm.Terminations, [.. vm.Markers], DragOptions);

        Assert.Equal(disabledAfterFirst + 1, solver.Lever1DisabledCount);
    }
}
