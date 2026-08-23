using System.IO.Compression;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Reported: the .zip door in File ▸ Import ▸ PDK does not work. It did not: an archive was read in
/// place, which tells you what a kit CONTAINS and nothing else — the installer's own <c>haveRoot</c>
/// branch skips artwork, netlist discovery, compiled models, settings and the manifest when the
/// root is not a directory, and the reference then recorded the <c>.zip</c> as the kit's location, so
/// the next workspace open reported the kit folder missing.
///
/// <para>The archive is now unpacked into the workspace first and imported as the folder it became.
/// What these pin is that door's two structural traps: where the kit ends up, and the extra folder
/// level nearly every real archive has.</para>
/// </summary>
public sealed class KitArchiveImportTests : IDisposable
{
    private readonly string _root;

    public KitArchiveImportTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-kitzip-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>Builds a .zip whose entries are exactly <paramref name="entries"/>.</summary>
    private string MakeArchive(string name, params string[] entries)
    {
        string zipPath = Path.Combine(_root, name + ".zip");
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (string e in entries)
        {
            var entry = zip.CreateEntry(e);
            using var w = new StreamWriter(entry.Open());
            w.Write("x");
        }
        return zipPath;
    }

    private string Workspace()
    {
        string ws = Path.Combine(_root, "ws");
        Directory.CreateDirectory(ws);
        File.WriteAllText(Path.Combine(ws, ".cws"), "{}");
        return ws;
    }

    [Fact]
    public void TheKitLandsInsideTheWorkspace_SoItTravelsWithIt()
    {
        string ws  = Workspace();
        string zip = MakeArchive("acme-kit", "acme-kit/cells/a.txt");

        string kitDir = KitArchive.ExtractInto(zip, ws, overwrite: false);

        Assert.StartsWith(Path.GetFullPath(ws), Path.GetFullPath(kitDir), StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(kitDir));
        Assert.True(File.Exists(Path.Combine(kitDir, "cells", "a.txt")));
    }

    [Fact]
    public void AnArchiveWrappedInItsOwnFolder_ResolvesToTheKit_NotTheWrapper()
    {
        // How a kit is nearly always packed. Returning the wrapper names the kit right by luck and
        // then resolves every asset path one level too high.
        string ws  = Workspace();
        string zip = MakeArchive("acme-kit", "acme-kit/cells/a.txt", "acme-kit/tech/stack.txt");

        string kitDir = KitArchive.ExtractInto(zip, ws, overwrite: false);

        Assert.Equal("acme-kit", Path.GetFileName(kitDir));
        Assert.True(Directory.Exists(Path.Combine(kitDir, "cells")));
        Assert.True(Directory.Exists(Path.Combine(kitDir, "tech")));
    }

    [Fact]
    public void AnArchivePackedFlat_IsTheKitItself()
    {
        string ws  = Workspace();
        string zip = MakeArchive("flat-kit", "a.txt", "b/c.txt");

        string kitDir = KitArchive.ExtractInto(zip, ws, overwrite: false);

        Assert.Equal(KitArchive.DestinationFor(zip, ws), kitDir);
        Assert.True(File.Exists(Path.Combine(kitDir, "a.txt")));
    }

    [Fact]
    public void TwoTopLevelFolders_AreNotUnwrapped()
    {
        string ws  = Workspace();
        string zip = MakeArchive("two-kit", "left/a.txt", "right/b.txt");

        string kitDir = KitArchive.ExtractInto(zip, ws, overwrite: false);

        Assert.Equal(KitArchive.DestinationFor(zip, ws), kitDir);
    }

    [Fact]
    public void ASingleTopLevelFolderThatIsNotTheWrapper_IsNotDescendedInto()
    {
        // The trap "one directory and no files" walks straight into: a kit whose whole top level is
        // one cells/ folder. Descending returns something that is not the kit, and every path
        // resolved from it is wrong in a way nothing downstream can detect.
        string ws  = Workspace();
        string zip = MakeArchive("acme-kit", "cells/a.txt", "cells/b.txt");

        string kitDir = KitArchive.ExtractInto(zip, ws, overwrite: false);

        Assert.Equal(KitArchive.DestinationFor(zip, ws), kitDir);
        Assert.True(File.Exists(Path.Combine(kitDir, "cells", "a.txt")));
    }

    [Fact]
    public void AnExistingUnpackedKit_IsNotReplacedWithoutBeingAsked()
    {
        string ws  = Workspace();
        string zip = MakeArchive("acme-kit", "acme-kit/cells/a.txt");

        KitArchive.ExtractInto(zip, ws, overwrite: false);

        // Someone edited the unpacked kit — a hand-written manifest is exactly what lives there.
        string dest = KitArchive.DestinationFor(zip, ws);
        File.WriteAllText(Path.Combine(dest, "acme-kit", "hand-written.json"), "{}");

        Assert.Throws<IOException>(() => KitArchive.ExtractInto(zip, ws, overwrite: false));
        Assert.True(File.Exists(Path.Combine(dest, "acme-kit", "hand-written.json")));

        // And is replaced outright once it has been.
        KitArchive.ExtractInto(zip, ws, overwrite: true);
        Assert.False(File.Exists(Path.Combine(dest, "acme-kit", "hand-written.json")));
    }

    [Fact]
    public void OnlyAZipIsTreatedAsAnArchive()
    {
        Assert.True(KitArchive.IsArchive("/kits/thing.zip"));
        Assert.True(KitArchive.IsArchive("/kits/thing.ZIP"));
        Assert.False(KitArchive.IsArchive("/kits/thing"));
        Assert.False(KitArchive.IsArchive(null));
    }
}
