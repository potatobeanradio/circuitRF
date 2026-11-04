using System;
using System.IO;
using CircuitRF.Core.Devices.External;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Like <see cref="FactAttribute"/>, but SKIPS (never fails) with a stated reason when no Verilog-A compiler or no built
/// device worker is reachable — what a test that compiles a <c>.va</c> and places it as a <c>VerilogA</c> component needs
/// (brief-modelcomposer-26 §4). Modelled on <see cref="FixtureFactAttribute"/>: the compiler is the user's own and the
/// worker is native, so neither is ever why somebody cannot run the suite.
/// </summary>
public sealed class CompilerFactAttribute : FactAttribute
{
    public CompilerFactAttribute()
    {
        if (CompilerAvailability.SkipReason is { } why) Skip = why;
    }
}

/// <summary>The <see cref="TheoryAttribute"/> form of <see cref="CompilerFactAttribute"/>.</summary>
public sealed class CompilerTheoryAttribute : TheoryAttribute
{
    public CompilerTheoryAttribute()
    {
        if (CompilerAvailability.SkipReason is { } why) Skip = why;
    }
}

internal static class CompilerAvailability
{
    public const string WorkerRel = "tools/osdi-worker";

    /// <summary>Probed once per test run: finding a compiler launches it.</summary>
    private static readonly Lazy<string?> Reason = new(() =>
    {
        if (WorkerDirectory() is null)
            return $"No built device worker under '{WorkerRel}' — run tools/osdi-worker/build.sh (needs a C compiler).";
        return VerilogACompilerDiscovery.Find(out var rejected) is null
            ? "No Verilog-A compiler is reachable — install one on PATH or name it in " +
              $"{VerilogACompilerDiscovery.EnvironmentVariable}. {VerilogACompilerDiscovery.DescribeFailure(rejected)}"
            : null;
    });

    public static string? SkipReason => Reason.Value;

    /// <summary>The folder holding the built worker, found walking up from the test assembly.</summary>
    public static string? WorkerDirectory()
    {
        string? dir = FixturePaths.Find(WorkerRel);
        if (dir is null) return null;
        bool built = File.Exists(Path.Combine(dir, "osdi-worker")) || File.Exists(Path.Combine(dir, "osdi-worker.exe"));
        return built ? dir : null;
    }
}

/// <summary>
/// <see cref="CompilerFactAttribute"/> that also needs zig, the linker a build for another platform links with (brief
/// vendorlib-18 §5.4).
/// </summary>
public sealed class CompilerZigFactAttribute : FactAttribute
{
    public CompilerZigFactAttribute()
    {
        if (CompilerAvailability.SkipReason is { } why) Skip = why;
        else if (ZigDiscovery.Find(out var rejected) is null) Skip = ZigDiscovery.DescribeFailure(rejected);
    }
}

/// <summary>The link driver's host in a test process: the CLI's apphost beside the test assembly, which hands a launcher
/// call to the driver exactly as the installed executable does (brief vendorlib-18 R-vl18-1b).</summary>
internal static class LinkDriverHost
{
    public static void Ensure()
        => VerilogALinkDriver.Host ??= VerilogALinkDriver.HostOf(null, AppContext.BaseDirectory, "CircuitRF.Cli")
                                       ?? throw new InvalidOperationException("No CircuitRF.Cli apphost beside the test assembly.");
}
