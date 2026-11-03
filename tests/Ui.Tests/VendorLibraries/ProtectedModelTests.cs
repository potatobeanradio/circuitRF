using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.VendorLibraries;
using CircuitRF.Design.VendorLibraries.ModelLibrary;
using CircuitRF.Design.Workspace;
using CircuitRF.Diagnostics;
using CircuitRF.Engine;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.ViewModels.Dock;
using Xunit;
using static CircuitRF.Ui.Tests.VendorLibraries.ModelLibraryExportTests;

namespace CircuitRF.Ui.Tests.VendorLibraries;

/// <summary>
/// Brief vendorlib-15 §7: compiled-only model folders and signed libraries. Platform builds resolve before a copied-up
/// one; a compiled-only folder checks, runs DC through its <c>.cnl</c> with an SnP beside it, and is greyed where it has
/// no build; Export ships the build it ran with plus the maker's other builds, and compiles only when asked; a signed
/// library verifies, names what changed, and refuses what does not verify; and the format page alone is enough to
/// verify one. Every part, key and publisher here is synthetic.
/// </summary>
[Collection(CellStatGlobalsCollection.Name)]
public sealed class ProtectedModelTests : IDisposable
{
    private const string FakeOsdi = "tools/fake-osdi-model/fake_osdi.osdi";
    private const string WorkerRel = "tools/osdi-worker/osdi-worker";
    private const string HowTo = "run tools/osdi-worker/build.sh (needs a C compiler)";

    private static readonly string Here = OsdiBuilds.CurrentRid;
    /// <summary>A platform that is not this one.</summary>
    private static readonly string Other = Here == "win-x64" ? "linux-x64" : "win-x64";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-vl15-" + Guid.NewGuid().ToString("N")[..12]);

    public ProtectedModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    // 1 — the platform build wins over a copied-up one; with neither, the refusal names this platform and what exists.
    [Fact]
    public void ThePlatformBuildIsChosen_AndWithNoneTheRefusalNamesThePlatformAndTheFolders()
    {
        string models = Path.Combine(_dir, "models");
        string flat = Path.Combine(models, "TSTFET100.osdi");
        Directory.CreateDirectory(Path.Combine(models, Here));
        File.WriteAllText(flat, "copied up");
        File.WriteAllText(Path.Combine(models, Here, "TSTFET100.osdi"), "this platform");

        Assert.Equal(Path.Combine(models, Here, "TSTFET100.osdi"), OsdiBuilds.Resolve(flat));
        Assert.Equal(VerilogAFileResolver.Prefix + Path.Combine(models, Here, "TSTFET100.osdi"), VerilogAFileResolver.ProviderNameFor(flat));

        File.Delete(flat);
        File.Delete(Path.Combine(models, Here, "TSTFET100.osdi"));
        Directory.CreateDirectory(Path.Combine(models, Other));
        File.WriteAllText(Path.Combine(models, Other, "TSTFET100.osdi"), "another platform");

        var ex = Assert.Throws<ExternalDeviceException>(() => new VerilogAFileResolver().Resolve(VerilogAFileResolver.ProviderNameFor(flat)));
        Assert.Contains($"for {Here}", ex.Message);
        Assert.Contains($"builds for: {Other}", ex.Message);
    }

    // 2 — a compiled-only folder checks clean; a Builds entry with no folder is an error.
    [FixtureFact(FakeOsdi, HowTo)]
    public void ACompiledOnlyFolder_ChecksClean_AndABuildsEntryWithNoFolderIsAnError()
    {
        string model = WriteCompiledModel(Path.Combine(_dir, "lib"), [Here]);
        Assert.DoesNotContain(ModelFolderCheck.Validate(model), d => d.Severity >= DiagnosticSeverity.Warning);

        WriteManifest(model, [Here, Other]);
        var missing = Assert.Single(ModelFolderCheck.Validate(model), d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal("check.model.build-missing", missing.Id);
        Assert.Contains($"models/{Other}/TSTFET100.osdi", missing.Render());
    }

    // 2 — and it runs DC through its .cnl, with the SnP beside the compiled device in the same .cnl used.
    [FixtureFact(WorkerRel, HowTo)]
    public void ACompiledOnlyModel_RunsDcThroughItsNetlist_WithItsSnp()
    {
        Assert.True(FixturePaths.Exists(FakeOsdi), HowTo);
        string model = WriteCompiledModel(Path.Combine(_dir, "lib"), [Here]);
        EnsureWorkerBeside(AppContext.BaseDirectory);

        // g = 2 mS in series with the SnP's 100 Ω: 1 V across both puts 1/600 A through them.
        string bench = File.ReadAllText(ModelNetlist.PathOf(model))
                       + "\nVdc:V1 in 0 Vdc=1\nTSTFET100:X1 in 0\nanalysis DC1 type=dc\n";
        var (lib, tb) = new CnlReader().Read(bench, "bench", sourceDirectory: model);
        var nl = new CircuitRF.Core.Elaboration.Elaborator(lib) { BaseDirectory = model }.Elaborate(tb);
        var dc = NonlinearDcEngine.Run(nl);
        Assert.True(dc.Converged);
        var (rows, names) = nl.Nodes.ResultRows(excludeInternal: false);
        double vm = dc.NodeVoltages[rows[Array.IndexOf(names, "X1.m")] - 1];
        Assert.Equal(1.0 - 500.0 / 600.0, vm, 6);
    }

    // 3 — D3: with this platform not in Builds, the palette row is greyed with the sentence, and placing it is refused.
    [Fact]
    public void WithNoBuildHere_ThePaletteRowIsGreyedWithTheSentence_AndPlacingIsRefused()
    {
        string lib = Path.Combine(_dir, "lib");
        WriteCompiledModel(lib, [Other], withBuild: false);
        string ws = WorkspaceCreate.Create(_dir, "ws", null, vendorLibraries: [("Lib", lib)]).WorkspaceDir;

        var entry = Assert.Single(VendorLibraryIndex.Build(lib).Entries);
        string sentence = $"No build of this model for {Here}. The publisher ships it for: {Other}.";
        Assert.Equal(sentence, entry.Model!.NoBuildSentence());

        var row = new VendorPartRowVm("Lib", entry, _ => { });
        Assert.False(row.IsPlaceable);
        Assert.Equal(sentence, row.Detail);
        Assert.Contains(sentence, row.ToolTipText);

        string schDir = Path.Combine(ws, "top", "schematic");
        Directory.CreateDirectory(schDir);
        var sink = new Sink();
        var vm = new SchematicViewModel(new SchematicEditModel { SchematicDirectory = schDir }, sink);
        vm.CommitVendorPlacement(row.Ref, SymbolRotation.R0, 0, 0);
        Assert.Empty(vm.EditModel.Components);
        Assert.Contains(sink.Lines, l => l.Contains(sentence, StringComparison.Ordinal));
    }

    // R-vl15-1d — Export ships the build it ran with in models/<rid>/ and the maker's other builds beside it; the dialog's
    // compile checkbox starts OFF; and (R-vl15-1e) Copy to Workspace carries the builds unchanged.
    [FixtureFact(WorkerRel, HowTo)]
    public void Export_ShipsThisBuildAndTheOthers_AndCompileStartsOff()
    {
        Assert.True(FixturePaths.Exists(FakeOsdi), HowTo);
        EnsureWorkerBeside(AppContext.BaseDirectory);
        string ws = WorkspaceCreate.Create(_dir, "ws", null).WorkspaceDir;
        string ext = Path.Combine(_dir, "builds");
        Directory.CreateDirectory(ext);
        File.Copy(FixturePaths.Require(FakeOsdi), Path.Combine(ext, "TSTFET100.osdi"));
        string otherBuild = Path.Combine(ext, "other.osdi");
        File.WriteAllText(otherBuild, "a build made elsewhere");

        var sch = new SchematicEditModel();
        var p1 = Pin("P1", 1, "a", -400, 0);
        var va = new EditableComponent { InstanceName = "D1", Symbol = SymbolKind.VerilogA, X = 0, Y = 0 };
        va.Parameters.Add(new EditableParameter { Name = "File", Expression = Path.Combine(ext, "TSTFET100.osdi") });
        va.Parameters.Add(new EditableParameter { Name = "Model", Expression = "crf_rc" });
        va.Parameters.Add(new EditableParameter { Name = "g0", Expression = "0.002" });
        var p2 = Pin("P2", 2, "b", 400, 0);
        foreach (var c in new[] { p1, va, p2 }) sch.Components.Add(c);
        Wire(sch, p1, 0, va, 0);
        Wire(sch, va, 1, p2, 0);
        string cell = WriteCell(ws, "TSTFET100", sch);

        var dialog = new ExportToLibraryViewModel(cell, [], Path.Combine(_dir, "lib"));
        Assert.False(dialog.CompileModels);
        Assert.False(dialog.Options(replace: false).CompileModels);

        var result = ModelLibraryExport.Export(cell, new ExportOptions
        {
            LibraryDir = Path.Combine(_dir, "lib"), Builds = new Dictionary<string, string> { [Other] = otherBuild },
        });
        Assert.True(result.Succeeded, result.Refusal);
        string model = result.ModelDir!;
        Assert.True(File.Exists(Path.Combine(model, "models", Here, "TSTFET100.osdi")));
        Assert.Equal("a build made elsewhere", File.ReadAllText(Path.Combine(model, "models", Other, "TSTFET100.osdi")));
        Assert.False(File.Exists(Path.Combine(model, "models", "TSTFET100.osdi")));
        Assert.Matches(@"File=""?models/TSTFET100\.osdi""?\s", File.ReadAllText(ModelNetlist.PathOf(model)));
        var (part, _) = ModelPartManifest.Read(Path.Combine(model, ModelFolder.PartFileName));
        Assert.Equal(OsdiBuilds.Rids.Where(r => r == Here || r == Other), part!.Builds);
        Assert.EndsWith($"Checked on {Here}. The other 1 build was copied, not run.", result.Message);

        // R-vl15-1e — Copy to Workspace carries every build as a file, byte for byte, and the copy still resolves it.
        var copy = VendorModelAuthoring.CopyToWorkspace(model, WorkspaceCreate.Create(_dir, "ws2", null).WorkspaceDir, "Lib");
        Assert.True(copy.Succeeded, copy.Refusal);
        foreach (string rid in new[] { Here, Other })
            Assert.Equal(File.ReadAllBytes(Path.Combine(model, "models", rid, "TSTFET100.osdi")),
                         File.ReadAllBytes(Path.Combine(copy.CellDir!, "models", rid, "TSTFET100.osdi")));
        Assert.Equal(Path.Combine(copy.CellDir!, "models", Here, "TSTFET100.osdi"),
                     OsdiBuilds.Resolve(Path.Combine(copy.CellDir!, "models", "TSTFET100.osdi")));
    }

    // R-vl15-1d with Compile Verilog-A models ticked: the source is compiled and the build ships in its place.
    [CompilerFact]
    public void CompileModelsOn_ShipsTheBuildAndNotTheSource()
    {
        EnsureWorkerBeside(AppContext.BaseDirectory);
        string ws = WorkspaceCreate.Create(_dir, "ws", null).WorkspaceDir;
        string src = Path.Combine(_dir, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "tstres.va"), """
            `include "disciplines.vams"
            module tstres(a, b);
              inout a, b;
              electrical a, b;
              parameter real r = 100.0;
              analog I(a, b) <+ V(a, b) / r;
            endmodule
            """);

        var sch = new SchematicEditModel();
        var p1 = Pin("P1", 1, "a", -400, 0);
        var va = new EditableComponent { InstanceName = "D1", Symbol = SymbolKind.VerilogA, X = 0, Y = 0 };
        va.Parameters.Add(new EditableParameter { Name = "File", Expression = Path.Combine(src, "tstres.va") });
        var p2 = Pin("P2", 2, "b", 400, 0);
        foreach (var c in new[] { p1, va, p2 }) sch.Components.Add(c);
        Wire(sch, p1, 0, va, 0);
        Wire(sch, va, 1, p2, 0);
        string cell = WriteCell(ws, "TSTRES", sch);

        var plan = ModelLibraryExport.Plan(cell, new ExportOptions { LibraryDir = Path.Combine(_dir, "lib") });
        Assert.Null(plan.CompileUnavailable);
        Assert.False(plan.ShipsCompiled);   // off by default: the source ships

        var result = ModelLibraryExport.Export(cell, new ExportOptions { LibraryDir = Path.Combine(_dir, "lib"), CompileModels = true });
        Assert.True(result.Succeeded, result.Refusal);
        string model = result.ModelDir!;
        Assert.True(File.Exists(Path.Combine(model, "models", Here, "tstres.osdi")));
        Assert.Empty(Directory.EnumerateFiles(model, "*.va", SearchOption.AllDirectories));
        Assert.Equal([Here], ModelPartManifest.Read(Path.Combine(model, ModelFolder.PartFileName)).Manifest!.Builds);
        Assert.DoesNotContain(ModelFolderCheck.Validate(model), d => d.Severity == DiagnosticSeverity.Error);
    }

    // Over MCP: compileModels is the flag, and each builds entry is one --build with its file confined to the root.
    [Fact]
    public void OverMcp_CompileModelsAndBuildsBecomeTheFlags_AndABuildOutsideTheRootIsRefused()
    {
        var root = CircuitRF.Cli.Serve.PathRoot.Open(_dir, out _)!;
        JsonObject Call(object builds) => System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new
        {
            what = "libraryPart", cell = Path.Combine(_dir, "cell"), library = Path.Combine(_dir, "lib"),
            compileModels = true, builds,
        }))!.AsObject();

        var argv = CircuitRF.Cli.Serve.ToolCatalog.ToArgv("create",
            Call(new Dictionary<string, string> { ["win-x64"] = "builds/a.osdi" }), root, out var refusal);
        Assert.Null(refusal);
        Assert.Contains("--compile-models", argv!);
        Assert.Equal(["--build", "win-x64=" + Path.Combine(_dir, "builds", "a.osdi")], argv!.SkipWhile(a => a != "--build").Take(2));

        Assert.Null(CircuitRF.Cli.Serve.ToolCatalog.ToArgv("create",
            Call(new Dictionary<string, string> { ["win-x64"] = "/elsewhere/a.osdi" }), root, out var outside));
        Assert.NotNull(outside);
    }

    // 5 — sign, then verify: intact; an .osdi byte names its part; a sums byte does not verify; a trusted name with a new
    // key is an error.
    [Fact]
    public void SignThenVerify_IntactChangedTamperedAndAnotherKey()
    {
        var (lib, key) = SignedLibrary();
        string fp = LibrarySignature.NewKey(Path.Combine(_dir, "unused.pem"), out _)!;   // only to have a second key
        var trustedOwn = new Dictionary<string, string> { ["TST Devices"] = LibrarySignature.Inspect(lib).Fingerprint };

        var intact = LibrarySignature.Verify(lib, trustedOwn);
        Assert.Equal(LibrarySignatureState.SignedTrusted, intact.State);
        Assert.Equal("Signed by TST Devices", intact.RowText);
        Assert.Equal(LibrarySignatureState.SignedUntrusted, LibrarySignature.Verify(lib, new Dictionary<string, string>()).State);

        string osdi = Path.Combine(lib, "TSTFET100", "models", Here, "TSTFET100.osdi");
        File.AppendAllText(osdi, "x");
        var changed = LibrarySignature.Verify(lib, trustedOwn);
        Assert.Equal(LibrarySignatureState.ChangedSinceSigning, changed.State);
        Assert.Equal(["TSTFET100"], changed.ChangedParts);
        Assert.Contains(LibrarySignature.Findings(lib, trustedOwn), d => d.Severity == DiagnosticSeverity.Warning && d.Render().Contains("TSTFET100"));

        string sums = Path.Combine(lib, LibrarySignature.SumsFileName);
        byte[] bytes = File.ReadAllBytes(sums);
        bytes[7] ^= 1;
        File.WriteAllBytes(sums, bytes);
        Assert.Equal(LibrarySignatureState.DoesNotVerify, LibrarySignature.Verify(lib, trustedOwn).State);
        Assert.Contains(LibrarySignature.Findings(lib, trustedOwn), d => d.Severity == DiagnosticSeverity.Error);

        Assert.True(LibrarySignature.Sign(lib, key).Succeeded);   // re-signed: intact again
        var otherKey = new Dictionary<string, string> { ["TST Devices"] = fp };
        var swapped = LibrarySignature.Verify(lib, otherKey);
        Assert.Equal(LibrarySignatureState.TrustedNameDifferentKey, swapped.State);
        Assert.Equal("TST Devices was trusted with a different key. This library may not come from them.", swapped.RowText);
        Assert.Contains(LibrarySignature.Findings(lib, otherKey), d => d.Severity == DiagnosticSeverity.Error);
    }

    // 6 — sign refuses a key inside the library, and a library that fails check; --new-key never overwrites.
    [Fact]
    public void Sign_RefusesAKeyInsideTheLibrary_ABrokenLibrary_AndOverwritingAKey()
    {
        string lib = Path.Combine(_dir, "lib");
        string model = WriteCompiledModel(lib, [Here]);
        ModelLibraryManifest.Write(Path.Combine(lib, ModelFolder.LibraryFileName), new ModelLibraryManifest { Name = "TST", Publisher = "TST Devices" });

        string inside = Path.Combine(lib, "key.pem");
        var (exit, _, err) = RunCliPublic("sign", "--new-key", inside, lib);
        Assert.Equal(1, exit);
        Assert.Contains("inside the library", err);
        Assert.False(File.Exists(inside));

        string key = Path.Combine(_dir, "key.pem");
        Assert.NotNull(LibrarySignature.NewKey(key, out _));
        Assert.Null(LibrarySignature.NewKey(key, out string? overwrite));
        Assert.Contains("never overwritten", overwrite);
        File.Copy(key, inside);
        Assert.Contains("inside the library", LibrarySignature.Sign(lib, inside).Refusal);

        WriteManifest(model, [Here, Other]);   // Builds names a folder that is not there: check fails at error
        var broken = LibrarySignature.Sign(lib, key);
        Assert.Contains("does not check clean", broken.Refusal);
        Assert.False(File.Exists(Path.Combine(lib, LibrarySignature.SigFileName)));
    }

    // 7 — the format page's own rules verify a signed library, with no LibrarySignature call.
    [Fact]
    public void TheFormatPagesRules_AloneVerifyASignedLibrary()
    {
        var (lib, _) = SignedLibrary();
        string page = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "user", "src", "reference", "model-library-format.md"));
        // The page states the rules this verifier follows; hold the ones it depends on to the page's text.
        foreach (string rule in new[] { "IEEE P1363", "SubjectPublicKeyInfo", "sorted", "two spaces", "first 16 bytes" })
            Assert.Contains(rule, page);

        // library.sums, from scratch: one line per model folder (its PublishedHash rule) and one for library.clib.
        var lines = new List<(string Path, string Hash)>();
        foreach (string partFile in Directory.EnumerateFiles(lib, "part.cpart", SearchOption.AllDirectories))
        {
            string dir = Path.GetDirectoryName(partFile)!;
            var list = new StringBuilder();
            foreach (string rel in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                         .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'))
                         .Where(r => r != "part.cpart").Order(StringComparer.Ordinal))
            {
                byte[] b = File.ReadAllBytes(Path.Combine(dir, rel));
                if (Regex.IsMatch(rel, @"\.(cnl|csch|csym|ccell|va|vams|lib|cir|sub|txt|md|s\d+p)$", RegexOptions.IgnoreCase)
                    || rel.EndsWith(".ccell", StringComparison.Ordinal))
                    b = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(b).Replace("\r\n", "\n"));
                list.Append(Hex(SHA256.HashData(b))).Append("  ").Append(rel).Append('\n');
            }
            lines.Add((Path.GetRelativePath(lib, dir).Replace('\\', '/'), "sha256:" + Hex(SHA256.HashData(Encoding.UTF8.GetBytes(list.ToString())))));
        }
        lines.Add(("library.clib", "sha256:" + Hex(SHA256.HashData(File.ReadAllBytes(Path.Combine(lib, "library.clib"))))));
        string sums = string.Concat(lines.OrderBy(l => l.Path, StringComparer.Ordinal).Select(l => $"{l.Hash}  {l.Path}\n"));
        byte[] onDisk = File.ReadAllBytes(Path.Combine(lib, "library.sums"));
        Assert.Equal(sums, Encoding.UTF8.GetString(onDisk));

        using var sig = JsonDocument.Parse(File.ReadAllText(Path.Combine(lib, "library.sig")));
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(sig.RootElement.GetProperty("PublicKey").GetString()!), out _);
        Assert.True(key.VerifyData(onDisk, Convert.FromBase64String(sig.RootElement.GetProperty("Signature").GetString()!),
                                   HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        Assert.Equal("TST Devices", sig.RootElement.GetProperty("Publisher").GetString());

        static string Hex(byte[] b) => Convert.ToHexStringLower(b);
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    /// <summary>A library with one compiled-only model, signed by a fresh key. Returns the library and the key.</summary>
    private (string Lib, string Key) SignedLibrary()
    {
        string lib = Path.Combine(_dir, "lib");
        WriteCompiledModel(lib, [Here], withBuild: true, realBinary: false);
        ModelLibraryManifest.Write(Path.Combine(lib, ModelFolder.LibraryFileName), new ModelLibraryManifest { Name = "TST", Publisher = "TST Devices" });
        string key = Path.Combine(_dir, "signing.pem");
        Assert.NotNull(LibrarySignature.NewKey(key, out _));
        var signed = LibrarySignature.Sign(lib, key);
        Assert.True(signed.Succeeded, signed.Refusal);
        return (lib, key);
    }

    /// <summary>
    /// A compiled-only model folder by hand: <c>TSTFET100.cnl</c> places the fake model's two-terminal conductance by its
    /// platform-neutral name and a 100 Ω series SnP after it; the build sits in <c>models/&lt;rid&gt;/</c> and no source
    /// is anywhere in the folder.
    /// </summary>
    private static string WriteCompiledModel(string lib, IReadOnlyList<string> builds, bool withBuild = true, bool realBinary = true)
    {
        string dir = Path.Combine(lib, "TSTFET100");
        Directory.CreateDirectory(Path.Combine(dir, "models", Here));
        if (withBuild)
        {
            string dst = Path.Combine(dir, "models", Here, "TSTFET100.osdi");
            if (realBinary) File.Copy(FixturePaths.Require(FakeOsdi), dst);
            else File.WriteAllText(dst, "a compiled model");
        }
        // A series 100 Ω two-port in 50 Ω: S11 = S22 = S21 = S12 = 0.5.
        File.WriteAllText(Path.Combine(dir, "models", "pkg.s2p"),
            "# GHz S MA R 50\n0 0.5 0 0.5 0 0.5 0 0.5 0\n1 0.5 0 0.5 0 0.5 0 0.5 0\n");
        File.WriteAllText(Path.Combine(dir, "TSTFET100.cnl"), """
            ; synthetic compiled-only model
            define TSTFET100 (a b)
              VerilogA:D1 a m File="models/TSTFET100.osdi" Model=crf_rc g0=0.002 c=1e-12 tc=0 tnom=300
              SnP:S1 m b NumPorts=2 File="models/pkg.s2p"
            end TSTFET100

            """);
        WriteManifest(dir, builds);
        return dir;
    }

    private static void WriteManifest(string dir, IReadOnlyList<string> builds)
        => ModelPartManifest.Write(Path.Combine(dir, ModelFolder.PartFileName), new ModelPartManifest
        {
            Part = "TSTFET100", Cell = "TSTFET100", Kind = ModelPartManifest.KindActive, Pins = ["a", "b"],
            Description = "Synthetic compiled-only device", Builds = [.. builds],
        });

    /// <summary>The checkout's worker beside the test assembly, which is where the resolver looks by default — moved in by
    /// rename, never rewritten in place, so a process another test started on it is never disturbed.</summary>
    private static void EnsureWorkerBeside(string dir)
    {
        string worker = FixturePaths.Require(WorkerRel);
        string target = Path.Combine(dir, "osdi-worker");
        if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(File.ReadAllBytes(worker))) return;
        string staged = target + "." + Guid.NewGuid().ToString("N")[..8];
        File.Copy(worker, staged);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(staged, File.GetUnixFileMode(worker));
        File.Move(staged, target, overwrite: true);
    }

    private sealed class Sink : CircuitRF.Ui.Messages.IMessageSink
    {
        public List<string> Lines { get; } = [];
        public void Post(CircuitRF.Ui.Messages.MessageLevel level, string text, string? filePath = null) => Lines.Add(text);
        public void Clear() => Lines.Clear();
    }
}
