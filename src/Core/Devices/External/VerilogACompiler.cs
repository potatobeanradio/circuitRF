using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace CircuitRF.Core.Devices.External;

/// <summary>A Verilog-A compiler circuitRF can run.</summary>
/// <param name="Command">The executable to run. Absolute, or a bare name PATH resolves.</param>
/// <param name="Identity">
/// What this compiler answered when asked what it is — its version banner, trimmed to one line.
///
/// <para><b>Part of the cache key, and that is what it is for.</b> The same source compiled by two
/// different compilers is two different artefacts, and an upgrade in place changes the answer
/// without changing a byte of the user's source. Keying on the source alone would hand back the old
/// compiler's output forever.</para>
/// </param>
/// <param name="HowFound">Where it came from, in words — "set in Settings", "found on PATH". Appears
/// in the note that records the decision, because "circuitRF picked a compiler" is not actionable
/// and "it took the one you named in Settings" is.</param>
public sealed record VerilogACompilerInfo(string Command, string Identity, string HowFound);

/// <summary>
/// Finds a Verilog-A compiler on the user's machine.
///
/// <para><b>circuitRF neither ships nor bundles one, and this does not change its licence
/// position.</b> The compiler is a separately-installed program the user chose, started as its own
/// process with its own arguments; nothing is linked, ingested or redistributed. It is the same
/// arm's-length arrangement as building circuitRF with a C compiler.</para>
///
/// <para><b>An explicitly named compiler outranks PATH.</b> The zero-configuration case is what
/// makes this worth having at all, so PATH is searched and a machine with a compiler on it needs
/// nothing set — but a user who has NAMED one has made a deliberate statement, usually because the
/// one on PATH is the wrong version or the wrong build. A preference that loses to PATH is inert on
/// exactly the machine that needed it, so the order is preference, then environment, then PATH.</para>
/// </summary>
public static class VerilogACompilerDiscovery
{
    /// <summary>
    /// The command names looked for on PATH, most preferred first.
    ///
    /// <para><b>This is the one place in circuitRF that names a compiler, and it is deliberately a
    /// list rather than a constant.</b> Everything user-facing says "a Verilog-A compiler" — no
    /// dialog, refusal or doc page names a product. The names are needed HERE and only here, because
    /// searching PATH requires something to search for, and a search for nothing would mean every
    /// user configures a path by hand before their first compile.</para>
    ///
    /// <para>Settable, so an unusual toolchain is reachable without a code change and so a test can
    /// point the whole mechanism at a stub. Setting it to an empty list disables PATH discovery
    /// entirely, which leaves the preference and the environment variable as the only routes.</para>
    /// </summary>
    public static IReadOnlyList<string> CandidateCommands { get; set; } = ["openvaf"];

    /// <summary>
    /// Names a compiler for one process, outranking PATH and beaten only by the user's own
    /// preference. This is how a headless run — CI, a test, a batch job — points at a toolchain
    /// without writing anyone's preferences file.
    /// </summary>
    public const string EnvironmentVariable = "CRF_VERILOGA_COMPILER";

    /// <summary>
    /// The compiler the user named in application settings, or null when they have named none.
    ///
    /// <para><b>Installed by the UI, because the preference lives there and this assembly may not
    /// reach across the firewall to read it</b> — the same seam, and for the same reason, as
    /// <c>LayoutTextOutline.TypefaceSource</c>. Unset, discovery simply falls through to the
    /// environment variable and PATH, which is what a headless process gets.</para>
    ///
    /// <para>A <c>Func</c> rather than a string so the answer is read at the moment it is needed: a
    /// user who changes the setting and compiles again must get the compiler they just named, not
    /// the one that was set when this assembly loaded.</para>
    /// </summary>
    public static Func<string?>? PreferredCommand { get; set; }

    /// <summary>How long to let a candidate identify itself before moving on. A probe that hangs — a
    /// stale symlink, a shim waiting on a network store — must not hold up a Run.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The compiler to use, or null with <paramref name="rejected"/> explaining every candidate that
    /// was tried and why it was not taken.
    /// </summary>
    public static VerilogACompilerInfo? Find(out IReadOnlyList<string> rejected)
    {
        var notes = new List<string>();
        rejected = notes;

        if (PreferredCommand?.Invoke() is { } preferred && preferred.Trim().Length > 0)
        {
            if (TryProbe(preferred.Trim(), "set in Settings", out var chosen, out string? why))
                return chosen;
            // Reported and NOT silently fallen back from: a user who named a compiler and got a
            // different one has been overruled without being told, and the artefact they get is not
            // the one they asked for.
            notes.Add($"the compiler set in Settings ('{preferred.Trim()}'): {why}");
            return null;
        }

        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { } fromEnv
            && fromEnv.Trim().Length > 0)
        {
            if (TryProbe(fromEnv.Trim(), $"named by {EnvironmentVariable}", out var chosen, out string? why))
                return chosen;
            notes.Add($"the compiler named by {EnvironmentVariable} ('{fromEnv.Trim()}'): {why}");
            return null;
        }

        foreach (string command in CandidateCommands)
        {
            if (string.IsNullOrWhiteSpace(command)) continue;
            if (TryProbe(command, "found on PATH", out var found, out string? why)) return found;
            notes.Add($"'{command}' on PATH: {why}");
        }

        return null;
    }

    /// <summary>
    /// Runs a candidate and asks it what it is.
    ///
    /// <para>Asked with <c>--version</c>, and a NON-ZERO exit is not fatal: compilers differ on
    /// whether a version query exits zero, and refusing one over that would reject a working
    /// toolchain. What is required is that the program STARTS and says something — a name that
    /// resolves to nothing cannot compile anything, and that is the case worth catching here rather
    /// than three seconds into a Run.</para>
    /// </summary>
    public static bool TryProbe(
        string command, string howFound, out VerilogACompilerInfo? compiler, out string? why)
    {
        compiler = null;
        why      = null;

        var info = new ProcessStartInfo(command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        info.ArgumentList.Add("--version");

        string banner;
        try
        {
            using var probe = Process.Start(info);
            if (probe is null) { why = "it could not be started"; return false; }

            string stdout = probe.StandardOutput.ReadToEnd();
            string stderr = probe.StandardError.ReadToEnd();
            if (!probe.WaitForExit((int)ProbeTimeout.TotalMilliseconds))
            {
                try { probe.Kill(entireProcessTree: true); } catch { /* already gone */ }
                why = "it did not answer";
                return false;
            }

            // Either stream: a version banner lands on stdout for some tools and stderr for others.
            banner = FirstLine(stdout.Trim().Length > 0 ? stdout : stderr);
        }
        catch (Exception ex)
        {
            why = ex.Message;
            return false;
        }

        if (banner.Length == 0)
        {
            why = "it started but identified itself with nothing";
            return false;
        }

        compiler = new VerilogACompilerInfo(command, banner, howFound);
        return true;
    }

    private static string FirstLine(string s)
    {
        string line = s.Split('\n', '\r').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        return line.Length <= 200 ? line : line[..200];
    }

    /// <summary>
    /// The sentence shown when nothing was found — what to install, and where to point circuitRF at
    /// it. Names no product, for the reason <see cref="CandidateCommands"/> records.
    /// </summary>
    public static string DescribeFailure(IReadOnlyList<string> rejected)
    {
        string tried = rejected.Count == 0 ? "" :
            " Tried: " + string.Join("; ", rejected) + ".";

        return "No Verilog-A compiler was found, so this source cannot be compiled. circuitRF runs a "
             + "compiler you install rather than building one in. Install one and either put it on "
             + "PATH or name it in Settings ▸ Security & Permissions ▸ Verilog-A Compiler. A "
             + "compiled '.osdi' can still be used directly, with no compiler involved." + tried;
    }
}

/// <summary>
/// Turns a Verilog-A source file into the compiled artefact circuitRF can actually load, by running
/// the user's own compiler and caching the result.
///
/// <para><b>The cache key is the SOURCE'S CONTENT, not its path or its timestamp</b> — a hash over
/// the source and every file it includes, plus the compiler's own identity. So a Run that changes
/// nothing compiles nothing: the second and every later simulation of an unedited model finds the
/// artefact already built and starts the worker on it. Editing the source — or an include beside it,
/// or upgrading the compiler — changes the key and costs exactly one recompile.</para>
///
/// <para><b>The includes are in the hash, and they have to be.</b> Compact models of this shape
/// routinely put their parameter sets and macros in files beside the source and pull them in with
/// <c>`include</c>. Hashing only the top file means editing a parameter file and silently getting
/// the previous build — a wrong answer that looks like a working one, which is worse than a
/// needless recompile in every respect.</para>
/// </summary>
public static class VerilogASourceCompiler
{
    /// <summary>The extensions circuitRF treats as Verilog-A source rather than a built artefact.</summary>
    private static readonly string[] SourceExtensions = [".va", ".vams"];

    /// <summary>True when this path names Verilog-A source, which has to be compiled before it can
    /// be loaded. Everything else — in practice a <c>.osdi</c> — is taken as already built.</summary>
    public static bool IsSourceFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string ext = Path.GetExtension(path.Trim());
        return SourceExtensions.Any(e => ext.Equals(e, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Where built artefacts are kept.
    ///
    /// <para><b>Deliberately NOT beside the source.</b> A model family is routinely installed as a
    /// read-only kit tree, and writing into it either fails or — worse, on a tree the user does own —
    /// litters someone else's delivery with build output. A per-user cache always works and belongs
    /// to the person who ran the compiler.</para>
    ///
    /// <para>Settable so a test, or a tool that redirects circuitRF's per-user state, can move it.
    /// The UI keeps it in step with <c>AppDataRoot</c>.</para>
    /// </summary>
    public static string CacheDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "circuitRF", "compiled-models");

    /// <summary>
    /// The compiled artefact for <paramref name="sourcePath"/>, compiling it first if the cache does
    /// not already hold a build of exactly this content by exactly this compiler.
    ///
    /// <para><paramref name="note"/> says what happened in one sentence — which compiler was used
    /// and where the artefact went, or that the cache answered. A user who recompiles and sees no
    /// change needs to know where the artefact went, and a user whose PATH holds a compiler they did
    /// not expect needs to know which one ran.</para>
    /// </summary>
    /// <exception cref="ExternalDeviceException">
    /// The source does not exist, no compiler could be found, or the compiler refused. A refusal
    /// carries the compiler's own diagnostics VERBATIM — a paraphrase of a compiler error is
    /// strictly worse than the error, because the line and column in it are the whole value.
    /// </exception>
    public static string Compile(string sourcePath, out string note)
    {
        string source = Path.GetFullPath(sourcePath.Trim());
        if (!File.Exists(source))
            throw new ExternalDeviceException(
                $"The Verilog-A source '{source}' does not exist.");

        var compiler = VerilogACompilerDiscovery.Find(out var rejected)
            ?? throw new ExternalDeviceException(
                VerilogACompilerDiscovery.DescribeFailure(rejected));

        var included = new List<string>();
        string key   = ContentKey(source, compiler.Identity, included);

        string outDir = CacheDirectory;
        string output = Path.Combine(
            outDir, Path.GetFileNameWithoutExtension(source) + "-" + key + ".osdi");

        if (File.Exists(output))
        {
            note = $"Using the compiled model already built from '{Path.GetFileName(source)}' "
                 + $"at '{output}'. The source has not changed since it was built.";
            return output;
        }

        try { Directory.CreateDirectory(outDir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ExternalDeviceException(
                $"The compiled-model cache directory '{outDir}' could not be created, so the "
              + $"Verilog-A source could not be compiled: {ex.Message}");
        }

        RunCompiler(compiler, source, output, included);

        note = $"Compiled '{Path.GetFileName(source)}' with the Verilog-A compiler {compiler.HowFound} "
             + $"('{compiler.Command}') to '{output}'. It will be reused until the source changes.";
        return output;
    }

    /// <summary>The overload for callers with nothing to say the note to.</summary>
    public static string Compile(string sourcePath) => Compile(sourcePath, out _);

    // ── The cache key ─────────────────────────────────────────────────────────

    /// <summary>
    /// A hash over the source, every include it reaches, and the compiler's identity — the whole of
    /// what determines the artefact.
    ///
    /// <para>Each file contributes its RELATIVE name as well as its bytes, so two includes that
    /// happen to hold identical text still key differently by which one is which.</para>
    /// </summary>
    internal static string ContentKey(string source, string compilerIdentity, List<string> included)
    {
        using var sha = SHA256.Create();
        var buffer = new MemoryStream();

        void Feed(string text)
        {
            byte[] b = Encoding.UTF8.GetBytes(text);
            buffer.Write(b, 0, b.Length);
        }

        Feed("compiler " + compilerIdentity + " ");

        foreach (string file in SourceAndIncludes(source, included))
        {
            Feed("file " + Path.GetFileName(file) + " ");
            try
            {
                byte[] bytes = File.ReadAllBytes(file);
                buffer.Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An include circuitRF cannot READ still contributes its name, so the key changes if
                // it later becomes readable. Compiling is the compiler's job to refuse, not this.
                Feed("unreadable ");
            }
            Feed(" end ");
        }

        buffer.Position = 0;
        return Convert.ToHexString(sha.ComputeHash(buffer))[..16].ToLowerInvariant();
    }

    /// <summary>
    /// The source and every file it pulls in with <c>`include</c>, transitively, in a stable order.
    ///
    /// <para><b>Resolved against the including file's own directory first</b>, which is the rule the
    /// compiler itself follows and the reason a model that compiles from its own folder can fail
    /// from anywhere else. An include that resolves to nothing locally is skipped without complaint:
    /// the discipline headers every model of this shape opens with are supplied by the compiler, not
    /// by the kit, and treating their absence as an error would refuse every real model.</para>
    /// </summary>
    internal static IReadOnlyList<string> SourceAndIncludes(string source, List<string> included)
    {
        var order   = new List<string>();
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>();

        pending.Enqueue(source);
        seen.Add(source);

        while (pending.Count > 0)
        {
            string file = pending.Dequeue();
            order.Add(file);
            if (!file.Equals(source, StringComparison.OrdinalIgnoreCase)) included.Add(file);

            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }   // unreadable: it still contributed its name above

            string dir = Path.GetDirectoryName(file) ?? ".";
            foreach (string target in IncludeTargets(text))
            {
                string resolved;
                try { resolved = Path.GetFullPath(Path.Combine(dir, target)); }
                catch (ArgumentException) { continue; }

                if (!File.Exists(resolved)) continue;   // the compiler's own headers live elsewhere
                if (seen.Add(resolved)) pending.Enqueue(resolved);
            }
        }

        // Bounded, because a hash is not worth an unbounded walk of a delivery someone points at.
        return order.Count <= MaxIncludedFiles ? order : order.GetRange(0, MaxIncludedFiles);
    }

    /// <summary>Enough for a compact model and its parameter and macro files, and a stop on a tree
    /// that includes half a delivery.</summary>
    private const int MaxIncludedFiles = 256;

    /// <summary>
    /// The quoted targets of <c>`include</c> directives, skipping any inside a comment.
    ///
    /// <para>A deliberately small scanner: it answers "which files does this reach", which only has
    /// to be right enough to hash and to pass an include path. It is NOT a preprocessor, and does
    /// not try to be — a conditional include it cannot evaluate is followed anyway, which over-hashes
    /// (a needless recompile) rather than under-hashing (a stale artefact). That asymmetry is the
    /// whole reason to err in this direction.</para>
    /// </summary>
    public static IEnumerable<string> IncludeTargets(string text)
    {
        foreach (string raw in StripComments(text).Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith('`')) continue;
            if (!line[1..].TrimStart().StartsWith("include", StringComparison.OrdinalIgnoreCase)) continue;

            int open = line.IndexOf('"');
            if (open < 0) continue;
            int close = line.IndexOf('"', open + 1);
            if (close <= open + 1) continue;

            yield return line[(open + 1)..close];
        }
    }

    /// <summary>
    /// <paramref name="text"/> with <c>//</c> and <c>/* */</c> comments blanked, newlines kept so
    /// line structure survives. Shared by the include scan and the parameter-set reader, which have
    /// exactly the same need and had no business each growing their own.
    /// </summary>
    internal static string StripComments(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool inLine = false, inBlock = false, inString = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c    = text[i];
            char next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (inLine)
            {
                if (c == '\n') { inLine = false; sb.Append(c); }
                continue;
            }
            if (inBlock)
            {
                if (c == '*' && next == '/') { inBlock = false; i++; }
                else if (c == '\n') sb.Append(c);
                continue;
            }
            if (inString)
            {
                sb.Append(c);
                if (c == '\\' && next != '\0') { sb.Append(next); i++; }
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '/' && next == '/') { inLine  = true; i++; continue; }
            if (c == '/' && next == '*') { inBlock = true; i++; continue; }
            if (c == '"') inString = true;
            sb.Append(c);
        }

        return sb.ToString();
    }

    // ── Running it ────────────────────────────────────────────────────────────

    /// <summary>How long a compile may take. A compact model of this size is seconds; the ceiling is
    /// here so a compiler that wedges is a refusal rather than a hung application.</summary>
    private static readonly TimeSpan CompileTimeout = TimeSpan.FromMinutes(5);

    private static void RunCompiler(
        VerilogACompilerInfo compiler, string source, string output, IReadOnlyList<string> included)
    {
        var info = new ProcessStartInfo(compiler.Command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            // The source's own folder, so a compiler that resolves anything relative to the working
            // directory agrees with the one that resolves it relative to the file.
            WorkingDirectory       = Path.GetDirectoryName(source) ?? ".",
        };

        info.ArgumentList.Add(source);
        info.ArgumentList.Add("-o");
        info.ArgumentList.Add(output);

        // THE SOURCE'S OWN DIRECTORY IS THE FIRST INCLUDE PATH. A model that compiles from its own
        // folder and fails from circuitRF is this, every time — the includes beside it resolve
        // against the file for the compiler and against nothing at all for a host that forgot to say
        // so. Every directory an include was actually found in is passed too, so a family that keeps
        // its parameter sets one level down works without the user describing the layout.
        foreach (string dir in IncludeDirectories(source, included))
        {
            info.ArgumentList.Add("-I");
            info.ArgumentList.Add(dir);
        }

        string stdout, stderr;
        int exitCode;
        try
        {
            using var proc = Process.Start(info)
                ?? throw new ExternalDeviceException(
                    $"The Verilog-A compiler '{compiler.Command}' could not be started.");

            // Read both streams before waiting: a compiler that fills a pipe buffer while circuitRF
            // waits on exit deadlocks, and a compact model produces plenty of output to do it with.
            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit((int)CompileTimeout.TotalMilliseconds))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
                throw new ExternalDeviceException(
                    $"The Verilog-A compiler did not finish within {CompileTimeout.TotalMinutes:0} "
                  + $"minutes compiling '{Path.GetFileName(source)}', so it was stopped.");
            }

            stdout   = outTask.GetAwaiter().GetResult();
            stderr   = errTask.GetAwaiter().GetResult();
            exitCode = proc.ExitCode;
        }
        catch (ExternalDeviceException) { throw; }
        catch (Exception ex)
        {
            throw new ExternalDeviceException(
                $"The Verilog-A compiler '{compiler.Command}' could not be run: {ex.Message}");
        }

        if (exitCode != 0 || !File.Exists(output))
        {
            // VERBATIM, and that is the point: the compiler named a file, a line and a column, and a
            // paraphrase of that is strictly worse than the thing itself. circuitRF says only which
            // source and which compiler, and then gets out of the way.
            string diagnostics = (stderr.Trim().Length > 0 ? stderr : stdout).TrimEnd();
            if (diagnostics.Length == 0)
                diagnostics = $"(the compiler exited with code {exitCode} and printed nothing)";

            // A partial artefact must not be left where the cache would find it and treat it as a
            // successful build of this source.
            try { if (File.Exists(output)) File.Delete(output); } catch { /* best effort */ }

            throw new ExternalDeviceException(
                $"The Verilog-A compiler refused '{source}':{Environment.NewLine}{diagnostics}");
        }
    }

    /// <summary>The source's own directory first, then every other directory an include was found
    /// in, each once and in a stable order.</summary>
    public static IReadOnlyList<string> IncludeDirectories(string source, IReadOnlyList<string> included)
    {
        var dirs = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            if (seen.Add(dir)) dirs.Add(dir);
        }

        Add(Path.GetDirectoryName(source));
        foreach (string inc in included) Add(Path.GetDirectoryName(inc));
        return dirs;
    }
}
