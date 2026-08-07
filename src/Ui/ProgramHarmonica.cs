using System;
using System.IO;
using System.Linq;
using Avalonia;

namespace CircuitRF.Ui;

/// <summary>
/// The standalone harmonicaRF entry point (harmonicarf.md §3.1).
///
/// <para><b>Two <c>Main</c>s in one assembly, selected by an MSBuild property.</b> This project sets
/// <c>TreatWarningsAsErrors</c>, so a second entry point is CS0017 the moment it compiles unless
/// <c>&lt;StartupObject&gt;</c> names one. It is set EXPLICITLY for both configurations in the
/// <c>.csproj</c> — R-h8-5: relying on "there is only one <c>Main</c> today" is exactly what breaks
/// the moment the second one lands. Build the standalone with
/// <c>dotnet build -p:CrfApp=harmonica</c>.</para>
///
/// <para><b>Deliberately smaller than <see cref="Program"/>.</b> No Windows single-instance pipe:
/// that exists so a second <c>circuitRF.exe</c> hands its file to the first and exits, which is right
/// for a workspace application holding one project and wrong here — harmonicaRF opens one document
/// per window, so a second instance opening a second document is the behaviour, not a bug to
/// suppress. No <c>--generate-symbols</c> either; that is a circuitRF authoring tool.</para>
/// </summary>
internal sealed class ProgramHarmonica
{
    [STAThread]
    public static void Main(string[] args)
    {
        // argv is the double-click route on Windows and Linux. macOS delivers the file as an Apple
        // Event instead, which HarmonicaApp subscribes to; both land on the same shell method.
        HarmonicaApp.StartupFiles = args.Where(a => !a.StartsWith('-') && File.Exists(a)).ToArray();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<HarmonicaApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
