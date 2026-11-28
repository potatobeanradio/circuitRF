using CircuitRF.Diagnostics;

namespace CircuitRF.Design.PCellDesigner;

/// <summary>
/// The PCell Designer's refusals a user reads, as coded diagnostics (tests/Firewall.Tests/user-facing-text-allowlist.txt says
/// why). The text is what each refusal said before; each is thrown as an exception of the type its callers already catch,
/// whose message is the diagnostic rendered.
/// </summary>
public static class PCellDesignerDiagnostics
{
    /// <summary>Stretch by Parameter: the parameter the vertices are to follow has no value now.</summary>
    public static Diagnostic StretchNotEvaluable(string parameter, string reason) => Diagnostic.Create(
        "pcell.stretch.not-evaluable", DiagnosticSeverity.Error,
        "{parameter} does not evaluate: {reason}", ("parameter", parameter), ("reason", reason));

    /// <summary>A placed designed cell whose folder has gone since the workspace was read.</summary>
    public static Diagnostic CellMissing(string generatorId, string root) => Diagnostic.Create(
        "pcell.resolver.cell-missing", DiagnosticSeverity.Error,
        "{generatorId}: its designed cell is no longer found under {root}.", ("generatorId", generatorId), ("root", root));

    public static Diagnostic NoTechnology(string generatorId) => Diagnostic.Create(
        "pcell.resolver.no-technology", DiagnosticSeverity.Error,
        "{generatorId}: a designed cell is drawn on a technology, and none resolved.", ("generatorId", generatorId));

    public static Diagnostic DatabaseUnit(string cell, int dbuPerMicron, int expected) => Diagnostic.Create(
        "pcell.resolver.database-unit", DiagnosticSeverity.Error,
        "{cell}'s layout is at {dbuPerMicron} database units per micron; a designed cell is drawn at {expected}.",
        ("cell", cell), ("dbuPerMicron", dbuPerMicron), ("expected", expected));

    /// <summary>An operation names a layer the cell's technology does not have.</summary>
    public static Diagnostic NoLayer(string layer) => Diagnostic.Create(
        "pcell.operations.no-layer", DiagnosticSeverity.Error,
        "the technology has no layer {layer}", ("layer", layer));

    /// <summary>A new designed cell is never created over a folder already there.</summary>
    public static Diagnostic FolderExists(string cellName, string parentDir) => Diagnostic.Create(
        "pcell.new.folder-exists", DiagnosticSeverity.Error,
        "A folder named {cellName} already exists in {parentDir}; choose another name.",
        ("cellName", cellName), ("parentDir", parentDir));
}

/// <summary>A PCell Designer refusal, carrying its <see cref="Diagnostic"/>; its message is the diagnostic rendered.</summary>
public sealed class PCellDesignerRefusalException(Diagnostic diagnostic) : InvalidOperationException(diagnostic.Render())
{
    public Diagnostic Diagnostic { get; } = diagnostic;
}

/// <summary>As <see cref="PCellDesignerRefusalException"/>, for a refused argument; the message ends as an
/// <see cref="ArgumentException"/>'s does, with the parameter's name.</summary>
public sealed class PCellDesignerArgumentException(Diagnostic diagnostic, string paramName, Exception? inner = null)
    : ArgumentException(diagnostic.Render(), paramName, inner)
{
    public Diagnostic Diagnostic { get; } = diagnostic;
}

/// <summary>As <see cref="PCellDesignerRefusalException"/>, for a refusal over files.</summary>
public sealed class PCellDesignerIOException(Diagnostic diagnostic) : IOException(diagnostic.Render())
{
    public Diagnostic Diagnostic { get; } = diagnostic;
}
