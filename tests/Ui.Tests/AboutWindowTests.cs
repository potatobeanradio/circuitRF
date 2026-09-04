using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The About dialog exists to be READ OFF and pasted into a bug report, and both halves of that were
/// missing: nothing in it could be selected, and it did not say which build was running.
///
/// <para>The second half is not cosmetic. Windows on arm64 runs an x64 build under translation and
/// macOS on Apple Silicon runs one under Rosetta, neither of them saying so anywhere the user can
/// see — so an owner asked, of their own installed copy, which architecture it was, and had no way
/// to find out. The application is the only thing that can answer.</para>
///
/// <para>A source-text scan, like every other dialog-content test here: these Window subclasses
/// cannot be constructed headlessly, and this suite calls no Avalonia runtime API.</para>
/// </summary>
public class AboutWindowTests
{
    private static string ReadRepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root.");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }

    private static string Xaml => ReadRepoFile("src/Ui/Views/Dialogs/AboutWindow.axaml");
    private static string Code => ReadRepoFile("src/Ui/Views/Dialogs/AboutWindow.axaml.cs");

    /// <summary>
    /// <b>Every line of it, not most of them.</b> A dialog where the description is selectable and
    /// the version is not is worse than one where nothing is, because the one line anybody wants to
    /// copy is the one that refuses — and it reads as a broken selection rather than as a control
    /// choice.
    /// </summary>
    [Fact]
    public void EveryLineOfTextIsSelectable()
    {
        // <TextBlock, but not <SelectableTextBlock, which contains it as a substring.
        var plain = Regex.Matches(Xaml, @"<TextBlock\b").Count;

        Assert.True(plain == 0,
            $"{plain} plain TextBlock(s) in the About dialog. Text here exists to be copied into a "
            + "bug report; use SelectableTextBlock.");
        Assert.Contains("<SelectableTextBlock", Xaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// The build is named, and named from <c>AppVersion</c> rather than written into the AXAML —
    /// the same single-source rule the version line already follows, and for the same reason: a
    /// literal here would be right on the machine that typed it and wrong everywhere else.
    /// </summary>
    [Fact]
    public void TheRunningBuildIsNamed_AndReadFromAppVersion()
    {
        Assert.Contains("AppVersion.Platform", Code, StringComparison.Ordinal);
        Assert.Contains("PlatformText", Xaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The PROCESS architecture, and the machine's only when they differ.</b> Naming the machine
    /// alone would answer the question backwards on exactly the machine that raised it — an arm64
    /// Windows box running the x64 installer would report "arm64" and send the user looking for a
    /// problem in the wrong build.
    /// </summary>
    [Fact]
    public void Platform_NamesTheOperatingSystemAndTheProcessArchitecture()
    {
        string text = CircuitRF.Ui.AppVersion.Platform;

        string os = OperatingSystem.IsWindows() ? "Windows"
                  : OperatingSystem.IsMacOS()   ? "macOS"
                  : OperatingSystem.IsLinux()   ? "Linux" : null!;
        if (os is not null) Assert.StartsWith(os, text, StringComparison.Ordinal);

        Assert.Contains(Token(RuntimeInformation.ProcessArchitecture), text, StringComparison.Ordinal);

        // Same run, same machine: a translated build says so, a native one says nothing extra.
        if (RuntimeInformation.ProcessArchitecture == RuntimeInformation.OSArchitecture)
            Assert.DoesNotContain(" on ", text, StringComparison.Ordinal);
        else
            Assert.Contains($" on {Token(RuntimeInformation.OSArchitecture)}", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The spelling is the release artifacts' own, so what the About box says and what the user
    /// would go and download read the same. <c>UpdateAssetNames.ArchToken</c> is the other half.
    /// </summary>
    private static string Token(Architecture arch) => arch switch
    {
        Architecture.X64   => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86   => "x86",
        _ => arch.ToString().ToLowerInvariant(),
    };
}
