using System.Linq;
using CircuitRF.Design.Plugins;
using Xunit;

namespace CircuitRF.Ui.Tests.Plugins;

/// <summary>brief-plg-1 gate 1: every manifest refusal names its field, and a valid manifest reads back whole.</summary>
public class PluginManifestTests
{
    private const string Valid = """
    {
      "apiVersion": 1, "id": "example.filter", "name": "Filter", "version": "0.3.0",
      "summary": "s", "author": "a", "license": "MIT", "homepage": "https://example.invalid", "minCircuitRF": "1.8.0",
      "run": { "python": { "entry": "f.py", "requiresPython": ">=3.10" }, "osx-arm64": { "exe": "bin/osx-arm64/f" } },
      "kind": "panel", "menu": "Filters", "contexts": ["cell", "layout"],
      "params": [
        { "name": "type", "kind": "choice", "options": ["Chebyshev", "Butterworth"], "default": "Chebyshev" },
        { "name": "order", "kind": "int", "min": 1, "max": 9, "default": 5 },
        { "name": "f0", "kind": "quantity", "unit": "Hz", "default": "2.4 GHz", "min": "1 MHz" },
        { "name": "substrate", "kind": "substrate" },
        { "name": "lengths", "kind": "readout" }
      ],
      "timeouts": { "update": 5, "commit": 60 },
      "ui": { "layout": { "row": [ { "param": "type" }, { "readout": "lengths", "as": "table" } ] } },
      "somethingNewer": { "ignored": true }
    }
    """;

    [Fact]
    public void AValidManifestReadsBackWhole()
    {
        var (m, d) = PluginManifestReader.Read(Valid);
        Assert.NotNull(m);
        Assert.DoesNotContain(d, x => x.IsError);
        Assert.Equal(("example.filter", "Filter", "0.3.0", PluginKind.Panel, "Filters"), (m!.Id, m.Name, m.Version, m.Kind, m.Menu));
        Assert.Equal(["cell", "layout"], m.Contexts);
        Assert.Equal(["type", "order", "f0", "substrate", "lengths"], m.Params.Select(p => p.Name));
        Assert.Equal("Hz", m.Params.Single(p => p.Name == "f0").Unit);
        Assert.Equal((5.0, (double?)null, 60.0), (m.Timeouts.Update!.Value, m.Timeouts.Action, m.Timeouts.Commit!.Value));
        Assert.Contains("\"layout\"", m.UiJson);
        Assert.Equal("filter", m.IdTail);
    }

    [Theory]
    [InlineData("\"id\": \"example.filter\"", "\"id\": \"Example Filter\"", "id")]
    [InlineData("\"id\": \"example.filter\"", "\"id\": \"..\"", "id")]                     // the id names a folder
    [InlineData("\"timeouts\": { \"update\": 5", "\"timeouts\": { \"update\": 1e300", "timeouts.update")]   // would overflow a TimeSpan
    [InlineData("{ \"name\": \"order\", \"kind\": \"int\"", "{ \"name\": \"type\", \"kind\": \"int\"", "params.type.name")]
    [InlineData("\"default\": \"Chebyshev\"", "\"default\": \"Elliptic\"", "params.type.default")]
    [InlineData("\"unit\": \"Hz\"", "\"unit\": \"furlong\"", "params.f0.unit")]
    [InlineData("\"unit\": \"Hz\"", "\"unit\": \"GHz\"", "params.f0.unit")]
    [InlineData("\"name\": \"order\", \"kind\": \"int\"", "\"name\": \"order\", \"kind\": \"int\", \"chars\": \"wide\"", "params.order.chars")]
    [InlineData("\"run\": { \"python\": { \"entry\": \"f.py\", \"requiresPython\": \">=3.10\" }, \"osx-arm64\": { \"exe\": \"bin/osx-arm64/f\" } }",
                "\"run\": { \"amiga-68k\": { \"exe\": \"f\" } }", "run")]
    public void EachRefusalNamesItsField(string from, string to, string field)
    {
        Assert.Contains(from, Valid);
        var (m, d) = PluginManifestReader.Read(Valid.Replace(from, to));
        Assert.Null(m);
        Assert.Contains(d, x => x.IsError && x.Field == field);
    }

    /// <summary>The size hint is characters, clamped, and only means something on a typed box.</summary>
    [Fact]
    public void ACharsHintIsReadAndClamped()
    {
        var (m, _) = PluginManifestReader.Read(Valid
            .Replace("\"name\": \"order\", \"kind\": \"int\"", "\"name\": \"order\", \"kind\": \"int\", \"chars\": 4")
            .Replace("\"name\": \"f0\", \"kind\": \"quantity\"", "\"name\": \"f0\", \"kind\": \"quantity\", \"chars\": 500"));
        var order = m!.Params.Single(p => p.Name == "order");
        var f0 = m.Params.Single(p => p.Name == "f0");
        Assert.Equal((4, 4), (order.Chars, order.ClampedChars));
        Assert.Equal(PluginParam.CharsMax, f0.ClampedChars);
        Assert.Null(m.Params.Single(p => p.Name == "type").ClampedChars);
        Assert.False(m.Params.Single(p => p.Name == "type").IsTyped);
    }

    [Fact]
    public void RunPicksThisPlatformsBuildThenPythonThenRefuses()
    {
        var (m, _) = PluginManifestReader.Read(Valid);
        Assert.Equal("bin/osx-arm64/f", m!.ResolveRun("osx-arm64").Entry!.Exe);
        Assert.Equal("f.py", m.ResolveRun("win-x64").Entry!.PythonEntry);

        var (native, _) = PluginManifestReader.Read(Valid.Replace("\"python\": { \"entry\": \"f.py\", \"requiresPython\": \">=3.10\" }, ", ""));
        Assert.Equal("no build for win-x64", native!.ResolveRun("win-x64").Refusal);
    }

    [Fact]
    public void ANewerContractIsListedDimmedNotRefused()
    {
        var (m, _) = PluginManifestReader.Read(Valid.Replace("\"apiVersion\": 1", $"\"apiVersion\": {PluginApi.Max + 1}"));
        Assert.Equal("needs a newer circuitRF", m!.NeedsNewerCircuitRF);
    }

    /// <summary>Review fix: <c>minCircuitRF</c> was read and never consulted, so a plugin needing a later release started
    /// and failed in ways nobody could attribute. It now dims the plugin as a newer contract does.</summary>
    [Fact]
    public void AMinCircuitRFAboveThisRelease_IsListedDimmed()
    {
        var (m, _) = PluginManifestReader.Read(Valid.Replace("\"minCircuitRF\": \"1.8.0\"", "\"minCircuitRF\": \"999.0.0\""));
        Assert.Equal("needs circuitRF 999.0.0", m!.NeedsNewerCircuitRF);
        Assert.Null(PluginManifestReader.Read(Valid).Manifest!.NeedsNewerCircuitRF);
    }
}
