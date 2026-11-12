using System.Globalization;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.ThreeD;

/// <summary>
/// What the <c>.c3d</c> reader refuses and what its validator finds, as coded diagnostics
/// (<c>EmDiagnostics</c> is the pattern). Returned, never posted: <c>check</c> writes them to stderr and
/// the editor, when it exists, to the Messages window.
/// </summary>
public static class C3dDiagnostics
{
    // ── Refusals: the file cannot be read at all ─────────────────────────────────────────────────

    public static Diagnostic Unreadable(string reason) => Diagnostic.Create(
        "c3d.read.unreadable", DiagnosticSeverity.Error,
        "The 3D view could not be read: {reason}", ("reason", reason));

    public static Diagnostic NotAnObject() => new(
        "c3d.read.not-an-object", DiagnosticSeverity.Error,
        "The 3D view is not a JSON object, so it is not a circuitRF 3D view.");

    public static Diagnostic NewerFormat(int version, int current) => Diagnostic.Create(
        "c3d.read.newer-format", DiagnosticSeverity.Error,
        "This 3D view is format version {version}, newer than this build reads ({current}). Update the application.",
        ("version", version), ("current", current));

    /// <summary>R-em3d41-2d: an unknown <c>$type</c> is named, never a crash.</summary>
    public static Diagnostic UnknownKinds(string which, string known) => Diagnostic.Create(
        "c3d.read.unknown-kind", DiagnosticSeverity.Error,
        "This build cannot read {which}. It reads: {known}. The document was probably written by a later build.",
        ("which", which), ("known", known));

    public static Diagnostic MissingKind(int index) => Diagnostic.Create(
        "c3d.read.missing-kind", DiagnosticSeverity.Error,
        "Object {index} has no \"$type\", so there is no telling what kind of object it is.",
        ("index", index));

    /// <summary>brief-em3d-64 — an operation's operand with no <c>$type</c>.</summary>
    public static Diagnostic MissingOperandKind(string where) => Diagnostic.Create(
        "c3d.read.missing-kind", DiagnosticSeverity.Error,
        "{where} has no \"$type\", so there is no telling what kind of object it is.",
        ("where", Capital(where)));

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>R-em3d41-2d / brief-em3d-51: a bare string where a number belongs. A dimension's expression is an object
    /// carrying its unit; an outline, a hole or a polyhedron's vertices hold numbers only (brief-em3d-132 binds a wire's
    /// and a polyline's points).</summary>
    public static Diagnostic ExpressionNotYet(string text) => Diagnostic.Create(
        "c3d.read.expression", DiagnosticSeverity.Error,
        "\"{text}\" is written where a number belongs. Outline, Holes and Vertices hold numbers only; " +
        "a named dimension, or a wire's or a polyline's point, may hold an expression, written {example} with its unit.",
        ("text", text), ("example", Example(text)));

    /// <summary>brief-em3d-132 R-em3d132-2 — a polyline with Points3 ignores its Points, so an expression there would be dropped.</summary>
    public static Diagnostic IgnoredPointExpression(string polyline, string key) => Diagnostic.Create(
        "c3d.read.ignored-point-expression", DiagnosticSeverity.Error,
        "Polyline '{polyline}' has Points3, which is the polyline, so its Points are ignored; {key} holds an expression that " +
        "would be dropped. Write it in Points3, or delete Points.",
        ("polyline", polyline), ("key", key));

    /// <summary>brief-em3d-51 R-em3d51-1a — an expression written as a bare string in a dimension field.</summary>
    public static Diagnostic ExpressionAsString(string text) => Diagnostic.Create(
        "c3d.read.expression", DiagnosticSeverity.Error,
        "\"{text}\" is written as a bare string. An expression in a dimension is an object that carries the unit it was " +
        "typed in — {example} — so that changing the display unit never changes what it means.",
        ("text", text), ("example", Example(text)));

    /// <summary>brief-em3d-51 — an expression object with a key it does not have, or no text.</summary>
    public static Diagnostic ExpressionShape(string key) => Diagnostic.Create(
        "c3d.read.expression-shape", DiagnosticSeverity.Error,
        "An expression in a dimension is written {example}; '{key}' is not one of its keys, or its Expr is empty.",
        ("key", key), ("example", Example("…")));

    /// <summary>brief-em3d-86 R-em3d86-4 — a probe's Face that is neither a face nor a list of faces.</summary>
    public static Diagnostic FaceListShape(string found) => Diagnostic.Create(
        "c3d.read.face-list", DiagnosticSeverity.Error,
        "A probe's Face is \"object/face\", or a list of them read as one place; {found} is neither.",
        ("found", found));

    private static string Example(string text) => "{ \"Expr\": \"" + text + "\", \"Unit\": \"Mil\" }";

    public static Diagnostic NumberExpected(string found) => Diagnostic.Create(
        "c3d.read.number-expected", DiagnosticSeverity.Error,
        "A number was expected and a {found} was found.", ("found", found));

    public static Diagnostic IntegerExpected(string text) => Diagnostic.Create(
        "c3d.read.integer-expected", DiagnosticSeverity.Error,
        "{text} is not an integer. Every coordinate and size in a 3D view is an integer number of DBU.",
        ("text", text));

    public static Diagnostic ArrayExpected(string shape) => Diagnostic.Create(
        "c3d.read.array-expected", DiagnosticSeverity.Error,
        "A list spelled {shape} was expected here.", ("shape", shape));

    public static Diagnostic WrongArity(string shape, int expected, int found) => Diagnostic.Create(
        "c3d.read.wrong-arity", DiagnosticSeverity.Error,
        "A point is spelled {shape}: {expected} integers, and this one has {found}.",
        ("shape", shape), ("expected", expected), ("found", found));

    // ── Findings: the file reads, and something in it is wrong (R-em3d41-2f) ───────────────────

    public static Diagnostic InvalidName(string kind, string name, string reason) => Diagnostic.Create(
        "c3d.name.invalid", DiagnosticSeverity.Error,
        "The {kind} name '{name}' is not usable: {reason}", ("kind", kind), ("name", name), ("reason", reason));

    public static Diagnostic ReservedName(string name) => Diagnostic.Create(
        "c3d.name.reserved", DiagnosticSeverity.Error,
        "'{name}' is reserved: the air box's faces are named airbox/xmin, airbox/zmax and so on.",
        ("name", name));

    public static Diagnostic DuplicateName(string name, int count) => Diagnostic.Create(
        "c3d.name.duplicate", DiagnosticSeverity.Error,
        "The name '{name}' is used by {count} objects. A port or a boundary attaches to an object by name, " +
        "so every name must be unique in the document.",
        ("name", name), ("count", count));

    /// <summary>A group's name is unique among groups: <c>Ungroup match</c> must name one group (<see cref="C3dGroups"/>).</summary>
    public static Diagnostic GroupInTwoPlaces(string name, string paths) => Diagnostic.Create(
        "c3d.group.duplicate", DiagnosticSeverity.Error,
        "The group '{name}' is in more than one place ({paths}). A group's name must be unique among the document's groups.",
        ("name", name), ("paths", paths));

    /// <summary>A warning (3D editor bugs round 2): an object with no material yet is ignored by the solver, so a
    /// half-finished design still runs. The words are <see cref="C3dElaborator.NoMaterialWarning"/>'s.</summary>
    public static Diagnostic NoMaterial(string name) => Diagnostic.Create(
        "c3d.material.missing", DiagnosticSeverity.Warning,
        "'{name}' has no material, so the solver ignores it.", ("name", name));

    /// <summary>A warning, because materials resolve late (brief 42): the technology may not be the
    /// one the document will be elaborated against.</summary>
    public static Diagnostic UnknownMaterial(string name, string material) => Diagnostic.Create(
        "c3d.material.unknown", DiagnosticSeverity.Warning,
        "'{name}' is made of '{material}', which the technology does not define.",
        ("name", name), ("material", material));

    public static Diagnostic TooFewPoints(string name, string loop, int distinct) => Diagnostic.Create(
        "c3d.outline.too-few-points", DiagnosticSeverity.Error,
        "The {loop} of '{name}' has {distinct} distinct points; it needs at least three.",
        ("name", name), ("loop", loop), ("distinct", distinct));

    public static Diagnostic ZeroVolume(string name, string kind, string why) => Diagnostic.Create(
        "c3d.solid.zero-volume", DiagnosticSeverity.Error,
        "The {kind} '{name}' has no volume: {why}.", ("name", name), ("kind", kind), ("why", why));

    /// <summary>brief-sar-3 R-sar3-3 — a lumped element with no value.</summary>
    public static Diagnostic LumpedElementEmpty(string name) => Diagnostic.Create(
        "c3d.lumped.empty", DiagnosticSeverity.Error,
        "The lumped element '{name}' states no R, L or C; give it at least one.", ("name", name));

    /// <summary>brief-sar-3 R-sar3-2 — a phantom whose dimensions or layers do not make its shape.</summary>
    public static Diagnostic PhantomShape(string name, string why) => Diagnostic.Create(
        "c3d.phantom.shape", DiagnosticSeverity.Error,
        "The phantom '{name}' {why}.", ("name", name), ("why", why));

    /// <summary>brief-sar-11 — a voxel body or an imported surface mesh that does not hold together.</summary>
    public static Diagnostic VoxelBodyShape(string name, string why) => Diagnostic.Create(
        "c3d.voxelbody.shape", DiagnosticSeverity.Error,
        "The voxel body '{name}' {why}.", ("name", name), ("why", why));

    public static Diagnostic MeshShape(string name, string why) => Diagnostic.Create(
        "c3d.mesh.shape", DiagnosticSeverity.Error,
        "The mesh '{name}' {why}.", ("name", name), ("why", why));

    /// <summary>brief-sar-11 — an imported anatomy file whose bytes are not the ones recorded at import: a WARNING, since the model
    /// is read again as it now is, and the run says what it holds.</summary>
    public static Diagnostic ImportedFileChanged(string kind, string name, string file) => Diagnostic.Create(
        "c3d.imported.changed", DiagnosticSeverity.Warning,
        "The {kind} '{name}' was imported from '{file}', and that file has changed since: what runs is the file as it is now.",
        ("kind", kind), ("name", name), ("file", file));

    public static Diagnostic SheetShape(string name, string why) => Diagnostic.Create(
        "c3d.sheet.shape", DiagnosticSeverity.Error,
        "The sheet '{name}' {why}.", ("name", name), ("why", why));

    public static Diagnostic PolylineTooShort(string name, int count) => Diagnostic.Create(
        "c3d.polyline.too-short", DiagnosticSeverity.Error,
        "The polyline '{name}' has {count} point(s); it needs at least two.", ("name", name), ("count", count));

    /// <summary>brief-em3d-50 — a wire that cannot be a wire, whatever it lands on.</summary>
    public static Diagnostic WireShape(string name, string why) => Diagnostic.Create(
        "c3d.wire.shape", DiagnosticSeverity.Error,
        "The wire '{name}' {why}.", ("name", name), ("why", why));

    /// <summary>brief-em3d-50 R-em3d50-2 — a wire's points are world points: it has no placement.</summary>
    public static Diagnostic WirePlacement(string name) => Diagnostic.Create(
        "c3d.wire.placement", DiagnosticSeverity.Error,
        "The wire '{name}' states a Placement. A wire has none: its Points are where it is, because it spans things that " +
        "each have their own placement. Write the points where the wire is, and leave Placement out.", ("name", name));

    public static Diagnostic FaceIndex(string name, string face, int index, int vertexCount) => Diagnostic.Create(
        "c3d.polyhedron.bad-index", DiagnosticSeverity.Error,
        "Face '{face}' of '{name}' names vertex {index}, and the polyhedron has {vertexCount} vertices.",
        ("name", name), ("face", face), ("index", index), ("vertexCount", vertexCount));

    public static Diagnostic FaceTooSmall(string name, string face) => Diagnostic.Create(
        "c3d.polyhedron.face-too-small", DiagnosticSeverity.Error,
        "Face '{face}' of '{name}' has a loop of fewer than three vertices.", ("name", name), ("face", face));

    public static Diagnostic FaceUnnamed(string name, int index) => Diagnostic.Create(
        "c3d.polyhedron.face-unnamed", DiagnosticSeverity.Error,
        "Face {index} of '{name}' has no name. Faces are named, never indexed, so a boundary survives an edit.",
        ("name", name), ("index", index));

    public static Diagnostic FaceNameDuplicate(string name, string face) => Diagnostic.Create(
        "c3d.polyhedron.face-duplicate", DiagnosticSeverity.Error,
        "'{name}' has more than one face named '{face}'.", ("name", name), ("face", face));

    /// <summary>R-em3d41-2f: a failure names the edge's two vertices.</summary>
    public static Diagnostic NotClosed(string name, string edges) => Diagnostic.Create(
        "c3d.polyhedron.not-closed", DiagnosticSeverity.Error,
        "The polyhedron '{name}' is not closed. Every edge must be used by exactly two faces, in opposite " +
        "directions, and these are not: {edges}.",
        ("name", name), ("edges", edges));

    public static Diagnostic NotPlanar(string name, string face, double deviation) => Diagnostic.Create(
        "c3d.polyhedron.not-planar", DiagnosticSeverity.Error,
        "Face '{face}' of '{name}' is not planar: a vertex lies {deviation} DBU off the face's plane, and " +
        "the limit is 1 DBU.",
        ("name", name), ("face", face), ("deviation", deviation));

    public static Diagnostic FaceNoArea(string name, string face) => Diagnostic.Create(
        "c3d.polyhedron.face-no-area", DiagnosticSeverity.Error,
        "Face '{face}' of '{name}' has no area: its vertices are in a line.", ("name", name), ("face", face));

    public static Diagnostic InstanceNoCell(string name) => Diagnostic.Create(
        "c3d.instance.no-cell", DiagnosticSeverity.Error,
        "The instance '{name}' names no cell.", ("name", name));

    public static Diagnostic InstanceNoDesign(string name) => Diagnostic.Create(
        "c3d.instance.no-design", DiagnosticSeverity.Error,
        "The instance '{name}' places a Hertzon project but names no design.", ("name", name));

    // ── The Look (brief-em3d-106) ────────────────────────────────────────────────────────────

    /// <summary>R-em3d106-5 — an Exposure or Intensity outside its range (a Rotation that is not a number).</summary>
    public static Diagnostic LookRange(string key, double value, double lo, double hi) => Diagnostic.Create(
        "c3d.look.range", DiagnosticSeverity.Error,
        double.IsInfinity(lo) ? "The Look's {key} is {value}, which is not a number of degrees."
                              : "The Look's {key} is {value}; it is {lo} to {hi}.",
        ("key", key), ("value", value.ToString("G6", CultureInfo.InvariantCulture)),
        ("lo", lo.ToString("G6", CultureInfo.InvariantCulture)), ("hi", hi.ToString("G6", CultureInfo.InvariantCulture)));

    public static Diagnostic LookBackground(string spelled) => Diagnostic.Create(
        "c3d.look.background", DiagnosticSeverity.Error,
        "The Look's Background \"{spelled}\" is none of Theme, #rrggbb, \"#rrggbb,#rrggbb\" (a vertical gradient, top first) or " +
        "Environment.", ("spelled", spelled));

    /// <summary>brief-em3d-108 R-em3d108-3d — a Look.Camera a picture cannot be taken from.</summary>
    public static Diagnostic LookCamera(string fault) => Diagnostic.Create(
        "c3d.look.camera", DiagnosticSeverity.Error,
        "The Look's Camera cannot be used for a picture: its {fault}. Set Camera in the Look panel writes a sound one, and Clear removes it.",
        ("fault", fault));

    public static Diagnostic LookEnvironment(string spelled) => Diagnostic.Create(
        "c3d.look.environment", DiagnosticSeverity.Error,
        "The Look's Environment \"{spelled}\" is neither a studio (Studio, HighKey, Dark) nor a Radiance .hdr file; an .exr is not " +
        "read.", ("spelled", spelled));

    /// <summary>brief-em3d-109 R-em3d109-4a — a FieldStyle that is none of the three.</summary>
    public static Diagnostic LookFieldStyle(string spelled) => Diagnostic.Create(
        "c3d.look.field-style", DiagnosticSeverity.Error,
        "The Look's FieldStyle \"{spelled}\" is none of Exact, Lit or Glow.", ("spelled", spelled));

    /// <summary>R-em3d106-4d — a user environment that cannot be read. A warning: the view lights the scene with Studio instead
    /// and says so on its status line.</summary>
    public static Diagnostic LookHdrUnreadable(string path, string reason) => Diagnostic.Create(
        "c3d.look.hdr-unreadable", DiagnosticSeverity.Warning,
        "The Look's environment '{path}' cannot be read: {reason}. The realistic view lights the scene with Studio instead.",
        ("path", path), ("reason", reason));

    // ── Transparency (brief-em3d-92) ──────────────────────────────────────────────────────────

    /// <summary>A transparency outside 0 to <see cref="C3dTransparency.Max"/>; the sentence is built from the constant.</summary>
    public static Diagnostic TransparencyRange(string what, int percent) => Diagnostic.Create(
        "c3d.transparency.range", DiagnosticSeverity.Error, "{why}", ("why", C3dTransparency.OutOfRange(what, percent)));

    /// <summary>brief-em3d-101 R-em3d101-6 — an image that cannot be drawn. A warning, never an error: an image is drawing only, and a
    /// design must not fail <c>check</c> because a photo moved. The view draws a placeholder in its place.</summary>
    public static Diagnostic ImageUnreadable(string name, string path, string reason) => Diagnostic.Create(
        "c3d.image.unreadable", DiagnosticSeverity.Warning,
        "'{name}' is drawn with the image '{file}', which cannot be drawn: {reason}. The view draws a placeholder in its place; " +
        "Resolve Path… points it at the file.",
        ("name", name), ("file", path), ("reason", reason));

    /// <summary>brief-em3d-101 R-em3d101-8g — a face image on a face its object no longer has (an edit took the face away). A warning:
    /// the record is kept in the file, never dropped, and Remove takes it away.</summary>
    public static Diagnostic FaceImageUnresolved(string name, string face) => Diagnostic.Create(
        "c3d.image.face-missing", DiagnosticSeverity.Warning,
        "'{name}' has an image mapped onto its face '{face}', which it no longer has; the image is kept and not drawn. Remove it, or " +
        "map it onto a face it has.",
        ("name", name), ("face", face));

    /// <summary>brief-em3d-101 R-em3d101-8b (D4) — two images on one face: the first is drawn.</summary>
    public static Diagnostic FaceImageTwice(string name, string face) => Diagnostic.Create(
        "c3d.image.face-twice", DiagnosticSeverity.Warning,
        "'{name}' has more than one image mapped onto its face '{face}'; a face carries one, so only the first is drawn.",
        ("name", name), ("face", face));

    /// <summary>brief-em3d-101 — a face image whose stated Width or Height is not positive: drawn with no area (zero) or mirrored
    /// (negative), neither of which the editor writes. A warning, as every image finding is: an image is drawing only.</summary>
    public static Diagnostic FaceImageSize(string name, string face) => Diagnostic.Create(
        "c3d.image.face-size", DiagnosticSeverity.Warning,
        "The image on '{name}/{face}' states a Width or Height that is not positive. Omit both to fit it to the face.",
        ("name", name), ("face", face));

    /// <summary>brief-em3d-101 — <c>Locked</c> on a sheet with no image: only an image sheet is locked (R-em3d101-1g).</summary>
    public static Diagnostic LockedWithoutImage(string name) => Diagnostic.Create(
        "c3d.image.locked-without-image", DiagnosticSeverity.Warning,
        "'{name}' is Locked but carries no image. Only an image sheet is locked, so nothing reads it.", ("name", name));

    public static Diagnostic TransparencyOnPolyline(string name) => Diagnostic.Create(
        "c3d.transparency.polyline", DiagnosticSeverity.Warning,
        "The polyline '{name}' states a Transparency. A polyline is a line and is never filled, so nothing reads it.", ("name", name));

    public static Diagnostic TransparencyOnOperand(string name, string operand) => Diagnostic.Create(
        "c3d.transparency.operand", DiagnosticSeverity.Warning,
        "An operand of '{name}'{operand} states a Transparency. Inside an operation an operand has none of its own: the " +
        "operation's is its result's. Put it on '{name}'.", ("name", name), ("operand", operand.Length > 0 ? $" ('{operand}')" : ""));

    // ── Appearance (brief-em3d-105) ───────────────────────────────────────────────────────────

    /// <summary>R-em3d105-1b — an appearance value out of its range, or a colour that is not #rrggbb: the phrase is
    /// MaterialValidation.AppearanceFaults', the one rule a material's appearance is held to as well.</summary>
    public static Diagnostic AppearanceInvalid(string what, string fault) => Diagnostic.Create(
        "c3d.appearance.invalid", DiagnosticSeverity.Error, "{what} states an appearance whose {fault}.", ("what", what), ("fault", fault));

    /// <summary>A Like naming no material of the document's technology: a warning, and the look falls through.</summary>
    public static Diagnostic AppearanceLikeUnknown(string what, string like) => Diagnostic.Create(
        "c3d.appearance.like-unknown", DiagnosticSeverity.Warning,
        "{what} looks Like '{like}', which the document's technology does not define, so it takes nothing from it: its look falls " +
        "through to its material's.", ("what", what), ("like", like));

    public static Diagnostic AppearanceOnPolyline(string name) => Diagnostic.Create(
        "c3d.appearance.polyline", DiagnosticSeverity.Warning,
        "The polyline '{name}' states an Appearance. A polyline is a line and is never filled, so nothing reads it.", ("name", name));

    public static Diagnostic AppearanceOnOperand(string name, string operand) => Diagnostic.Create(
        "c3d.appearance.operand", DiagnosticSeverity.Warning,
        "An operand of '{name}'{operand} states an Appearance. Inside an operation an operand has none of its own: the " +
        "operation's is its result's. Put it on '{name}'.", ("name", name), ("operand", operand.Length > 0 ? $" ('{operand}')" : ""));

    // ── Model (brief-em3d-93) ─────────────────────────────────────────────────────────────────

    public static Diagnostic ModelOnPolyline(string name) => Diagnostic.Create(
        "c3d.model.polyline", DiagnosticSeverity.Warning,
        "The polyline '{name}' states \"Model\": false. A polyline is construction geometry and is never in a solve, so nothing reads it.",
        ("name", name));

    public static Diagnostic ModelOnOperand(string name, string operand) => Diagnostic.Create(
        "c3d.model.operand", DiagnosticSeverity.Warning,
        "An operand of '{name}'{operand} states \"Model\": false. Inside an operation an operand belongs to its result, so nothing " +
        "reads it: turn '{name}' off instead.", ("name", name), ("operand", operand.Length > 0 ? $" ('{operand}')" : ""));

    public static Diagnostic ArrayCounts(string name) => Diagnostic.Create(
        "c3d.instance.array-counts", DiagnosticSeverity.Error,
        "The array on '{name}' must give three counts, [nx, ny, nz], each at least 1.", ("name", name));

    // ── Operations (brief-em3d-64 R-em3d64-6a) ─────────────────────────────────────────────────

    public static Diagnostic OperationShape(string name, string kind, string why) => Diagnostic.Create(
        "c3d.operation.shape", DiagnosticSeverity.Error,
        "The {kind} '{name}' {why}.", ("name", name), ("kind", kind), ("why", why));

    /// <summary>R-em3d64-3b — a result's material and role are its Blank's (or Target's).</summary>
    public static Diagnostic OperationMaterial(string name, string kind, string inner) => Diagnostic.Create(
        "c3d.operation.material", DiagnosticSeverity.Error,
        "The {kind} '{name}' states a Material or Role of its own. A {kind}'s result is made of its {inner}'s material and takes " +
        "its {inner}'s role: set them on the {inner}.",
        ("name", name), ("kind", kind), ("inner", inner));

    /// <summary>R-em3d64-1d — the wrapper takes the name.</summary>
    public static Diagnostic OperandNamed(string name, string operandName, string slot) => Diagnostic.Create(
        "c3d.operation.operand-named", DiagnosticSeverity.Error,
        "The {slot} of '{name}' is named '{operandName}'. An operation takes the name of the object it acts on, which then has none " +
        "of its own: leave the {slot}'s Name out, and '{name}' is how the result is addressed.",
        ("name", name), ("operandName", operandName), ("slot", slot));

    /// <summary>R-em3d64-1e — solids only.</summary>
    public static Diagnostic OperandKind(string name, string operand, string kind) => Diagnostic.Create(
        "c3d.operation.operand-kind", DiagnosticSeverity.Error,
        "'{operand}' in '{name}' is a {kind}, which cannot be an operand: a Box, Prism, Cylinder, Sphere, Polyhedron, Boolean, Fillet, " +
        "Chamfer or Step part can, and a Sheet can with other sheets in its plane. A wire is not a solid the kernel builds.",
        ("name", name), ("operand", operand), ("kind", kind));

    /// <summary>brief-hzn-10 R-hzn10-1 — a boolean of sheets and solids together.</summary>
    public static Diagnostic SheetBooleanMixed(string name, string sheet, string other, string otherKind) => Diagnostic.Create(
        "c3d.operation.sheet-mixed", DiagnosticSeverity.Error,
        "'{name}' mixes sheets and solids: '{sheet}' is a sheet and '{other}' is a {otherKind}. A boolean's operands are all solids, " +
        "or all sheets in one plane.",
        ("name", name), ("sheet", sheet), ("other", other), ("otherKind", otherKind));

    /// <summary>brief-hzn-10 R-hzn10-1 — an operand of a sheet boolean out of the Blank's plane, or placed out of it.</summary>
    public static Diagnostic SheetBooleanPlane(string name, string operand, string why) => Diagnostic.Create(
        "c3d.operation.sheet-plane", DiagnosticSeverity.Error,
        "'{operand}' in '{name}' {why}. A boolean of sheets needs every operand in one plane, at one offset.",
        ("name", name), ("operand", operand), ("why", why));

    public static Diagnostic StepShape(string name, string why) => Diagnostic.Create(
        "c3d.step.shape", DiagnosticSeverity.Error,
        "The Step part '{name}' {why}.", ("name", name), ("why", why));

    /// <summary>brief-em3d-129 R-em3d129-1a — a Step object with no Solid whose part is several solids: a WARNING when their
    /// colours differ (probably different materials), a NOTE otherwise.</summary>
    public static Diagnostic StepManySolids(string name, int solids, string file, string part, bool coloursDiffer) => Diagnostic.Create(
        "c3d.step.many-solids", coloursDiffer ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info,
        "'{name}' is {solids} solids of {file} part {part} in one object, so they share one material{colours}. Split into Solids gives each its own.",
        ("name", name), ("solids", solids), ("file", file), ("part", part), ("colours", coloursDiffer ? "; their colours differ" : ""));

    /// <summary>Brief 68 §6's wording: the file changed outside circuitRF, so its face numbers no longer mean what the
    /// references assumed.</summary>
    public static Diagnostic StepHashMismatch(string name, string file) => Diagnostic.Create(
        "c3d.step.hash", DiagnosticSeverity.Error,
        "The Step part '{name}' was read from '{file}', and that file has changed since: its bytes are not the ones recorded in " +
        "Hash, so its face<n> names no longer mean what the ports and boundaries on it assumed. Use Reload from Source, which " +
        "re-matches every reference by geometry.",
        ("name", name), ("file", file));

    /// <summary>R-em3d64-5e — the sentence is <c>GeometryKernel.NeedsKernel</c>'s, the one place the absence is worded.</summary>
    public static Diagnostic NeedsKernel(string sentence) => Diagnostic.Create(
        "c3d.kernel.absent", DiagnosticSeverity.Error, "{sentence}", ("sentence", sentence));

    public static Diagnostic UnreadKey(string owner, string key) => Diagnostic.Create(
        "c3d.key.unread", DiagnosticSeverity.Warning,
        "{owner} has a key '{key}' that is not part of the format. It is kept, and nothing reads it.",
        ("owner", owner), ("key", key));
}
