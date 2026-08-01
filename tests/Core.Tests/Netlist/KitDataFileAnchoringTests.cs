using System;
using System.IO;
using System.Linq;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Core.Tests.Netlist;

/// <summary>
/// A kit names its data files by a path relative to its own data folder, which the simulator it
/// targets puts on a search path. circuitRF has no such search path, so the file has to be anchored
/// where it is read — while the file it came from is still known.
///
/// <para><b>The failure this prevents.</b> Left relative, the path survives into the generated
/// <c>.cnl</c> and is finally resolved against THAT file's folder — the workspace — so the run fails
/// naming a file in a directory the kit has nothing to do with, while the file sits untouched in the
/// kit.</para>
///
/// <para>Fixtures are synthetic: this is a format reader, and the repo commits no kit data.</para>
/// </summary>
public sealed class KitDataFileAnchoringTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-kit-" + Guid.NewGuid().ToString("N")[..8]);

    private string Models => Path.Combine(_root, "circuit", "models");
    private string Data   => Path.Combine(_root, "circuit", "data", "PartData");

    public KitDataFileAnchoringTests()
    {
        Directory.CreateDirectory(Models);
        Directory.CreateDirectory(Data);
        File.WriteAllText(Path.Combine(Data, "network.s2p"), "");
        File.WriteAllText(Path.Combine(Data, "model.mdl"),   "");
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    /// <summary>Writes a netlist into the kit's models folder and reads it back the way a run does.</summary>
    private KitNetlistResult ReadNetlist(string body)
    {
        string path = Path.Combine(Models, "part.net");
        File.WriteAllText(path, body);
        return KitNetlistReader.ReadFile(path);
    }

    private static string FileParameterOf(KitNetlistResult r, string instance)
        => r.Library.Cells
            .SelectMany(c => c.Instances)
            .Single(i => i.InstanceName == instance)
            .Overrides.Single(o => o.Name == "File").Expression;

    [Fact]
    public void ADataFileInASiblingFolder_IsAnchoredToWhereItActuallyIs()
    {
        // The layout a kit uses: netlists in one folder, data in a sibling. The netlist's own
        // folder is NOT the answer, which is why this is a look around rather than one anchor.
        var r = ReadNetlist("""
            define PART ( a b )
              S2P:SNP1  a b  File="PartData/network.s2p"
            end PART
            """);

        Assert.Equal($"\"{Path.Combine(Data, "network.s2p")}\"", FileParameterOf(r, "SNP1"));
    }

    [Fact]
    public void TheBackslashFormAKitActuallyWrites_AnchorsToo()
    {
        // A kit spells a folder `PartData\`, which is a separator here and not an escape.
        var r = ReadNetlist("""
            define PART ( a b )
              parameters DataPath="PartData\"
              S2P:SNP1  a b  File=strcat(DataPath,"network.s2p")
            end PART
            """);

        Assert.Equal($"\"{Path.Combine(Data, "network.s2p")}\"", FileParameterOf(r, "SNP1"));
    }

    [Fact]
    public void AFileBesideTheNetlist_IsAnchoredAsWell()
    {
        File.WriteAllText(Path.Combine(Models, "local.mdl"), "");

        var r = ReadNetlist("""
            define PART ( a b )
              FET:T1  a b  File="local.mdl"
            end PART
            """);

        Assert.Equal($"\"{Path.Combine(Models, "local.mdl")}\"", FileParameterOf(r, "T1"));
    }

    [Fact]
    public void EveryFileParameterIsAnchored_NotJustTheTouchstoneOne()
    {
        // A compiled model's data file is named by whatever keyword the kit likes, so anchoring a
        // list of known parameter names would silently cover some files and not others.
        var r = ReadNetlist("""
            define PART ( a b )
              S2P:SNP1  a b  File="PartData/network.s2p"
              FET:T1    a b  File="PartData/model.mdl"
            end PART
            """);

        Assert.Equal($"\"{Path.Combine(Data, "model.mdl")}\"", FileParameterOf(r, "T1"));
    }

    [Fact]
    public void APathNamingNoRealFile_IsLeftExactlyAsTheKitWroteIt()
    {
        // Rewriting only what is found is what makes this safe to try on every value. A guess here
        // would replace a path the kit meant with one circuitRF invented.
        var r = ReadNetlist("""
            define PART ( a b )
              S2P:SNP1  a b  File="PartData/missing.s2p"
            end PART
            """);

        Assert.Equal("\"PartData/missing.s2p\"", FileParameterOf(r, "SNP1"));
    }

    [Fact]
    public void AnAbsolutePathIsLeftAlone()
    {
        string absolute = Path.Combine(Data, "network.s2p");

        var r = ReadNetlist($"""
            define PART ( a b )
              S2P:SNP1  a b  File="{absolute}"
            end PART
            """);

        Assert.Equal($"\"{absolute}\"", FileParameterOf(r, "SNP1"));
    }

    [Fact]
    public void AValueThatIsNotAPath_IsNeverTouched()
    {
        var r = ReadNetlist("""
            define PART ( a b )
              FET:T1  a b  FS="PROC1"  Fingers=26  File="PartData/model.mdl"
            end PART
            """);

        var t1 = r.Library.Cells.SelectMany(c => c.Instances).Single(i => i.InstanceName == "T1");

        Assert.Equal("\"PROC1\"", t1.Overrides.Single(o => o.Name == "FS").Expression);
        Assert.Equal("26",        t1.Overrides.Single(o => o.Name == "Fingers").Expression);
    }

    [Fact]
    public void AnAnchoredPathStaysQuoted()
    {
        // Everything the reader produces is later EVALUATED, and a bare path is not an expression —
        // a leading '/' fails the parser at position 0.
        var r = ReadNetlist("""
            define PART ( a b )
              S2P:SNP1  a b  File="PartData/network.s2p"
            end PART
            """);

        string value = FileParameterOf(r, "SNP1");
        Assert.StartsWith("\"", value);
        Assert.EndsWith("\"", value);
    }

    [Fact]
    public void ReadingFromTextAnchorsNothing()
    {
        // There is no file to be relative TO. Silently resolving against the process's working
        // directory would make the result depend on where circuitRF happened to be started.
        var r = KitNetlistReader.Read("""
            define PART ( a b )
              S2P:SNP1  a b  File="PartData/network.s2p"
            end PART
            """);

        Assert.Equal("\"PartData/network.s2p\"", FileParameterOf(r, "SNP1"));
    }

    [Fact]
    public void TheSearchIsBounded_AndDoesNotClimbOutOfTheKit()
    {
        // Each ancestor's children are listed, so climbing one level too far starts listing a
        // directory that has nothing to do with the kit — and a value that happens to match a file
        // in there would resolve to something the kit never named.
        string far = Path.Combine(Path.GetTempPath(), "crf-kit-outside-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(far);
        try
        {
            File.WriteAllText(Path.Combine(far, "faraway.s2p"), "");

            var r = ReadNetlist("""
                define PART ( a b )
                  S2P:SNP1  a b  File="faraway.s2p"
                end PART
                """);

            Assert.Equal("\"faraway.s2p\"", FileParameterOf(r, "SNP1"));
        }
        finally { try { Directory.Delete(far, true); } catch { } }
    }
}
