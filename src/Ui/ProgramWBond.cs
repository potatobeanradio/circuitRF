using System;
using System.IO;
using System.Linq;
using Avalonia;

namespace CircuitRF.Ui;

/// <summary>
/// The standalone wBond entry point (wbond.md §11, WB38).
///
/// <para><b>Three <c>Main</c>s in one assembly, selected by an MSBuild property.</b> This project sets
/// <c>TreatWarningsAsErrors</c>, so a third entry point is CS0017 the moment it compiles unless
/// <b>every</b> <c>CrfApp</c> value names a <c>&lt;StartupObject&gt;</c> — WB39, and it bit on the
/// third entry point exactly as it bit on the second. Build the standalone with
/// <c>dotnet build -p:CrfApp=wbond</c>.</para>
///
/// <para><b>Deliberately as small as <see cref="ProgramHarmonica"/>, and for the same reasons.</b>
/// No Windows single-instance pipe: that exists so a second <c>circuitRF.exe</c> hands its file to
/// the first and exits, which is right for a workspace application holding one project and wrong
/// here — wBond opens one document per window (R-wbe-4), so a second instance opening a second
/// document is the behaviour rather than a bug to suppress. No <c>--generate-symbols</c> either;
/// that is a circuitRF authoring tool.</para>
/// </summary>
internal sealed class ProgramWBond
{
    [STAThread]
    public static void Main(string[] args)
    {
        Diagnostics.CrashReporter.Install("wBond");

        // argv is the double-click route on Windows and Linux. macOS delivers the file as an Apple
        // Event instead, which WBondApp subscribes to; both land on the same shell method.
        WBondApp.StartupFiles = args.Where(a => !a.StartsWith('-') && File.Exists(a)).ToArray();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<WBondApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
