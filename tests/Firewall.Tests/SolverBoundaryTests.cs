using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Reflection.Metadata.Ecma335;
using System.Text.RegularExpressions;

namespace CircuitRF.Firewall.Tests;

/// <summary>
/// The GPL boundary, under test before any code that talks to a GPL program exists (brief-em3d-6
/// R-em3d6-6, em-3d.md §12).
///
/// <para><b>What it holds.</b> Gmsh and openEMS are GPL, CSXCAD is LGPL, and a default Palace build
/// carries ParMETIS, which is not open source for commercial use at all (§2). circuitRF is MIT and
/// talks to all of them in exactly one way: it writes a text file, starts the program as a separate
/// process, and reads the files it writes (overview §0's one rule). Each fact below is a way that rule
/// could be broken without anyone noticing — a package reference, a P/Invoke, a copied source file, a
/// shipped native library, or a call through a scripting interface — and each is checked the way
/// <see cref="UiFirewallTests"/> checks for Avalonia.</para>
///
/// <para><b>Every rule has a planted-violation twin</b> (R-em3d6-6b): the same scanner pointed at a
/// temporary file that breaks the rule, which must be reported. A scanner that finds nothing on the
/// real tree proves something only if it demonstrably finds the thing when it is there — the proof the
/// Avalonia firewall was given against a deliberate reference.</para>
/// </summary>
public sealed class SolverBoundaryTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "crf-gplwall-" + Guid.NewGuid().ToString("N")[..10]);

    public SolverBoundaryTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // ── 1. no project references one ─────────────────────────────────────────────────────────

    [Fact]
    public void NoProjectFileReferencesASolverOrMesherLibrary()
        => AssertNone(Boundary.ProjectFileViolations(ProjectFiles()), "project file");

    [Fact]
    public void Planted_APackageReferenceToAMesherIsCaught()
    {
        string csproj = Write("Planted.csproj",
            """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Gmsh.Interop" Version="4.15.2" /></ItemGroup></Project>""");
        Assert.NotEmpty(Boundary.ProjectFileViolations([csproj]));
    }

    // ── 2. no assembly references one, managed or native ─────────────────────────────────────

    [Fact]
    public void NoCircuitRfAssemblyReferencesOrImportsASolverLibrary()
        => AssertNone(Boundary.AssemblyViolations(CircuitRfAssemblies()), "assembly");

    /// <summary>A real assembly, emitted here, whose one method is a P/Invoke into <c>gmsh</c> — the
    /// ModuleReference the scanner reads is then genuinely in its metadata, not simulated.</summary>
    [Fact]
    public void Planted_AnAssemblyWithAPInvokeIntoGmshIsCaught()
    {
        string path = Path.Combine(_tmp, "Planted.dll");
        var builder = new PersistedAssemblyBuilder(new AssemblyName("Planted"), typeof(object).Assembly);
        var module  = builder.DefineDynamicModule("Planted");
        var type    = module.DefineType("Planted.Native", TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed);
        type.DefinePInvokeMethod("gmshInitialize", "gmsh",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.PinvokeImpl,
            CallingConventions.Standard, typeof(void), Type.EmptyTypes, CallingConvention.Cdecl, CharSet.Ansi);
        type.CreateType();
        builder.Save(path);

        Assert.NotEmpty(Boundary.AssemblyViolations([path]));
    }

    [Fact]
    public void NoNativeSolverLibraryShipsInAnyOutputFolder()
        => AssertNone(Boundary.NativeLibraryViolations(OutputFolders()), "output folder");

    [Fact]
    public void Planted_ANativeOpenEmsLibraryInAnOutputFolderIsCaught()
    {
        Write(Path.Combine("bin", "runtimes", "osx-arm64", "native", "libopenEMS.0.dylib"), "not really a library");
        Assert.NotEmpty(Boundary.NativeLibraryViolations([Path.Combine(_tmp, "bin")]));
    }

    // ── 3. no interop declaration names one ──────────────────────────────────────────────────

    [Fact]
    public void NoInteropDeclarationInSrcNamesASolver()
        => AssertNone(Boundary.InteropViolations(SourceFiles(Path.Combine(RepoRoot(), "src"), "*.cs")), "source file");

    [Theory]
    [InlineData("""[DllImport("libCSXCAD")] static extern int Load();""")]
    [InlineData("""[LibraryImport("GMSH")] internal static partial void Init();""")]
    [InlineData("""const string Lib = "Palace"; [DllImport(Lib)] static extern void Run();""")]
    [InlineData("""var h = NativeLibrary.Load("openems");""")]
    public void Planted_AnInteropDeclarationNamingASolverIsCaught(string code)
        => Assert.NotEmpty(Boundary.InteropViolations([Write("Planted.cs", code)]));

    // ── 4. no upstream source file is in the tree ────────────────────────────────────────────

    [Fact]
    public void NoFileUnderSrcOrToolsCarriesAnUpstreamHeader()
    {
        var signatures = Boundary.HeaderSignatures(Path.Combine(RepoRoot(), "testdata", "em3d", "upstream-headers"));
        Assert.True(signatures.Count >= 4, "the upstream-header fixtures are missing, so this scan would pass vacuously");
        var files = SourceFiles(Path.Combine(RepoRoot(), "src"), "*").Concat(SourceFiles(Path.Combine(RepoRoot(), "tools"), "*"));
        AssertNone(Boundary.HeaderViolations(files, signatures), "source file");
    }

    [Theory]
    [InlineData("gmsh.txt",    "// Gmsh - Copyright (C) 1997-2024 C. Geuzaine, J.-F. Remacle\n#include <cmath>")]
    [InlineData("openems.txt", "/*\n*\tCopyright (C) 2011,2012 Thorsten Liebig (Thorsten.Liebig@gmx.de)\n*/")]
    [InlineData("csxcad.txt",  "/*\n *  Copyright (C) 2008-2014 Thorsten Liebig (Thorsten.Liebig@gmx.de)\n */")]
    public void Planted_AFileCarryingAnUpstreamHeaderIsCaught(string fixture, string header)
    {
        // Years differ from the fixture's on purpose: every upstream file states its own.
        var signatures = Boundary.HeaderSignatures(Path.Combine(RepoRoot(), "testdata", "em3d", "upstream-headers"),
                                                   only: fixture);
        Assert.NotEmpty(Boundary.HeaderViolations([Write("planted.cpp", header)], signatures));
    }

    // ── 5. no scripting interface is used ────────────────────────────────────────────────────

    [Fact]
    public void NoScriptingInterfaceIsInvokedOrShippedFromSrc()
        => AssertNone(Boundary.ScriptingViolations(Path.Combine(RepoRoot(), "src")), "src");

    [Theory]
    [InlineData("planted.py", "import CSXCAD")]
    [InlineData("planted.m",  "CSX = InitCSX();")]
    [InlineData("Planted.cs", """var psi = new ProcessStartInfo("python3"); psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("from openEMS import openEMS");""")]
    [InlineData("Planted.cs", """using var p = Process.Start("octave", "--eval"); string pkg = "openEMS";""")]
    public void Planted_AScriptingInterfaceIsCaught(string file, string content)
    {
        string root = Path.Combine(_tmp, "src");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, file), content);
        Assert.NotEmpty(Boundary.ScriptingViolations(root));
    }

    // ── 6. the notices name none of them ─────────────────────────────────────────────────────

    /// <summary>R-em3d6-6c. circuitRF redistributes none of the three (§7.1), so there is nothing to give
    /// notice of — an entry appearing here is the first sign that something now ships.</summary>
    [Fact]
    public void ThirdPartyNoticesNamesNoSolver()
        => AssertNone(Boundary.NoticeViolations(File.ReadAllText(Path.Combine(RepoRoot(), "THIRD-PARTY-NOTICES.md"))),
                      "THIRD-PARTY-NOTICES.md");

    [Fact]
    public void Planted_ANoticeNamingOpenEmsIsCaught()
        => Assert.NotEmpty(Boundary.NoticeViolations("## openEMS\nGPL-3.0, (C) its authors."));

    // ── the tree ─────────────────────────────────────────────────────────────────────────────

    private static void AssertNone(IReadOnlyList<string> violations, string what)
        => Assert.True(violations.Count == 0,
            $"GPL boundary (em-3d.md §12) crossed in {violations.Count} {what}(s). circuitRF talks to Palace, " +
            "Gmsh and openEMS only by writing files and starting them as separate programs:\n  " +
            string.Join("\n  ", violations));

    private string Write(string relative, string content)
    {
        string path = Path.Combine(_tmp, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static IEnumerable<string> ProjectFiles()
    {
        string root = RepoRoot();
        foreach (string f in (string[])["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props"])
            if (File.Exists(Path.Combine(root, f))) yield return Path.Combine(root, f);
        foreach (string dir in (string[])["src", "tools"])
            foreach (string f in SourceFiles(Path.Combine(root, dir), "*.*proj"))
                yield return f;
    }

    /// <summary>Every circuitRF assembly this test can see: the ones copied beside it (every project but
    /// src/Ui, which this project may not reference), plus src/Ui's own from its build output when
    /// there is one.</summary>
    private static IEnumerable<string> CircuitRfAssemblies()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string f in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll"))
            if (Path.GetFileName(f).StartsWith("CircuitRF.", StringComparison.Ordinal) || Path.GetFileName(f) == "RfCore.dll")
                if (seen.Add(Path.GetFileName(f))) yield return f;

        string uiBin = Path.Combine(RepoRoot(), "src", "Ui", "bin");
        if (Directory.Exists(uiBin)
            && Directory.EnumerateFiles(uiBin, "CircuitRF.Ui.dll", SearchOption.AllDirectories).FirstOrDefault() is { } ui)
            yield return ui;
    }

    private static IEnumerable<string> OutputFolders()
    {
        yield return AppContext.BaseDirectory;
        foreach (string project in Directory.EnumerateDirectories(Path.Combine(RepoRoot(), "src")))
            if (Directory.Exists(Path.Combine(project, "bin"))) yield return Path.Combine(project, "bin");
        if (Directory.Exists(Path.Combine(RepoRoot(), "dist"))) yield return Path.Combine(RepoRoot(), "dist");
    }

    /// <summary>
    /// Files under <paramref name="root"/>, never inside build output, a virtual environment, a hidden
    /// directory or a symbolic link. The last two are not tidiness: a developer's machine keeps
    /// untracked state under <c>tools/</c> (an emulator prefix whose drive links reach the whole disk),
    /// and a scan that followed it would read — or be refused — files that are not the tree.
    /// </summary>
    private static IEnumerable<string> SourceFiles(string root, string pattern)
    {
        var skip = new[] { "bin", "obj", "venv", "node_modules", "TestResults" };
        var stack = new Stack<string>([root]);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            foreach (string sub in Directory.EnumerateDirectories(dir))
            {
                string name = Path.GetFileName(sub);
                if (name.StartsWith('.') || skip.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                if (new DirectoryInfo(sub).LinkTarget is not null) continue;
                stack.Push(sub);
            }
            foreach (string f in Directory.EnumerateFiles(dir, pattern))
                if (new FileInfo(f).LinkTarget is null) yield return f;
        }
    }

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, "Could not locate the repo root from " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}

/// <summary>
/// The scanners, each taking what to scan as an argument so its planted twin can point it elsewhere.
/// </summary>
internal static class Boundary
{
    /// <summary>The programs on the far side of the wall. Palace is on it for ParMETIS (§2), not for GPL.</summary>
    private static readonly Regex Named = new(@"gmsh|openems|csxcad|palace", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<string> ProjectFileViolations(IEnumerable<string> files)
    {
        var item = new Regex(@"<(\w+)\b[^>]*\b(?:Include|Update)\s*=\s*""([^""]*)""", RegexOptions.Compiled);
        var hint = new Regex(@"<HintPath>([^<]*)</HintPath>", RegexOptions.Compiled);
        var found = new List<string>();
        foreach (string f in files)
        {
            string text = File.ReadAllText(f);
            foreach (Match m in item.Matches(text))
                if (Named.IsMatch(m.Groups[2].Value)) found.Add($"{f}: <{m.Groups[1].Value} Include=\"{m.Groups[2].Value}\">");
            foreach (Match m in hint.Matches(text))
                if (Named.IsMatch(m.Groups[1].Value)) found.Add($"{f}: <HintPath>{m.Groups[1].Value}</HintPath>");
        }
        return found;
    }

    /// <summary>A managed assembly's references — other assemblies, and the native modules its P/Invokes
    /// name (<c>ModuleReferences</c>). A file that is not a managed assembly is not this rule's.</summary>
    public static IReadOnlyList<string> AssemblyViolations(IEnumerable<string> dlls)
    {
        var found = new List<string>();
        foreach (string dll in dlls)
        {
            using var stream = File.OpenRead(dll);
            using var pe     = new PEReader(stream);
            if (!pe.HasMetadata) continue;
            var md = pe.GetMetadataReader();
            foreach (var h in md.AssemblyReferences)
            {
                string name = md.GetString(md.GetAssemblyReference(h).Name);
                if (Named.IsMatch(name)) found.Add($"{dll}: references assembly {name}");
            }
            for (int row = 1; row <= md.GetTableRowCount(TableIndex.ModuleRef); row++)
            {
                string name = md.GetString(md.GetModuleReference(MetadataTokens.ModuleReferenceHandle(row)).Name);
                if (Named.IsMatch(name)) found.Add($"{dll}: imports native module {name}");
            }
        }
        return found;
    }

    public static IReadOnlyList<string> NativeLibraryViolations(IEnumerable<string> folders)
    {
        var found = new List<string>();
        foreach (string folder in folders)
            foreach (string f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(f);
                bool native = name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                           || name.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase)
                           || name.EndsWith(".so", StringComparison.OrdinalIgnoreCase)
                           || name.Contains(".so.", StringComparison.OrdinalIgnoreCase);
                if (native && Named.IsMatch(name)) found.Add(f);
            }
        return found;
    }

    public static IReadOnlyList<string> InteropViolations(IEnumerable<string> csFiles)
    {
        var literal = new Regex(@"(?:DllImport|LibraryImport)\s*\(\s*""([^""]*)""|NativeLibrary\.(?:Try)?Load\s*\(\s*""([^""]*)""");
        var byName  = new Regex(@"(?:DllImport|LibraryImport)\s*\(\s*(\w+)\s*[,)]");
        var found = new List<string>();
        foreach (string f in csFiles)
        {
            string text = File.ReadAllText(f);
            foreach (Match m in literal.Matches(text))
            {
                string lib = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (Named.IsMatch(lib)) found.Add($"{f}: {m.Value}");
            }
            foreach (Match m in byName.Matches(text))
            {
                // [DllImport(Lib)] with `const string Lib = "…"` in the same file.
                var constant = Regex.Match(text, $@"\bconst\s+string\s+{Regex.Escape(m.Groups[1].Value)}\s*=\s*""([^""]*)""");
                if (constant.Success && Named.IsMatch(constant.Groups[1].Value)) found.Add($"{f}: {m.Value} = \"{constant.Groups[1].Value}\"");
            }
        }
        return found;
    }

    /// <summary>
    /// The fixtures' lines as patterns. A comment marker is stripped, and a year run (<c>2008,2009,2010</c>,
    /// <c>1997-2025</c>) matches any year run, since each upstream file states its own years.
    /// </summary>
    public static IReadOnlyList<Regex> HeaderSignatures(string fixtureDirectory, string? only = null)
    {
        var years = new Regex(@"\d{4}(?:\s*[,\-–]\s*\d{4})*");
        var list = new List<Regex>();
        foreach (string f in Directory.EnumerateFiles(fixtureDirectory, "*.txt").Order(StringComparer.Ordinal))
        {
            if (only is not null && Path.GetFileName(f) != only) continue;
            foreach (string raw in File.ReadAllLines(f))
            {
                string line = raw.Trim().TrimStart('/', '*', '#').Trim();
                if (line.Length < 12) continue;
                var parts = years.Split(line).Select(Regex.Escape);
                string pattern = string.Join(@"[\d,\s\-–]+", parts).Replace(@"\ ", @"\s+").Replace(@"\t", @"\s+");
                list.Add(new Regex(pattern, RegexOptions.Compiled));
            }
        }
        return list;
    }

    public static IReadOnlyList<string> HeaderViolations(IEnumerable<string> files, IReadOnlyList<Regex> signatures)
    {
        var binary = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".png", ".jpg", ".ico", ".icns", ".ttf", ".otf", ".dll", ".exe", ".so", ".dylib", ".zip", ".gz",
              ".npy", ".pdf", ".gds", ".bin", ".woff", ".woff2" };
        var found = new List<string>();
        foreach (string f in files)
        {
            if (binary.Contains(Path.GetExtension(f))) continue;
            var info = new FileInfo(f);
            if (info.Length > 4_000_000) continue;
            string text = File.ReadAllText(f);
            foreach (var s in signatures)
                if (s.Match(text) is { Success: true } m) { found.Add($"{f}: {m.Value.Trim()}"); break; }
        }
        return found;
    }

    public static IReadOnlyList<string> ScriptingViolations(string srcRoot)
    {
        var found = new List<string>();
        var files = Directory.EnumerateFiles(srcRoot, "*", SearchOption.AllDirectories)
                             .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                                      && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                             .ToList();

        // §5.2 forbids the scripting interfaces, so no script in either language ships from src/.
        foreach (string f in files)
            if (Path.GetExtension(f) is ".py" or ".m") found.Add($"{f}: a {Path.GetExtension(f)} file in src/");

        var literal     = new Regex(@"""((?:[^""\\\n]|\\.)*)""");
        var start       = new Regex(@"ProcessStartInfo|Process\.Start|Em3dProcessLauncher\.Start");
        var interpreter = new Regex(@"^(?:.*[/\\])?(?:python\d*(?:\.\d+)?|octave(?:-cli)?)(?:\.exe)?$", RegexOptions.IgnoreCase);
        var importLine  = new Regex(@"\b(?:import|from)\s+(?:csxcad|openems)\b|-m\s+(?:csxcad|openems)\b", RegexOptions.IgnoreCase);
        var module      = new Regex(@"csxcad|openems", RegexOptions.IgnoreCase);
        foreach (string f in files.Where(f => f.EndsWith(".cs", StringComparison.Ordinal)))
        {
            var literals = literal.Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value).ToList();
            foreach (string l in literals.Where(l => importLine.IsMatch(l)))
                found.Add($"{f}: \"{l}\"");
            string text = File.ReadAllText(f);
            if (start.IsMatch(text) && literals.Any(l => interpreter.IsMatch(l.Trim())) && literals.Any(l => module.IsMatch(l)))
                found.Add($"{f}: starts an interpreter in a file that names CSXCAD or openEMS");
        }
        return found;
    }

    public static IReadOnlyList<string> NoticeViolations(string notices)
        => Regex.Matches(notices, @"\b(?:palace|gmsh|openems)\b", RegexOptions.IgnoreCase)
                .Select(m => $"names {m.Value}").ToList();
}
