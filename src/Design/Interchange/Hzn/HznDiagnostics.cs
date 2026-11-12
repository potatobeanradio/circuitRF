using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Interchange.Hzn;

/// <summary>
/// Every refusal <see cref="HznFile"/> returns (HZN-1). Returned, never posted: the GUI raises them
/// as Messages, the CLI writes them to stderr. The ids are the durable part.
/// </summary>
public static class HznDiagnostics
{
    public static Diagnostic Unreadable(string file, string reason) => Diagnostic.Create(
        "hzn.file.unreadable",
        DiagnosticSeverity.Error,
        "'{file}' could not be read: {reason}",
        ("file", file), ("reason", reason));

    public static Diagnostic NotUtf8(string file) => Diagnostic.Create(
        "hzn.file.not-utf8",
        DiagnosticSeverity.Error,
        "'{file}' is not a Hertzon project: it is not UTF-8 text.",
        ("file", file));

    public static Diagnostic NotJson(string file, string reason, int line, int column) => Diagnostic.Create(
        "hzn.file.not-json",
        DiagnosticSeverity.Error,
        "'{file}' is not a Hertzon project: it is not valid JSON ({reason}, line {line}, column {column}).",
        ("file", file), ("reason", reason), ("line", line), ("column", column));

    /// <summary>R-hzn1-2 — <c>format</c> is absent or is not <c>hertzon-project</c>.</summary>
    public static Diagnostic NotHertzonProject(string file, string? format) => Diagnostic.Create(
        "hzn.format.not-hertzon",
        DiagnosticSeverity.Error,
        "'{file}' is not a Hertzon project: its format is {found}, not 'hertzon-project'.",
        ("file", file), ("found", format is null ? "not stated" : $"'{format}'"));

    /// <summary>R-hzn1-2 — a version above the one circuitRF reads.</summary>
    public static Diagnostic NewerVersion(string file, long version) => Diagnostic.Create(
        "hzn.version.newer",
        DiagnosticSeverity.Error,
        "'{file}' was written by a newer Hertzon, format version {version}; circuitRF reads version {readable}.",
        ("file", file), ("version", version), ("readable", HznFile.ReadableVersion));

    /// <summary>R-hzn1-2 — version 1, which Hertzon itself converts when it opens it.</summary>
    public static Diagnostic OlderVersion(string file, long version) => Diagnostic.Create(
        "hzn.version.older",
        DiagnosticSeverity.Error,
        "'{file}' is a Hertzon format version {version} project; circuitRF reads version {readable}. " +
        "Open it in Hertzon and save it once, which converts it.",
        ("file", file), ("version", version), ("readable", HznFile.ReadableVersion));

    /// <summary>R-hzn1-2 — <c>version</c> is absent or is not a whole number.</summary>
    public static Diagnostic UnreadableVersion(string file) => Diagnostic.Create(
        "hzn.version.unreadable",
        DiagnosticSeverity.Error,
        "'{file}' states no format version circuitRF can read; circuitRF reads version {readable}.",
        ("file", file), ("readable", HznFile.ReadableVersion));

    /// <summary>R-hzn1-3 — the file on disk is not the one that was read.</summary>
    public static Diagnostic ChangedOnDisk(string file) => Diagnostic.Create(
        "hzn.save.changed-on-disk",
        DiagnosticSeverity.Error,
        "'{file}' was not saved: it changed on disk since it was opened (Hertzon may have saved it). Nothing was written.",
        ("file", file));

    /// <summary>R-hzn1-3 — the file that was read is gone.</summary>
    public static Diagnostic GoneFromDisk(string file) => Diagnostic.Create(
        "hzn.save.gone",
        DiagnosticSeverity.Error,
        "'{file}' was not saved: it is no longer on disk where it was opened. Nothing was written.",
        ("file", file));

    public static Diagnostic SaveFailed(string file, string reason) => Diagnostic.Create(
        "hzn.save.failed",
        DiagnosticSeverity.Error,
        "'{file}' could not be saved: {reason}",
        ("file", file), ("reason", reason));

    /// <summary>R-hzn1-4 — the backup could not be written, so the save did not start.</summary>
    public static Diagnostic BackupFailed(string file, string backup, string reason) => Diagnostic.Create(
        "hzn.save.backup-failed",
        DiagnosticSeverity.Error,
        "'{file}' was not saved: its backup '{backup}' could not be written ({reason}). Nothing was written.",
        ("file", file), ("backup", backup), ("reason", reason));
}
