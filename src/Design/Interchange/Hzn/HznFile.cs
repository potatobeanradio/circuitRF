// HZN-1 — a Hertzon project file (`.hzn`), read and written without losing anything.
//
// Every later HZN brief reads a `.hzn` through this class, and briefs 4 and 5 write one. The
// guarantee they all rest on is made here once: what circuitRF does not change comes back byte for
// byte (HznJson.cs says how), the version gate refuses what circuitRF cannot read by name, and a
// file Hertzon saved while circuitRF had it open is never overwritten.
//
// Nothing here is ported from Hertzon (brief-hzn-0 D1): the format is read from the files
// themselves, Hertzon's documentation and black-box runs; src/Design/RESOLVED.md §HZN records each
// meaning relied on.

using System.Security.Cryptography;
using System.Text;
using CircuitRF.Design.Cells;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Interchange.Hzn;

/// <summary>What <see cref="HznFile.Read"/> produced: the file, or the refusal.</summary>
public sealed record HznReadResult(HznFile? File, Diagnostic? Refusal)
{
    public bool Ok => File is not null;
}

/// <summary>What <see cref="HznFile.Save"/> did.</summary>
/// <param name="Saved">True unless refused.</param>
/// <param name="Wrote">False when the bytes on disk were already the bytes to write, so nothing was touched.</param>
/// <param name="BackupPath">The backup this save wrote first, if it wrote one (R-hzn1-4).</param>
/// <param name="Refusal">Why nothing was written.</param>
public sealed record HznSaveResult(bool Saved, bool Wrote, string? BackupPath, Diagnostic? Refusal);

/// <summary>A <c>.hzn</c> held open: its JSON tree, a typed view of it, and the stamp of the bytes
/// it was read from.</summary>
public sealed class HznFile
{
    /// <summary>The only <c>format</c> a Hertzon project states.</summary>
    public const string FormatName = "hertzon-project";

    /// <summary>The format version circuitRF reads (R-hzn1-2). A write keeps the version it read.</summary>
    public const int ReadableVersion = 2;

    private long _length;
    private DateTime _modifiedUtc;
    private byte[] _sha256;
    private bool _backedUp;

    private HznFile(string path, HznJsonDocument document, byte[] bytes, DateTime modifiedUtc)
    {
        Path = path;
        Document = document;
        Project = new HznProject((HznJsonObject)document.Root);
        _length = bytes.Length;
        _modifiedUtc = modifiedUtc;
        _sha256 = SHA256.HashData(bytes);
    }

    public string Path { get; }

    /// <summary>The whole file, every byte of it accounted for.</summary>
    public HznJsonDocument Document { get; }

    /// <summary>The typed view over <see cref="Document"/>; reads and writes the same tree.</summary>
    public HznProject Project { get; }

    /// <summary>
    /// R-hzn1-4: when on, the first <see cref="Save"/> of this instance copies the file as it was to
    /// <c>&lt;name&gt;.hzn.bak</c> first, replacing any earlier backup. Off until the owner answers
    /// brief-hzn-0's Q1.
    /// </summary>
    public bool BackupOnFirstSave { get; set; }

    /// <summary>Where the backup goes.</summary>
    public string BackupPath => Path + ".bak";

    /// <summary>Reads and gates a <c>.hzn</c>. Records the file's length, modification time and
    /// SHA-256 so <see cref="Save"/> can tell whether it changed on disk since.</summary>
    public static HznReadResult Read(string path)
    {
        byte[] bytes;
        DateTime modified;
        try
        {
            bytes = File.ReadAllBytes(path);
            modified = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return new(null, HznDiagnostics.Unreadable(path, e.Message));
        }

        HznJsonDocument document;
        try
        {
            document = HznJsonDocument.Parse(bytes);
        }
        catch (DecoderFallbackException)
        {
            return new(null, HznDiagnostics.NotUtf8(path));
        }
        catch (HznJsonSyntaxException e)
        {
            return new(null, HznDiagnostics.NotJson(path, e.Reason, e.Line, e.Column));
        }

        if (Gate(path, document) is { } refusal) return new(null, refusal);
        return new(new HznFile(path, document, bytes, modified), null);
    }

    /// <summary>R-hzn1-2 — a Hertzon project, at the version circuitRF reads.</summary>
    private static Diagnostic? Gate(string path, HznJsonDocument document)
    {
        if (document.Root is not HznJsonObject root)
            return HznDiagnostics.NotHertzonProject(path, null);

        string? format = root.GetString("format");
        if (!string.Equals(format, FormatName, StringComparison.Ordinal))
            return HznDiagnostics.NotHertzonProject(path, format);

        if (root["version"] is not HznJsonScalar v || !v.TryGetInt64(out long version) || version < 1)
            return HznDiagnostics.UnreadableVersion(path);
        if (version > ReadableVersion) return HznDiagnostics.NewerVersion(path, version);
        if (version < ReadableVersion) return HznDiagnostics.OlderVersion(path, version);
        return null;
    }

    /// <summary>The file as it would be written now.</summary>
    public byte[] ToBytes() => Document.ToBytes();

    /// <summary>
    /// Writes the tree back to <see cref="Path"/>. Refused, writing nothing, when the file on disk is
    /// not the one that was read (R-hzn1-3); a reload is the caller's decision. The write is atomic: a
    /// temp file in the same folder, then a replace.
    /// </summary>
    public HznSaveResult Save()
    {
        byte[] onDisk;
        try
        {
            if (!File.Exists(Path)) return Refused(HznDiagnostics.GoneFromDisk(Path));
            var info = new FileInfo(Path);
            if (info.Length != _length || info.LastWriteTimeUtc != _modifiedUtc)
                return Refused(HznDiagnostics.ChangedOnDisk(Path));
            onDisk = File.ReadAllBytes(Path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Refused(HznDiagnostics.SaveFailed(Path, e.Message));
        }
        if (!SHA256.HashData(onDisk).AsSpan().SequenceEqual(_sha256))
            return Refused(HznDiagnostics.ChangedOnDisk(Path));

        byte[] bytes;
        try
        {
            bytes = ToBytes();
        }
        catch (EncoderFallbackException e)
        {
            return Refused(HznDiagnostics.SaveFailed(Path, e.Message));
        }
        if (bytes.AsSpan().SequenceEqual(onDisk)) return new(true, false, null, null);

        string? backup = null;
        if (BackupOnFirstSave && !_backedUp)
        {
            try
            {
                AtomicFile.WriteAllBytes(BackupPath, onDisk);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Refused(HznDiagnostics.BackupFailed(Path, BackupPath, e.Message));
            }
            _backedUp = true;
            backup = BackupPath;
        }

        try
        {
            AtomicFile.WriteAllBytes(Path, bytes);
            _modifiedUtc = File.GetLastWriteTimeUtc(Path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(false, false, backup, HznDiagnostics.SaveFailed(Path, e.Message));
        }
        _length = bytes.Length;
        _sha256 = SHA256.HashData(bytes);
        return new(true, true, backup, null);
    }

    private static HznSaveResult Refused(Diagnostic why) => new(false, false, null, why);
}
