using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Design.Reference;
using CircuitRF.Design.VendorLibraries.ModelLibrary;
using Xunit;

namespace CircuitRF.Ui.Tests.VendorLibraries;

/// <summary>
/// Brief vendorlib-6 §7: the model-library folder format's readers and writers, its hash, <c>check</c> on a
/// model folder, and the format page's own example — written from the page's text, so the published example
/// cannot rot.
/// </summary>
public sealed class ModelLibraryFormatTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-vl6-" + Guid.NewGuid().ToString("N")[..12]);

    public ModelLibraryFormatTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    // ── 1. round trip ─────────────────────────────────────────────────────────

    [Fact]
    public void Manifests_RoundTripKeepingAnUnknownKey_AndANewerFormatVersionIsRefused()
    {
        string part = Path.Combine(_root, "in.cpart");
        File.WriteAllText(part, PartJson().Replace("\"Revision\": 1", "\"Revision\": 1, \"Future\": { \"x\": 1 }"));
        var (m, refusal) = ModelPartManifest.Read(part);
        Assert.Null(refusal);
        string written = Path.Combine(_root, "out.cpart");
        ModelPartManifest.Write(written, m!);
        var (back, _) = ModelPartManifest.Read(written);
        Assert.Contains("\"Future\"", File.ReadAllText(written));
        Assert.Equal(["g", "d", "s"], back!.Pins);
        Assert.Equal("3 V", back.Parameters.Single().Default);

        string lib = Path.Combine(_root, "library.clib");
        File.WriteAllText(lib, "{ \"Format\": \"circuitrf-model-library\", \"FormatVersion\": 1, \"Name\": \"X\", \"Extra\": [1, 2] }");
        var (l, _) = ModelLibraryManifest.Read(lib);
        ModelLibraryManifest.Write(lib, l!);
        Assert.Contains("\"Extra\"", File.ReadAllText(lib));
        Assert.Equal("X", ModelLibraryManifest.Read(lib).Manifest!.Name);

        File.WriteAllText(part, PartJson().Replace("\"FormatVersion\": 1", "\"FormatVersion\": 2"));
        var (none, why) = ModelPartManifest.Read(part);
        Assert.Null(none);
        Assert.Contains("version 2", why);
        Assert.Contains("version 1", why);
    }

    // ── 2. the hash ───────────────────────────────────────────────────────────

    [Fact]
    public void PublishedHash_IgnoresLineEndings_AndSeesOneChangedByte()
    {
        string dir = WriteModel("TSTFET100");
        string cnl = Path.Combine(dir, "TSTFET100.cnl");
        string before = ModelPartHash.Compute(dir);

        File.WriteAllText(cnl, File.ReadAllText(cnl).Replace("\n", "\r\n"));
        Assert.Equal(before, ModelPartHash.Compute(dir));

        File.WriteAllText(Path.Combine(dir, "part.cpart"), "anything");     // the manifest is not hashed
        Assert.Equal(before, ModelPartHash.Compute(dir));

        File.WriteAllText(cnl, File.ReadAllText(cnl).Replace("R=50", "R=51"));
        Assert.NotEqual(before, ModelPartHash.Compute(dir));
    }

    // ── 3. check on a model folder ────────────────────────────────────────────

    [Fact]
    public void Check_OnASyntheticModelFolder_IsClean()
    {
        string dir = WriteModel("TSTFET100");
        Assert.Empty(ModelFolderCheck.Validate(dir));

        var run = RunCli("check", dir, "--severity", "warning", "--json");
        Assert.True(run.ExitCode == 0, run.StdErr);
        Assert.Contains("\"model-part\"", run.StdOut);
    }

    [Theory]
    [InlineData("part-unreadable",         "check.model.part-unreadable")]
    [InlineData("missing-key",             "check.model.part-unreadable")]
    [InlineData("part-not-folder",         "check.model.part-not-folder")]
    [InlineData("netlist-missing",         "check.model.netlist-missing")]
    [InlineData("top-level-line",          "check.model.netlist-top-level")]
    [InlineData("top-cell",                "check.model.top-cell")]
    [InlineData("pins",                    "check.model.pins")]
    [InlineData("parameters",              "check.model.parameters")]
    [InlineData("reference-leaves-folder", "check.model.reference-leaves-folder")]
    [InlineData("edited-since-exported",   "check.model.edited-since-exported")]
    public void Check_ReportsEachRow_InItsOwnCopy(string defect, string id)
    {
        string dir  = WriteModel("TSTFET100");
        string part = Path.Combine(dir, "part.cpart");
        string cnl  = Path.Combine(dir, "TSTFET100.cnl");
        switch (defect)
        {
            case "part-unreadable":         File.WriteAllText(part, "{"); break;
            case "missing-key":             Edit(part, "\"Pins\"", "\"Pinz\""); break;
            case "part-not-folder":         Edit(part, "\"Part\": \"TSTFET100\"", "\"Part\": \"TSTFET200\""); break;
            case "netlist-missing":         File.Delete(cnl); break;
            case "top-level-line":          File.AppendAllText(cnl, "R:R9 a 0 R=1 Ohm\n"); break;
            case "top-cell":                Edit(part, "\"Cell\": \"TSTFET100\"", "\"Cell\": \"Other\""); break;
            case "pins":                    Edit(part, "[\"g\", \"d\", \"s\"]", "[\"g\", \"s\", \"d\"]"); break;
            case "parameters":              Edit(part, "\"3 V\"", "\"5 V\""); break;
            case "reference-leaves-folder": Edit(cnl, "\"models/TSTFET100.s2p\"", "\"../TSTFET100.s2p\""); break;
            case "edited-since-exported":
                Edit(part, "\"PublishedHash\": \"\"", $"\"PublishedHash\": \"{ModelPartHash.Compute(dir)}\"");
                File.AppendAllText(cnl, "; a comment is still an edit\n");
                break;
        }

        var found = ModelFolderCheck.Validate(dir);
        Assert.Equal(id, Assert.Single(found).Id);
        Assert.Equal(defect == "edited-since-exported" ? CircuitRF.Diagnostics.DiagnosticSeverity.Warning
                                                       : CircuitRF.Diagnostics.DiagnosticSeverity.Error,
                     found[0].Severity);
    }

    [Fact]
    public void Check_OnALibrary_ReportsOnePartNumberInTwoFolders()
    {
        string lib = Path.Combine(_root, "lib");
        WriteModel("TSTFET100", Path.Combine(lib, "A"));
        string second = WriteModel("TSTFET100", Path.Combine(lib, "B"));
        Assert.Empty(ModelFolderCheck.Validate(second));

        var found = ModelLibraryCheck.Validate(lib);
        Assert.Equal("check.model.duplicate-part", Assert.Single(found).Id);
    }

    // ── 4. the published example ──────────────────────────────────────────────

    [Fact]
    public void TheFormatPagesExample_ChecksClean()
    {
        string page = ReferenceLibrary.Read(ReferenceLibrary.Find(ReferenceLibrary.ModelLibraryTopic)!);
        int begin = page.IndexOf("<!-- example:begin -->", StringComparison.Ordinal);
        int end   = page.IndexOf("<!-- example:end -->", StringComparison.Ordinal);
        Assert.True(begin >= 0 && end > begin, "the page's example markers are gone.");

        var files = Regex.Matches(page[begin..end], @"^`([^`\n]+)`\s*\n+```[a-z]*\n(.*?)\n```", RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.True(files.Count >= 3, "the example shows fewer files than a library needs.");
        foreach (System.Text.RegularExpressions.Match m in files)
        {
            string path = Path.Combine(_root, m.Groups[1].Value);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, m.Groups[2].Value + "\n");
        }

        var run = RunCli("check", Path.Combine(_root, "example-devices"), "--severity", "warning", "--json");
        Assert.True(run.ExitCode == 0, run.StdErr);
        Assert.DoesNotContain("warning:", run.StdErr);
        Assert.Contains("\"model-part\"", run.StdOut);      // the model folder was found and checked as one
    }

    // ── fixture ───────────────────────────────────────────────────────────────

    private static string PartJson(string part = "TSTFET100") => $$"""
        {
          "Format": "circuitrf-model",
          "FormatVersion": 1,
          "Part": "{{part}}",
          "Cell": "{{part}}",
          "Kind": "Active",
          "Description": "Synthetic test device",
          "Pins": ["g", "d", "s"],
          "Parameters": [ { "Name": "Vds", "Default": "3 V" } ],
          "Glyph": "FET",
          "Revision": 1,
          "PublishedHash": ""
        }
        """;

    private string WriteModel(string part, string? parent = null)
    {
        string dir = Path.Combine(parent ?? Path.Combine(_root, Guid.NewGuid().ToString("N")[..8]), part);
        Directory.CreateDirectory(Path.Combine(dir, "models"));
        File.WriteAllText(Path.Combine(dir, "part.cpart"), PartJson(part));
        File.WriteAllText(Path.Combine(dir, part + ".cnl"), $"""
            ; synthetic model
            define {part} (g d s)
              parameters Vds=3 V
              R:R1 g s R=50 Ohm
              SnP:S1 d s NumPorts=2 File="models/{part}.s2p"
            end {part}

            """);
        File.WriteAllText(Path.Combine(dir, "models", part + ".s2p"), "# GHz S MA R 50\n1 0 0 1 0 1 0 0 0\n");
        return dir;
    }

    private static void Edit(string path, string from, string to)
    {
        string text = File.ReadAllText(path);
        Assert.Contains(from, text);
        File.WriteAllText(path, text.Replace(from, to));
    }

    private (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.ArgumentList.Add(CliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment[CircuitRF.Design.UserStateDirectory.EnvironmentVariable] = Path.Combine(_root, "state");

        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(ModelLibraryFormatTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        return Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
    }
}
