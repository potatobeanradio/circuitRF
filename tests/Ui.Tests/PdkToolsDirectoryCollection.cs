using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Serializes every test class that imports a PDK, because <see
/// cref="CircuitRF.Core.Devices.External.DeviceWorkerManifest.ToolsDirectory"/> is PROCESS-WIDE
/// mutable state that the import path reads — to find circuitRF's own worker, and (since the alias
/// map landed) circuitRF's own node-alias map.
///
/// <para><b>Why a collection rather than leaving them parallel.</b> One class points that static at a
/// folder holding an alias map, on purpose, to drive the fallback branch. Every OTHER class asserts
/// exact argument and share counts on the manifest it just wrote — so if the two overlap, the folder
/// one test installed is read by another's import and its osx entry grows a share it never asked for.
/// The failure is intermittent and reads as a flake rather than as shared state.</para>
///
/// <para>This is a bare marker: xUnit serializes the classes carrying it relative to each other and
/// still parallelizes them against every other collection. It is the same mechanism this repo
/// already uses for the Skia typeface override and the microstrip cache — the standing rule is that
/// a process-wide static a test mutates needs one of these, not a blanket parallelism switch.</para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class PdkToolsDirectoryCollection
{
    public const string Name = "pdk-tools-directory";
}
