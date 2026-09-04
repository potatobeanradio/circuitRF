using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using CircuitRF.Core.Devices.External;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.External;

/// <summary>
/// PM2 P1's gate: circuitRF accepts Verilog-A source, compiles it with the user's own compiler,
/// caches the artefact on the source's own content, and refuses cleanly when there is no compiler.
///
/// <para><b>Driven against a STUB compiler, and every one of these is testable without a third-party
/// one.</b> The stub is a script this test writes: it reads its input, writes an artefact derived
/// from it, and reports its own identity — which is the whole of the contract circuitRF depends on.
/// Requiring a real Verilog-A compiler would make the gate skip on every machine that matters and
/// would be measuring that compiler rather than circuitRF.</para>
///
/// <para><b>The fixture <c>.va</c> is circuitRF's own and is MIT</b>, like the rest of this
/// repository. No model family, no vendor source, and no compiled artefact is committed.</para>
/// </summary>
public sealed class VerilogACompileTests : IDisposable
{
    /// <summary>circuitRF's own test-only artefact, built by the worker's build script. Its absence
    /// is an ordinary state of a fresh clone — the worker is native, and the standing rule is that a
    /// missing C compiler warns and the build still succeeds.</summary>
    private const string FakeModelRel = "tools/fake-osdi-model/fake_osdi.osdi";
    private const string WorkerRel    = "tools/osdi-worker/osdi-worker";
    private const string BuildHowTo   = "run tools/osdi-worker/build.sh (needs a C compiler)";

    private readonly string _dir;
    private readonly IReadOnlyList<string> _savedCandidates;
    private readonly Func<string?>? _savedPreference;
    private readonly string _savedCache;
    private readonly string? _savedEnv;
    private readonly string _savedTools;

    public VerilogACompileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "crf-va-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);

        _savedCandidates = VerilogACompilerDiscovery.CandidateCommands;
        _savedPreference = VerilogACompilerDiscovery.PreferredCommand;
        _savedCache      = VerilogASourceCompiler.CacheDirectory;
        _savedEnv        = Environment.GetEnvironmentVariable(
                               VerilogACompilerDiscovery.EnvironmentVariable);
        _savedTools      = DeviceWorkerManifest.ToolsDirectory;

        // No PATH discovery and no ambient environment: every test here says explicitly which
        // compiler it wants, so a machine that happens to have a real one installed runs the same
        // test as one that does not.
        VerilogACompilerDiscovery.CandidateCommands = [];
        Environment.SetEnvironmentVariable(VerilogACompilerDiscovery.EnvironmentVariable, null);
        VerilogASourceCompiler.CacheDirectory = Path.Combine(_dir, "cache");
    }

    public void Dispose()
    {
        VerilogACompilerDiscovery.CandidateCommands = _savedCandidates;
        VerilogACompilerDiscovery.PreferredCommand = _savedPreference;
        VerilogASourceCompiler.CacheDirectory      = _savedCache;
        Environment.SetEnvironmentVariable(
            VerilogACompilerDiscovery.EnvironmentVariable, _savedEnv);
        DeviceWorkerManifest.ToolsDirectory = _savedTools;

        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ── The fixture source: circuitRF's own, MIT, and deliberately trivial ────

    private const string TrivialSource = """
        // circuitRF's own fixture. MIT, like the rest of this repository.
        `include "disciplines.vams"

        module crf_fixture_res(p, n);
            inout p, n;
            electrical p, n;
            parameter real r = 1.0 from (0:inf);
            analog I(p, n) <+ V(p, n) / r;
        endmodule
        """;

    private string WriteSource(string name = "crf_fixture.va", string? text = null)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, text ?? TrivialSource);
        return path;
    }

    // ── The stub compiler ────────────────────────────────────────────────────

    /// <summary>
    /// Writes a script that behaves like a compiler for the two things circuitRF asks of one:
    /// <c>--version</c> identifies it, and <c>&lt;source&gt; -o &lt;out&gt;</c> produces the file.
    /// <paramref name="identity"/> lets a test stand up a SECOND compiler and prove the artefact is
    /// keyed on which one ran.
    /// </summary>
    private string WriteStubCompiler(
        string identity = "stub-compiler 1.0", bool failing = false, bool refuseToCompile = false)
    {
        bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        string path  = Path.Combine(_dir, "stubc-" + Guid.NewGuid().ToString("N")[..6]
                                        + (windows ? ".cmd" : ".sh"));

        // The stub COPIES ITS INPUT into the artefact. That is what makes the cache tests real: the
        // output differs when the source differs, so a stale cache hit is visible in the bytes and
        // not only in a timestamp.
        string script = windows
            ? $"""
               @echo off
               if "%~1"=="--version" (
                 echo {identity}
                 exit /b 0
               )
               {(failing ? "echo crf_fixture.va:7:12: syntax error, unexpected 'endmodule' 1>&2\r\nexit /b 1"
                 : refuseToCompile ? "echo THIS STUB MUST NOT BE ASKED TO COMPILE 1>&2\r\nexit /b 3"
                 : "copy /y %~1 %~3 >nul\r\nexit /b 0")}
               """
            : $"""
               #!/bin/sh
               if [ "$1" = "--version" ]; then
                 echo "{identity}"
                 exit 0
               fi
               {(failing
                   ? "echo \"crf_fixture.va:7:12: syntax error, unexpected 'endmodule'\" >&2\nexit 1"
                   : refuseToCompile
                   ? "echo \"THIS STUB MUST NOT BE ASKED TO COMPILE\" >&2\nexit 3"
                   : "cp \"$1\" \"$3\"\nexit 0")}
               """;

        File.WriteAllText(path, script);
        if (!windows) File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private void UseCompiler(string path)
        => VerilogACompilerDiscovery.PreferredCommand = () => path;

    // ── The gate ─────────────────────────────────────────────────────────────

    [Fact]
    public void SourceFileIsRecognisedByExtension()
    {
        Assert.True(VerilogASourceCompiler.IsSourceFile("m.va"));
        Assert.True(VerilogASourceCompiler.IsSourceFile("m.vams"));
        Assert.True(VerilogASourceCompiler.IsSourceFile("M.VA"));       // case is not a distinction

        // A compiled artefact is already built and must NOT be sent to a compiler.
        Assert.False(VerilogASourceCompiler.IsSourceFile("m.osdi"));
        Assert.False(VerilogASourceCompiler.IsSourceFile(""));
        Assert.False(VerilogASourceCompiler.IsSourceFile(null));
    }

    [Fact]
    public void ATrivialSourceCompiles()
    {
        UseCompiler(WriteStubCompiler());
        string source = WriteSource();

        string artefact = VerilogASourceCompiler.Compile(source, out string note);

        Assert.True(File.Exists(artefact));
        Assert.EndsWith(".osdi", artefact, StringComparison.Ordinal);
        Assert.Contains("Compiled", note, StringComparison.Ordinal);

        // Written into the CACHE, never beside the source — a model family is routinely installed
        // read-only, and writing into someone else's delivery is wrong even where it succeeds.
        Assert.StartsWith(VerilogASourceCompiler.CacheDirectory, artefact, StringComparison.Ordinal);
        Assert.NotEqual(Path.GetDirectoryName(source), Path.GetDirectoryName(artefact));
    }

    /// <summary>
    /// The owner's own question, and the property the whole cache exists for: <b>a simulation of an
    /// unedited model compiles nothing.</b>
    ///
    /// <para><b>The second compiler REFUSES to compile but still identifies itself.</b> That is what
    /// makes this a real measurement rather than a timing guess: if anything reached for the
    /// compiler to BUILD, the stub exits non-zero and the call throws. It is deliberately not tested
    /// by deleting the compiler, because a cache hit does still ask the compiler what it is — the
    /// compiler's identity is half the cache key, so keying on the source alone would serve an old
    /// compiler's output forever. That probe is one `--version` per provider resolve, not per
    /// simulation, and it is not a rebuild.</para>
    /// </summary>
    [Fact]
    public void AnUnchangedSourceIsNotRecompiled()
    {
        const string Identity = "stub-compiler 1.0";
        UseCompiler(WriteStubCompiler(identity: Identity));
        string source = WriteSource();

        string first = VerilogASourceCompiler.Compile(source, out string firstNote);

        UseCompiler(WriteStubCompiler(identity: Identity, refuseToCompile: true));
        string second = VerilogASourceCompiler.Compile(source, out string secondNote);

        Assert.Equal(first, second);
        Assert.Contains("Compiled", firstNote, StringComparison.Ordinal);
        Assert.Contains("has not changed", secondNote, StringComparison.Ordinal);
    }

    [Fact]
    public void TouchingTheSourceWithoutChangingItDoesNotRecompile()
    {
        const string Identity = "stub-compiler 1.0";
        UseCompiler(WriteStubCompiler(identity: Identity));
        string source = WriteSource();

        string first = VerilogASourceCompiler.Compile(source);

        // The key is the source's CONTENT, deliberately — not its timestamp. Re-saving a file in an
        // editor without changing a character must not cost a rebuild of a 2,300-line model.
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddHours(1));
        UseCompiler(WriteStubCompiler(identity: Identity, refuseToCompile: true));

        Assert.Equal(first, VerilogASourceCompiler.Compile(source));
    }

    [Fact]
    public void EditingTheSourceRecompiles()
    {
        UseCompiler(WriteStubCompiler());
        string source = WriteSource();

        string first = VerilogASourceCompiler.Compile(source);
        File.WriteAllText(source, TrivialSource.Replace("r = 1.0", "r = 2.0"));
        string second = VerilogASourceCompiler.Compile(source, out string note);

        Assert.NotEqual(first, second);
        Assert.True(File.Exists(first));    // the old build is kept — the key, not the file, moved
        Assert.True(File.Exists(second));
        Assert.Contains("Compiled", note, StringComparison.Ordinal);

        // The stub copies its input, so the artefact's own bytes prove the SECOND source was built
        // and not the first one handed back under a new name.
        Assert.Contains("r = 2.0", File.ReadAllText(second), StringComparison.Ordinal);
    }

    [Fact]
    public void EditingAnIncludedFileRecompiles()
    {
        UseCompiler(WriteStubCompiler());

        // The case this exists for: both published model families keep parameters and macros in
        // files BESIDE the source and pull them in. Hashing only the top file would hand back the
        // previous build after a parameter edit — a stale artefact that runs and looks healthy,
        // which is a far worse outcome than a needless recompile.
        string include = Path.Combine(_dir, "params.vams");
        File.WriteAllText(include, "parameter real vxo = 1.3e7;\n");
        string source = WriteSource(text: "`include \"params.vams\"\n" + TrivialSource);

        string first = VerilogASourceCompiler.Compile(source);
        File.WriteAllText(include, "parameter real vxo = 2.6e7;\n");
        string second = VerilogASourceCompiler.Compile(source);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ADifferentCompilerRecompiles()
    {
        UseCompiler(WriteStubCompiler(identity: "stub-compiler 1.0"));
        string source = WriteSource();
        string first  = VerilogASourceCompiler.Compile(source);

        // Same source, different compiler: a different artefact. An upgrade in place changes the
        // answer without changing a byte of the user's source, so the compiler's identity has to be
        // in the key or the old output is served forever.
        UseCompiler(WriteStubCompiler(identity: "stub-compiler 2.0"));
        string second = VerilogASourceCompiler.Compile(source);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void NoCompilerIsACleanRefusalNamingWhatToDo()
    {
        VerilogACompilerDiscovery.PreferredCommand = null;   // nothing set
        VerilogACompilerDiscovery.CandidateCommands = [];    // and nothing on PATH

        var ex = Assert.Throws<ExternalDeviceException>(
            () => VerilogASourceCompiler.Compile(WriteSource()));

        Assert.Contains("No Verilog-A compiler was found", ex.Message, StringComparison.Ordinal);
        // It must say what to do about it, and that the .osdi route still works.
        Assert.Contains("Settings", ex.Message, StringComparison.Ordinal);
        Assert.Contains(".osdi", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACompilerRefusalCarriesTheCompilersOwnDiagnosticsVerbatim()
    {
        UseCompiler(WriteStubCompiler(failing: true));

        var ex = Assert.Throws<ExternalDeviceException>(
            () => VerilogASourceCompiler.Compile(WriteSource()));

        // VERBATIM. The line and column are the whole value of a compiler error, and a paraphrase
        // of one is strictly worse than the error itself.
        Assert.Contains("crf_fixture.va:7:12: syntax error, unexpected 'endmodule'",
                        ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedCompileLeavesNothingTheCacheWouldReuse()
    {
        string source = WriteSource();

        UseCompiler(WriteStubCompiler(failing: true));
        Assert.Throws<ExternalDeviceException>(() => VerilogASourceCompiler.Compile(source));

        // A half-written artefact left in the cache would be picked up as a successful build of this
        // source and never rebuilt. The same source must still compile once a working compiler is
        // named — with the SAME identity, so nothing but the failure distinguishes the two runs.
        UseCompiler(WriteStubCompiler());
        Assert.True(File.Exists(VerilogASourceCompiler.Compile(source)));
    }

    [Fact]
    public void ANamedCompilerOutranksPath()
    {
        // A preference that lost to PATH would be inert on exactly the machine that needed it: one
        // with a compiler already on PATH that is the wrong version or the wrong build.
        VerilogACompilerDiscovery.CandidateCommands = ["definitely-not-a-real-command-xyz"];
        UseCompiler(WriteStubCompiler(identity: "the one I named"));

        var found = VerilogACompilerDiscovery.Find(out _);

        Assert.NotNull(found);
        Assert.Equal("the one I named", found!.Identity);
        Assert.Equal("set in Settings", found.HowFound);
    }

    [Fact]
    public void ANamedCompilerThatDoesNotWorkIsReportedRatherThanFallenBackFrom()
    {
        // Silently using a different compiler than the one the user named means the artefact they
        // get is not the one they asked for, and nothing said so.
        string working = WriteStubCompiler(identity: "the one on PATH");
        VerilogACompilerDiscovery.CandidateCommands = [working];
        UseCompiler(Path.Combine(_dir, "not-here"));

        var found = VerilogACompilerDiscovery.Find(out var rejected);

        Assert.Null(found);
        Assert.Contains(rejected, r => r.Contains("set in Settings", StringComparison.Ordinal));
    }

    [Fact]
    public void TheEnvironmentVariableNamesACompilerForAHeadlessRun()
    {
        VerilogACompilerDiscovery.PreferredCommand = null;
        Environment.SetEnvironmentVariable(
            VerilogACompilerDiscovery.EnvironmentVariable,
            WriteStubCompiler(identity: "from the environment"));

        var found = VerilogACompilerDiscovery.Find(out _);

        Assert.NotNull(found);
        Assert.Equal("from the environment", found!.Identity);
    }

    [Fact]
    public void AMissingSourceIsRefusedByName()
    {
        UseCompiler(WriteStubCompiler());
        string missing = Path.Combine(_dir, "not-written.va");

        var ex = Assert.Throws<ExternalDeviceException>(
            () => VerilogASourceCompiler.Compile(missing));

        Assert.Contains(missing, ex.Message, StringComparison.Ordinal);
    }

    // ── Include scanning ─────────────────────────────────────────────────────

    [Fact]
    public void IncludesAreFoundAndCommentedOnesAreNot()
    {
        string text = """
            `include "real.vams"
            // `include "commented-out.vams"
            /* `include "also-commented.vams" */
            `include <system.vams>
            """;

        var targets = VerilogASourceCompiler.IncludeTargets(text).ToList();

        Assert.Equal(["real.vams"], targets);
    }

    [Fact]
    public void TheSourcesOwnDirectoryIsTheFirstIncludePath()
    {
        // A model that compiles from its own folder and fails from circuitRF is this, every time.
        string source = Path.Combine(_dir, "sub", "m.va");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, TrivialSource);

        var dirs = VerilogASourceCompiler.IncludeDirectories(source, []);

        Assert.Equal(Path.GetDirectoryName(source), dirs[0]);
    }

    // ── The seam: a .va reaches a RUNNING worker ─────────────────────────────

    /// <summary>
    /// End to end through the real <see cref="VerilogAFileResolver"/>: a component pointed at
    /// <c>.va</c> SOURCE compiles, loads and describes itself — the same provider name, the same
    /// registry and the same worker process a Run uses.
    ///
    /// <para><b>Why this is not covered by the tests above.</b> They prove the compiler is driven
    /// correctly; this proves the compile is wired into the ONE seam every consumer passes through
    /// (the parameter dialog, elaboration, <c>Cli hb</c>, harmonicaRF's Set DUT). Putting the compile
    /// anywhere else would give a <c>.va</c> that works in the GUI and fails headless, and only a
    /// test at this level can tell the difference.</para>
    ///
    /// <para>The stub "compiles" by copying circuitRF's own <c>fake-osdi-model</c> over the output,
    /// so what the worker then loads is a genuine artefact this repository built. Skipped with a
    /// reason where that has not been built — it is native, and the standing rule is that a missing
    /// C compiler warns and the build still succeeds.</para>
    /// </summary>
    [FixtureFact(WorkerRel, BuildHowTo)]
    public void SourceReachesARunningWorkerThroughTheOrdinaryResolver()
    {
        string model = FixturePaths.Require(FakeModelRel);   // built by the same script

        // The shipped worker lives beside the APPLICATION, and a test binary is not that. Pointing
        // ToolsDirectory at the built worker is what a real install already satisfies; the resolver's
        // own lookup rule is exercised unchanged.
        DeviceWorkerManifest.ToolsDirectory =
            Path.GetDirectoryName(FixturePaths.Require(WorkerRel))!;

        // A "compiler" that answers --version and produces a real artefact.
        bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        string stub  = Path.Combine(_dir, "stub-real" + (windows ? ".cmd" : ".sh"));
        File.WriteAllText(stub, windows
            ? "@echo off\r\n"
              + "if \"%~1\"==\"--version\" (echo stub 1.0\r\nexit /b 0)\r\n"
              + "copy /y \"" + model + "\" %~3 >nul\r\n"
            : "#!/bin/sh\n"
              + "if [ \"$1\" = \"--version\" ]; then echo \"stub 1.0\"; exit 0; fi\n"
              + "cp \"" + model + "\" \"$3\"\n");
        if (!windows) File.SetUnixFileMode(stub,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        UseCompiler(stub);

        string source = WriteSource("placed_by_a_user.va");
        string name   = VerilogAFileResolver.ProviderNameFor(source);

        var resolver = new VerilogAFileResolver();
        using var provider = resolver.Resolve(name) as IDisposable;
        Assert.NotNull(provider);

        var described = ((IExternalDeviceProvider)provider!).Describe();
        Assert.NotEmpty(described);
        // The artefact really was loaded and interrogated: these are fake-osdi-model's own devices.
        Assert.Contains(described, d => d.TypeId.Length > 0);
    }

    [Fact]
    public void AnUnresolvableIncludeIsNotAnError()
    {
        // Every model of this shape opens by including a discipline header the COMPILER supplies.
        // Treating its absence here as an error would refuse every real model.
        UseCompiler(WriteStubCompiler());
        string source = WriteSource(text: "`include \"disciplines.vams\"\n" + TrivialSource);

        Assert.True(File.Exists(VerilogASourceCompiler.Compile(source)));
    }
}
