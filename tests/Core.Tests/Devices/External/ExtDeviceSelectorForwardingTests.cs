using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.External;

/// <summary>
/// An <c>ExtDevice</c>'s <c>File</c> and <c>Model</c> are SELECTORS — stored verbatim rather than
/// evaluated, because a path is not an expression (a leading <c>/</c> alone stops the parser at
/// position 0) and one that happened to parse as arithmetic would be silently turned into a number.
///
/// <para><b>What verbatim must not swallow is a REFERENCE.</b> A kit's device cell declares its data
/// file as a cell parameter and forwards it into the device by name — <c>File=File</c> — which is the
/// ordinary way a netlist passes a value down, and the only way one part can be instantiated at
/// several file-backed sizes. Read verbatim, the device is handed the literal four characters
/// <c>File</c>. Owner-reported: every operating point failed, and the worker's own log said
/// <c>File=File (NOT READABLE HERE)</c> — the numeric parameters beside it had resolved correctly,
/// because those are not selectors.</para>
///
/// <para><b>The rule that separates the two is the quoting, not a guess.</b> A netlist writes a
/// literal path in quotes and a reference as a bare name, so a quoted value is a literal, always; a
/// bare name is resolved only when the scope actually binds it, and is otherwise left exactly as it
/// was. Nothing else changes, so an enum, a bare unquoted path and an unbound name all behave as
/// before.</para>
/// </summary>
[Collection(ExternalProviderRegistryCollection.Name)]
public sealed class ExtDeviceSelectorForwardingTests : IDisposable
{
    public void Dispose() => ExternalDeviceRegistry.Clear();

    private const string Path1 = "/kits/acme/data/device_15p6.mdl";
    private const string Path2 = "/kits/acme/data/device_9p6.mdl";

    /// <summary>
    /// The shape a kit ships: a cell declaring the data file as a parameter, forwarding it into
    /// the device by name, instantiated twice with different files.
    /// </summary>
    private static ElaboratedNetlist Elaborate(
        string forwarded, string? declaredDefault, params string[] instanceFiles)
    {
        ExternalDeviceRegistry.Register(new DeviceWorkerProvider("AcmeKit", new FakeDeviceWorker()));

        var cell = new Cell("acme_fet");
        cell.Ports.AddRange(["g", "d", "s", "th"]);
        if (declaredDefault is not null)
            cell.Parameters.Add(new ParameterDeclaration("File", declaredDefault, null));
        cell.Parameters.Add(new ParameterDeclaration("Scale", "-1", null));

        cell.Instances.Add(new Instance("FET1", "ExtDevice", ["g", "d", "s", "th"],
        [
            new ParameterAssignment("Provider", "AcmeKit"),
            new ParameterAssignment("Type",     FakeDeviceWorker.NonlinearType),
            new ParameterAssignment("File",     forwarded),
            new ParameterAssignment("Scale",    "Scale"),
        ]));

        var lib = new Library("lib");
        lib.Cells.Add(cell);

        var tb = new TestBench("tb");
        for (int i = 0; i < instanceFiles.Length; i++)
            tb.Instances.Add(new Instance($"X{i + 1}", "acme_fet", ["g", "d", "0", $"th{i}"],
            [
                new ParameterAssignment("File", instanceFiles[i]),
                new ParameterAssignment("Scale", ((i + 1) * 1e-6).ToString("R")),
            ]));

        return new Elaborator(lib).Elaborate(tb);
    }

    private static string FileOf(ElaboratedNetlist netlist, string path)
        => netlist.Components.Single(c => c.InstancePath == path).Parameters["File"].AsString();

    // ── The gate ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The reported failure, in miniature: a cell parameter forwarded into the device by name must
    /// arrive as the value, not as its own name.
    /// </summary>
    [Fact]
    public void ACellParameterForwardedByNameArrivesAsItsValue()
    {
        var netlist = Elaborate("File", $"\"{Path2}\"", $"\"{Path1}\"");
        Assert.Equal(Path1, FileOf(netlist, "X1.FET1"));
    }

    /// <summary>
    /// Two instances of one cell at different files stay different. This is what the forwarding is
    /// FOR — one part, several file-backed sizes — and it is the case a per-instance literal cannot
    /// express.
    /// </summary>
    [Fact]
    public void TwoInstancesOfOneCellKeepTheirOwnFiles()
    {
        var netlist = Elaborate("File", "\"unused-default.mdl\"", $"\"{Path1}\"", $"\"{Path2}\"");

        Assert.Equal(Path1, FileOf(netlist, "X1.FET1"));
        Assert.Equal(Path2, FileOf(netlist, "X2.FET1"));
    }

    /// <summary>The cell's own declared default is used when the instance overrides nothing — the
    /// forwarding resolves against the cell's scope, which is where the default lives.</summary>
    [Fact]
    public void TheCellsDeclaredDefaultIsUsedWhenNothingOverridesIt()
    {
        ExternalDeviceRegistry.Register(new DeviceWorkerProvider("AcmeKit", new FakeDeviceWorker()));

        var cell = new Cell("acme_fet");
        cell.Ports.AddRange(["g", "d", "s", "th"]);
        cell.Parameters.Add(new ParameterDeclaration("File", $"\"{Path2}\"", null));
        cell.Instances.Add(new Instance("FET1", "ExtDevice", ["g", "d", "s", "th"],
        [
            new ParameterAssignment("Provider", "AcmeKit"),
            new ParameterAssignment("Type",     FakeDeviceWorker.NonlinearType),
            new ParameterAssignment("File",     "File"),
        ]));

        var lib = new Library("lib");
        lib.Cells.Add(cell);

        var tb = new TestBench("tb");
        tb.Instances.Add(new Instance("X1", "acme_fet", ["g", "d", "0", "th"], []));

        Assert.Equal(Path2, FileOf(new Elaborator(lib).Elaborate(tb), "X1.FET1"));
    }

    /// <summary>
    /// The numeric parameters beside it were already correct and must stay so. Stated because they
    /// are what made the reported failure confusing — the model was clearly being given SOME of what
    /// the design said, which reads as a model problem rather than a forwarding one.
    /// </summary>
    [Fact]
    public void ANonSelectorParameterStillResolvesAsBefore()
    {
        var netlist = Elaborate("File", "\"d.mdl\"", "\"/a.mdl\"", "\"/b.mdl\"");

        Assert.Equal(1e-6, netlist.Components.Single(c => c.InstancePath == "X1.FET1").Parameters["Scale"].AsReal());
        Assert.Equal(2e-6, netlist.Components.Single(c => c.InstancePath == "X2.FET1").Parameters["Scale"].AsReal());
    }

    // ── What must NOT change ──────────────────────────────────────────────────

    /// <summary>
    /// A literal path is quoted, and a quoted value is never looked up — not even when something in
    /// scope happens to be spelled the same. This is the whole reason the rule reads the quoting
    /// rather than trying a lookup first.
    /// </summary>
    [Fact]
    public void AQuotedLiteralIsNeverLookedUp_EvenWhenSomethingInScopeSharesItsSpelling()
    {
        ExternalDeviceRegistry.Register(new DeviceWorkerProvider("AcmeKit", new FakeDeviceWorker()));

        var cell = new Cell("acme_fet");
        cell.Ports.AddRange(["g", "d", "s", "th"]);
        cell.Parameters.Add(new ParameterDeclaration("File", "\"/somewhere/else.mdl\"", null));
        cell.Instances.Add(new Instance("FET1", "ExtDevice", ["g", "d", "s", "th"],
        [
            new ParameterAssignment("Provider", "AcmeKit"),
            new ParameterAssignment("Type",     FakeDeviceWorker.NonlinearType),
            // QUOTED, and spelled exactly like the cell parameter above.
            new ParameterAssignment("File",     "\"File\""),
        ]));

        var lib = new Library("lib");
        lib.Cells.Add(cell);

        var tb = new TestBench("tb");
        tb.Instances.Add(new Instance("X1", "acme_fet", ["g", "d", "0", "th"], []));

        Assert.Equal("File", FileOf(new Elaborator(lib).Elaborate(tb), "X1.FET1"));
    }

    /// <summary>An unquoted path is left exactly as it was — it is not a bare name, so nothing looks
    /// it up, and the parser is never handed it.</summary>
    [Fact]
    public void AnUnquotedPathIsStillTakenVerbatim()
    {
        ExternalDeviceRegistry.Register(new DeviceWorkerProvider("AcmeKit", new FakeDeviceWorker()));

        var inst = new Instance("X1", "ExtDevice", ["a", "b", "c", "d"],
        [
            new ParameterAssignment("Provider", "AcmeKit"),
            new ParameterAssignment("Type",     FakeDeviceWorker.NonlinearType),
            new ParameterAssignment("File",     Path1),
        ]);

        var tb = new TestBench("tb");
        tb.Instances.Add(inst);

        Assert.Equal(Path1, FileOf(new Elaborator(new Library("lib")).Elaborate(tb), "X1"));
    }

    /// <summary>A bare name nothing binds is a name the provider owns — an enum value, a model name
    /// inside a library — and stays verbatim rather than becoming an error.</summary>
    [Fact]
    public void ABareNameNothingBindsStaysVerbatim()
    {
        ExternalDeviceRegistry.Register(new DeviceWorkerProvider("AcmeKit", new FakeDeviceWorker()));

        var tb = new TestBench("tb");
        tb.Instances.Add(new Instance("X1", "ExtDevice", ["a", "b", "c", "d"],
        [
            new ParameterAssignment("Provider", "AcmeKit"),
            new ParameterAssignment("Type",     FakeDeviceWorker.NonlinearType),
            new ParameterAssignment("Note",     "nmos_hv"),
        ]));

        var netlist = new Elaborator(new Library("lib")).Elaborate(tb);
        Assert.Equal("nmos_hv",
            netlist.Components.Single(c => c.InstancePath == "X1").Parameters["Note"].AsString());
    }

    /// <summary><c>Provider</c> and <c>Type</c> follow the same rule, so a cell can parameterise
    /// which formulation of a part it builds — and a bare literal name still selects.</summary>
    [Fact]
    public void ProviderAndTypeFollowTheSameRule()
    {
        ExternalDeviceRegistry.Register(new DeviceWorkerProvider("AcmeKit", new FakeDeviceWorker()));

        var cell = new Cell("acme_fet");
        cell.Ports.AddRange(["g", "d", "s", "th"]);
        cell.Parameters.Add(new ParameterDeclaration("Formulation", $"\"{FakeDeviceWorker.NonlinearType}\"", null));
        cell.Instances.Add(new Instance("FET1", "ExtDevice", ["g", "d", "s", "th"],
        [
            new ParameterAssignment("Provider", "AcmeKit"),
            new ParameterAssignment("Type",     "Formulation"),
        ]));

        var lib = new Library("lib");
        lib.Cells.Add(cell);

        var tb = new TestBench("tb");
        tb.Instances.Add(new Instance("X1", "acme_fet", ["g", "d", "0", "th"], []));

        var netlist = new Elaborator(lib).Elaborate(tb);
        Assert.Equal(FakeDeviceWorker.NonlinearType,
            netlist.Components.Single(c => c.InstancePath == "X1.FET1").Parameters["Type"].AsString());
    }
}
