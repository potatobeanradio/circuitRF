// Brief vendorlib-18: every platform's build from any one machine. The Verilog-A compiler links by running a program named
// `clang` (macOS host), `ld` (Linux host) or `link.exe` (any Windows target) from PATH, and none of those links for another
// operating system. So a cross build runs the compiler with a per-run folder first on its PATH, holding a launcher under
// each name it can ask for; every launcher is circuitRF's own executable, which sees CRF_LINK_RID and runs Main below
// instead of anything else, translating the compiler's link line into zig's. The native build never comes here (D2).

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CircuitRF.Core.Devices.External;

/// <summary>
/// Finds zig, the linker a cross-platform build links with (brief vendorlib-18 D3): a path in Settings, then
/// <see cref="EnvironmentVariable"/>, then PATH and the directories the compiler is looked for in. circuitRF ships none,
/// and a missing one is a sentence.
/// </summary>
public static class ZigDiscovery
{
    /// <summary>Names zig for one process, outranking PATH and beaten only by the user's own preference.</summary>
    public const string EnvironmentVariable = "CRF_ZIG";

    /// <summary>The zig the user named in Settings, or null; installed by the UI, read at the moment it is needed — the
    /// compiler's <see cref="VerilogACompilerDiscovery.PreferredCommand"/> arrangement.</summary>
    public static Func<string?>? PreferredCommand { get; set; }

    /// <summary>The command looked for on PATH. Settable so a test can point the search at nothing.</summary>
    public static IReadOnlyList<string> CandidateCommands { get; set; } = ["zig"];

    /// <summary>zig, or null with <paramref name="rejected"/> saying what was tried.</summary>
    public static VerilogACompilerInfo? Find(out IReadOnlyList<string> rejected)
    {
        var notes = new List<string>();
        rejected = notes;
        if (PreferredCommand?.Invoke() is { } preferred && preferred.Trim().Length > 0)
        {
            if (Probe(preferred.Trim(), "set in Settings", out var chosen, out string? why)) return chosen;
            notes.Add($"the zig set in Settings ('{preferred.Trim()}'): {why}");
            return null;
        }
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { } fromEnv && fromEnv.Trim().Length > 0)
        {
            if (Probe(fromEnv.Trim(), $"named by {EnvironmentVariable}", out var chosen, out string? why)) return chosen;
            notes.Add($"the zig named by {EnvironmentVariable} ('{fromEnv.Trim()}'): {why}");
            return null;
        }
        foreach (string command in CandidateCommands)
        {
            if (string.IsNullOrWhiteSpace(command)) continue;
            if (Probe(command, "found on PATH", out var found, out string? why)) return found;
            notes.Add($"'{command}' on PATH: {why}");
            if (Path.IsPathRooted(command)) continue;
            // A bundled macOS application does not inherit the login shell's PATH; the compiler's search has the note.
            foreach (string dir in VerilogACompilerDiscovery.SearchDirectories)
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string path = Path.Combine(dir, command + (OperatingSystem.IsWindows() ? ".exe" : ""));
                if (!File.Exists(path)) continue;
                if (Probe(path, $"found at {path}", out found, out why)) return found;
                notes.Add($"'{path}': {why}");
            }
        }
        return null;
    }

    private static bool Probe(string command, string howFound, out VerilogACompilerInfo? found, out string? why)
        => VerilogACompilerDiscovery.TryProbe(command, howFound, "version", out found, out why);

    /// <summary>What the Settings row's Test says when nothing was found.</summary>
    public static string DescribeFailure(IReadOnlyList<string> rejected)
        => "zig was not found. circuitRF links a build for another platform with zig, which it does not ship; install it and "
         + "put it on PATH or name it here. The build for this machine needs no linker of circuitRF's."
         + (rejected.Count == 0 ? "" : " Tried: " + string.Join("; ", rejected) + ".");
}

/// <summary>
/// circuitRF's link driver (brief vendorlib-18 R-vl18-1): the launchers a cross build puts on the compiler's PATH, and
/// the translation from the compiler's link line to zig's.
/// </summary>
public static class VerilogALinkDriver
{
    /// <summary>Set on the compiler's environment: the platform being linked. A process that sees it is a launcher.</summary>
    public const string RidVariable = "CRF_LINK_RID";
    /// <summary>zig's path, or empty when none was found — discovery runs in the process that knows the user's
    /// Settings, not in the launcher.</summary>
    public const string ZigVariable = "CRF_LINK_ZIG";
    /// <summary>The PATH the compiler had before the launcher folder was put first, for whatever the driver runs.</summary>
    public const string PathVariable = "CRF_LINK_PATH";
    /// <summary>The per-run folder, which the driver links in.</summary>
    public const string DirVariable = "CRF_LINK_DIR";

    /// <summary>Every name the compiler asks for a linker by, on any host (R-vl18-1a).</summary>
    public static readonly IReadOnlyList<string> LauncherNames = ["clang", "cc", "ld", "gcc", "link"];

    /// <summary>The glibc a Linux build is linked against: old enough for the distributions the Linux packages target
    /// (linked and loaded at 2.28, brief vendorlib-18 §0).</summary>
    public const string Glibc = "2.28";

    /// <summary>The program <see cref="LinkPlan.Program"/> names when the link is zig's.</summary>
    public const string Zig = "zig";

    /// <summary>
    /// The executable every launcher is a link to: circuitRF itself, whose entry point hands a launcher call to
    /// <see cref="Main"/> before anything else. Set by each entry point (<see cref="HostOf"/>); null in a process that
    /// cannot be a launcher, where a cross build is refused with a sentence.
    /// </summary>
    public static string? Host { get; set; }

    /// <summary>The executable to set <see cref="Host"/> to: the process's own, or — when that is the <c>dotnet</c>
    /// muxer running an assembly — the apphost beside the assembly, when there is one.</summary>
    public static string? HostOf(string? processPath, string baseDirectory, string assemblyName)
    {
        if (processPath is { Length: > 0 } && !Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return processPath;
        string apphost = Path.Combine(baseDirectory, assemblyName + (OperatingSystem.IsWindows() ? ".exe" : ""));
        return File.Exists(apphost) ? apphost : null;
    }

    /// <summary>True when this process was started as a launcher.</summary>
    public static bool IsInvoked => Environment.GetEnvironmentVariable(RidVariable) is { Length: > 0 };

    // ── The sentences (R-vl18-1e) ─────────────────────────────────────────────────────────────────

    private const string ZigMissingTail = "needs zig to link it, and zig was not found.";
    private const string RefusedHead = "could not link the ";

    /// <summary>The driver's first line when zig is missing.</summary>
    public static string ZigMissingLine(string rid) => $"The {rid} build {ZigMissingTail}";

    /// <summary>The driver's first line when the linker it ran refused; the linker's own output follows it.</summary>
    public static string RefusedLine(string program, string rid) => $"{program} {RefusedHead}{rid} build:";

    /// <summary>True when <paramref name="message"/> carries the driver's missing-zig line.</summary>
    public static bool SaysZigMissing(string message) => message.Contains(ZigMissingTail, StringComparison.Ordinal);

    /// <summary>The driver's own first line in a compiler failure, or null when the driver said nothing.</summary>
    public static string? DriverLine(string message)
        => Lines(message).FirstOrDefault(l => l.EndsWith(ZigMissingTail, StringComparison.Ordinal)
                                           || l.StartsWith(Zig + " " + RefusedHead, StringComparison.Ordinal)
                                           || l.StartsWith("cc " + RefusedHead, StringComparison.Ordinal)
                                           || l.StartsWith(DriverFailedHead, StringComparison.Ordinal));

    /// <summary>zig's own first line after the driver's refusal line, verbatim; null when zig did not refuse.</summary>
    public static string? ZigRefusal(string message)
    {
        var lines = Lines(message).ToList();
        int at = lines.FindIndex(l => l.StartsWith(Zig + " " + RefusedHead, StringComparison.Ordinal));
        return at >= 0 && at + 1 < lines.Count ? lines[at + 1] : null;
    }

    private const string DriverFailedHead = "circuitRF's link driver could not link the ";

    private static IEnumerable<string> Lines(string message)
        => System.Text.RegularExpressions.Regex.Replace(message, @"\x1B\[[0-9;]*m", "")
                 .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    // ── The translation (R-vl18-1d) ───────────────────────────────────────────────────────────────

    /// <summary>
    /// One link, planned: run <paramref name="Program"/> (<see cref="Zig"/> or the system <c>cc</c>) with
    /// <paramref name="Arguments"/> after making each copy and writing each file; it writes <paramref name="Output"/> in
    /// the work folder, which is then moved to <paramref name="Destination"/>, where the compiler asked for it. Linking in
    /// the work folder keeps whatever else the linker writes beside its output (a Windows link's <c>.pdb</c> and import
    /// library) out of the library.
    /// </summary>
    public sealed record LinkPlan(string Program, IReadOnlyList<string> Arguments, IReadOnlyList<(string From, string To)> Copies,
                                  IReadOnlyList<(string Path, string Text)> Writes, string Output, string Destination);

    /// <summary>
    /// The compiler's link line for <paramref name="rid"/>, as zig's (or, for a Mac target on a Mac, the system linker's
    /// with the architecture named). Pure: nothing is run, copied or written.
    /// </summary>
    public static LinkPlan LinkCommand(string rid, IReadOnlyList<string> compilerArgs, string workDir, bool onMac)
    {
        int dash = rid.IndexOf('-');
        if (dash <= 0) throw new ArgumentException($"'{rid}' is not a platform circuitRF builds for.", nameof(rid));
        string os = rid[..dash], cpu = rid[(dash + 1)..];
        string arch = cpu switch
        {
            "x64" => "x86_64",
            "arm64" => "aarch64",
            _ => throw new ArgumentException($"The compiler has no target for {rid}.", nameof(rid)),
        };
        if (os is not ("linux" or "win" or "osx")) throw new ArgumentException($"'{rid}' is not a platform circuitRF builds for.", nameof(rid));

        string? destination = null;
        var inputs = new List<string>();
        var options = new List<string>();
        for (int k = 0; k < compilerArgs.Count; k++)
        {
            string a = compilerArgs[k];
            if (a == "-o" && k + 1 < compilerArgs.Count) { destination = compilerArgs[++k]; continue; }
            if (a.StartsWith("/OUT:", StringComparison.OrdinalIgnoreCase)) { destination = a[5..]; continue; }
            if (a == "-m" && k + 1 < compilerArgs.Count) { k++; continue; }   // GNU ld's emulation
            if (a is "--no-add-needed" || a.StartsWith("--hash-style", StringComparison.Ordinal)) continue;
            if (a.Equals("/NOLOGO", StringComparison.OrdinalIgnoreCase) || a.Equals("/DLL", StringComparison.OrdinalIgnoreCase)) continue;
            if (a.Equals("msvcrt.lib", StringComparison.OrdinalIgnoreCase)) continue;   // zig supplies the Windows runtime
            if (a is "-shared" or "-dynamiclib") continue;                               // said once, below
            if (a.StartsWith('-')) options.Add(a); else inputs.Add(a);
        }
        if (destination is null) throw new ArgumentException("The compiler's link line names no output.", nameof(compilerArgs));
        string output = Path.Combine(workDir, "link" + Path.GetExtension(destination));

        if (os == "osx" && onMac)
        {
            // The compiler's Mach-O link names no architecture, and without one the host linker writes the host's.
            var args = new List<string> { "-arch", cpu == "x64" ? "x86_64" : "arm64" };
            args.AddRange(options);
            args.AddRange(inputs);
            args.AddRange(["-o", output, "-dynamiclib"]);
            return new LinkPlan("cc", args, [], [], output, destination);
        }

        // zig takes an object only by its extension, and the compiler names its objects .o1 … .oN.
        var copies = new List<(string, string)>();
        var linked = new List<string>();
        foreach (string input in inputs)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(Path.GetExtension(input), @"^\.o\d+$"))
            {
                string renamed = Path.Combine(workDir, Path.GetFileName(input) + ".o");
                copies.Add((input, renamed));
                linked.Add(renamed);
            }
            else linked.Add(input);
        }

        string target = os switch
        {
            "linux" => $"{arch}-linux-gnu.{Glibc}",
            "win" => $"{arch}-windows-gnu",
            _ => $"{arch}-macos",
        };
        var zig = new List<string> { "cc", "-target", target, "-shared", "-o", output };
        zig.AddRange(linked);
        zig.AddRange(options);
        var writes = new List<(string, string)>();
        if (os == "linux") zig.AddRange(["-lc", "-lm"]);
        if (os == "win" && cpu == "x64")
        {
            // The compiled code references _fltused, which the Windows C runtime defines and zig's does not.
            string fltused = Path.Combine(workDir, "fltused.c");
            writes.Add((fltused, "int _fltused = 0;\n"));
            zig.Add(fltused);
        }
        return new LinkPlan(Zig, zig, copies, writes, output, destination);
    }

    // ── The launcher folder (R-vl18-1a, -1b) ──────────────────────────────────────────────────────

    /// <summary>
    /// Makes <paramref name="dir"/> a launcher folder: each of <see cref="LauncherNames"/> a link to <see cref="Host"/>
    /// (with <c>.exe</c> on Windows; <c>link.exe</c> by that name everywhere, since the compiler asks for it so).
    /// A symbolic link on macOS and Linux, where the apphost follows it to its own folder; a hard link on Windows, where
    /// the shipped executable is a single file, and one copy when even that cannot be made (another volume).
    /// </summary>
    public static void PrepareLaunchers(string dir, string host)
    {
        Directory.CreateDirectory(dir);
        bool windows = OperatingSystem.IsWindows();
        string? first = null;
        foreach (string name in LauncherNames)
        {
            string file = Path.Combine(dir, name == "link" || windows ? name + ".exe" : name);
            if (!windows) { File.CreateSymbolicLink(file, host); continue; }
            if (first is null)
            {
                if (!CreateHardLink(file, host, IntPtr.Zero)) File.Copy(host, file);
                first = file;
            }
            else if (!CreateHardLink(file, first, IntPtr.Zero)) File.Copy(first, file);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string newFile, string existingFile, IntPtr securityAttributes);

    /// <summary>The environment a cross build's compiler runs with: the launcher folder first on PATH, and what the
    /// driver needs to know.</summary>
    public static void Configure(ProcessStartInfo info, string dir, string rid, string? zig)
    {
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        info.Environment[PathVariable] = path;
        info.Environment["PATH"] = dir + Path.PathSeparator + path;
        info.Environment[RidVariable] = rid;
        info.Environment[ZigVariable] = zig ?? "";
        info.Environment[DirVariable] = dir;
    }

    // ── The launcher's entry point ────────────────────────────────────────────────────────────────

    /// <summary>
    /// What a launcher runs: the compiler's link line in <paramref name="args"/>, the platform in
    /// <see cref="RidVariable"/>. Everything goes to stderr, which the compiler prints when the link fails, so a refusal
    /// reads first as the driver's sentence and then as the linker's own text.
    /// </summary>
    public static int Main(string[] args)
    {
        string rid = Environment.GetEnvironmentVariable(RidVariable) ?? "";
        try
        {
            string dir = Environment.GetEnvironmentVariable(DirVariable) is { Length: > 0 } d ? d : Path.GetTempPath();
            string work = Path.Combine(dir, "link-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(work);
            var plan = LinkCommand(rid, args, work, OperatingSystem.IsMacOS());

            string program = plan.Program;
            if (program == Zig)
            {
                string zig = Environment.GetEnvironmentVariable(ZigVariable) ?? "";
                if (zig.Length == 0) { Console.Error.WriteLine(ZigMissingLine(rid)); return 1; }
                program = zig;
            }
            foreach (var (from, to) in plan.Copies) File.Copy(from, to, overwrite: true);
            foreach (var (path, text) in plan.Writes) File.WriteAllText(path, text, new UTF8Encoding(false));

            var info = new ProcessStartInfo(program)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (string a in plan.Arguments) info.ArgumentList.Add(a);
            // What the driver runs must not find the launchers again, nor think it is one.
            info.Environment["PATH"] = Environment.GetEnvironmentVariable(PathVariable) ?? info.Environment["PATH"];
            foreach (string v in new[] { RidVariable, ZigVariable, PathVariable, DirVariable }) info.Environment.Remove(v);

            using var proc = Process.Start(info) ?? throw new InvalidOperationException($"'{program}' could not be started.");
            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();
            proc.WaitForExit();
            string said = (errTask.GetAwaiter().GetResult() + outTask.GetAwaiter().GetResult()).Trim();
            if (proc.ExitCode != 0 || !File.Exists(plan.Output))
            {
                Console.Error.WriteLine(RefusedLine(plan.Program, rid));
                Console.Error.WriteLine(said.Length > 0 ? said : $"(it exited with code {proc.ExitCode} and printed nothing)");
                return 1;
            }
            File.Move(plan.Output, plan.Destination, overwrite: true);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{DriverFailedHead}{rid} build: {ex.Message}");
            return 1;
        }
    }
}
