// brief-em3d-101 R-em3d101-1 — reference images in 3D: an image a sheet is drawn with (and, Phase B, one mapped onto a face).
//
// DRAWING ONLY. No solver, mesher, exporter or boolean reads an image: a sheet that carries one takes part in a solve exactly
// as any sheet does when it is modelled (C3dSheet.Model), and the image is the sheet's SURFACE in the view. So an image edit
// never makes a result stale (C3dPersistence.SerializeForRun leaves Image and Locked out) and an image sheet is not modelled
// when it is placed — a tracing underlay is not part of the design until someone says so.
//
// A REFERENCE, NEVER THE BYTES (R-em3d101-1b). The path is written relative to the .c3d when the file lies inside the .c3d's
// workspace, absolute otherwise, and it resolves against the .c3d that HOLDS it — an instance's content against the CHILD
// document. Store and Resolve below are the one spelling of that rule.
//
// ONE ORIENTATION RULE (R-em3d101-1c), here and nowhere else: a plane's (u, v) are the image's (right, up) — XY right +x up
// +y, XZ right +x up +z, YZ right +y up +z — which is how each plane reads from the standard view that looks at it (Top from
// +z, Front from −y, Right from +x; Camera3D's StandardView3D), so the picture is never mirrored there. The brief's
// "positive side" of XZ would be +y, from which (x, z) reads mirrored; the Front view is what anyone tracing an XZ drawing
// looks at it from, so that is the side the rule is written for (src/Design/RESOLVED.md).

using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.ThreeD;

/// <summary>An image drawn in the 3D view: the one on a sheet (<see cref="C3dSheet.Image"/>), or one mapped onto a face (brief
/// 101 Phase B). Drawing only: no solver, mesher or exporter reads it.</summary>
public sealed class C3dImage
{
    /// <summary>The image file: relative to the <c>.c3d</c> inside its workspace, absolute outside it (<see cref="C3dImages.Store"/>).</summary>
    public string Path { get; set; } = "";

    /// <summary>A SHEET's image drawn over every face lying on its plane — a picture placed on a solid's face — instead of under
    /// them, the underlay a tracing wants (Scene3DDepthTie.Underlay). It then ties as a face image does. Drawing only; a face
    /// image is always over its face and does not read it. Written only when true.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool InFront { get; set; }

    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Unread { get; set; }
}

/// <summary>
/// brief-em3d-101 R-em3d101-8 — an image mapped onto one named flat face of its object. Its frame is the face's (C3dImages.FaceFrame):
/// seen from OUTSIDE the solid, up is world +z projected into the face (+y on a face whose normal is ±z) and right is up × the
/// outward normal, so the picture is never mirrored; the centre is the face's centroid. With <see cref="Width"/> and
/// <see cref="Height"/> null it is FITTED: as large as fits in the face's bounding rectangle in that frame, aspect kept, centred.
/// Drawn only where the face is.
/// </summary>
public sealed class C3dFaceImage
{
    /// <summary>The face's NAME, as <see cref="C3dObject.FaceNames"/> spells it (a split face's <c>zmax</c> covers <c>zmax#1</c>, …).</summary>
    public string Face { get; set; } = "";

    public C3dImage Image { get; set; } = new();

    /// <summary>Its OWN transparency, percent, independent of the object's (a lid at 80 % can carry a marking at 0 %); an instance's
    /// still multiplies onto it. Omitted: opaque. Capped at <see cref="C3dTransparency.Max"/>.</summary>
    public int? Transparency { get; set; }

    /// <summary>In the face's plane, degrees, counter-clockwise seen from outside.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public double RotationDeg { get; set; }

    /// <summary>DBU; null with <see cref="Height"/> null is fitted to the face.</summary>
    public long? Width { get; set; }
    public long? Height { get; set; }

    /// <summary>The image's centre from the face's centre, along the face frame's (right, up), DBU.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public C3dPoint2 Offset { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool Hidden { get; set; }

    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Unread { get; set; }

    /// <summary>Whether the size is the face's (no Width or Height stated).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Fitted => Width is null && Height is null;
}

/// <summary>
/// brief-em3d-101 Phase B — a face image as the elaboration found it: the elaborated object it is on, the record (and its index in
/// the object's list), its file resolved against the document that holds the object, the document's metres per DBU, and the
/// opacity the instances above multiply onto it. Where the face is, and so where the picture is, is the scene's to work out from
/// the object's own triangles (<see cref="C3dImages.OnFace"/>).
/// </summary>
public sealed record C3dFaceImageUse(string Object, int Index, C3dFaceImage Record, string? Path, double MetresPerDbu, double Opacity)
{
    /// <summary>The face spelled as a record names it: <c>die/zmax</c>.</summary>
    public string FaceSpelled => Object + "/" + Record.Face;
}

/// <summary>
/// brief-em3d-101 — an image placed in the world, as the elaboration resolved it: the file's absolute path (the texture key)
/// and where the picture's corners are. <see cref="Origin"/> is the picture's bottom-left, <see cref="Right"/> its bottom-right
/// and <see cref="Up"/> its top-left, world metres — the corners of the rectangle the image fills, of which a sheet's outline
/// may be only part (an edited sheet clips its image, R-em3d101-1a).
/// </summary>
/// <para><see cref="InFront"/> — a sheet's image drawn over the faces on its plane (<see cref="C3dImage.InFront"/>).</para>
public sealed record C3dPlacedImage(string Path, Point3 Origin, Point3 Right, Point3 Up, bool InFront = false)
{
    /// <summary>The image coordinates of world point <paramref name="p"/> (on the image's plane): u from 0 at the left to 1 at
    /// the right, v from 0 at the BOTTOM to 1 at the top. A texture's v runs down, so it samples at 1 − v.</summary>
    public (double U, double V) At(Point3 p)
    {
        double rx = Right.X - Origin.X, ry = Right.Y - Origin.Y, rz = Right.Z - Origin.Z;
        double ux = Up.X - Origin.X, uy = Up.Y - Origin.Y, uz = Up.Z - Origin.Z;
        double dx = p.X - Origin.X, dy = p.Y - Origin.Y, dz = p.Z - Origin.Z;
        double rr = rx * rx + ry * ry + rz * rz, uu = ux * ux + uy * uy + uz * uz;
        return (rr > 0 ? (dx * rx + dy * ry + dz * rz) / rr : 0, uu > 0 ? (dx * ux + dy * uy + dz * uz) / uu : 0);
    }
}

/// <summary>brief-em3d-101 — the image rules: what can be placed, where its file is, and how it sits on its sheet.</summary>
public static class C3dImages
{
    /// <summary>What the picker offers and a drop accepts: what SkiaSharp decodes (R-em3d101-2a).</summary>
    public static readonly IReadOnlyList<string> Extensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp"];

    /// <summary>Whether <paramref name="path"/> names a file the 3D view places as an image (by extension).</summary>
    public static bool IsImageFile(string? path)
        => path is { Length: > 0 } && Extensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The longest edge, pixels, an image is uploaded at (owner decision D10). An image larger than this is downsampled before
    /// upload, with one note naming the file: it is the size every GPU the 3D view targets guarantees, and an 8000 × 6000 photo is
    /// 190 MB of mip levels otherwise.
    /// </summary>
    public const int MaxTexturePixels = 4096;

    /// <summary>The name a placed image sheet takes (<c>image1</c>, <c>image2</c>, …).</summary>
    public const string NameStem = "image";

    /// <summary>
    /// <paramref name="absolute"/> as a <c>.c3d</c> at <paramref name="documentPath"/> stores it: relative to the document's folder
    /// when the file lies inside the document's workspace (the nearest <c>.cws</c> above it), absolute otherwise — and absolute
    /// for a document in no workspace. Separators are written as <c>/</c>, so the file reads the same on every platform.
    /// </summary>
    public static string Store(string documentPath, string absolute)
    {
        string full = System.IO.Path.GetFullPath(absolute);
        string docDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(documentPath))!;
        if (Workspace.WorkspaceRootFinder.FindAncestorCws(docDir) is not { } cws) return full;
        string root = System.IO.Path.GetDirectoryName(cws)!;
        if (Workspace.WorkspaceRootFinder.IsOutside(full, root)) return full;
        return System.IO.Path.GetRelativePath(docDir, full).Replace('\\', '/');
    }

    /// <summary>The absolute path <paramref name="stored"/> names, resolved against the <c>.c3d</c> at <paramref name="documentPath"/>
    /// that holds it; null for an empty path.</summary>
    public static string? Resolve(string documentPath, string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;
        try
        {
            if (System.IO.Path.IsPathRooted(stored)) return System.IO.Path.GetFullPath(stored);
            string docDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(documentPath))!;
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(docDir, stored));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException) { return null; }
    }

    /// <summary>
    /// Why the image at <paramref name="absolute"/> cannot be drawn, or null when it can: the file is missing, or what is there
    /// does not decode as an image. Reads only the header (SkiaSharp's codec), never the pixels.
    /// </summary>
    public static string? Problem(string? absolute)
    {
        if (absolute is null) return "it names no file";
        if (!System.IO.File.Exists(absolute)) return "the file is not there";
        try
        {
            using var codec = SkiaSharp.SKCodec.Create(absolute);
            return codec is { Info.Width: > 0, Info.Height: > 0 } ? null : "the file is not an image this build reads";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "the file could not be read: " + ex.Message;
        }
    }

    /// <summary>The image's own file name, for a tree row's detail and a hover (<c>die.png</c>).</summary>
    public static string FileName(C3dImage image) => System.IO.Path.GetFileName(image.Path.Replace('\\', '/'));

    /// <summary>
    /// The rectangle a sheet's image fills, in the sheet's own plane coordinates (DBU): its <c>Rect</c>, or — an edited sheet —
    /// the outline's bounding rectangle, which the outline then clips (R-em3d101-1a). Null for a sheet with no extent.
    /// </summary>
    public static (long U0, long V0, long U1, long V1)? FillRect(C3dSheet s)
    {
        if (s.Rect is { } r)
        {
            long u0 = Math.Min(r.Min.U, r.Min.U + r.Size.U), u1 = Math.Max(r.Min.U, r.Min.U + r.Size.U);
            long v0 = Math.Min(r.Min.V, r.Min.V + r.Size.V), v1 = Math.Max(r.Min.V, r.Min.V + r.Size.V);
            return u1 > u0 && v1 > v0 ? (u0, v0, u1, v1) : null;
        }
        if (s.Ellipse is { } el)   // brief-hzn-13 — the ellipse's bounding rectangle, which the ellipse then clips
            return el.Radii.U > 0 && el.Radii.V > 0 ? (el.Centre.U - el.Radii.U, el.Centre.V - el.Radii.V, el.Centre.U + el.Radii.U, el.Centre.V + el.Radii.V) : null;
        if (s.Outline.Count < 3) return null;
        long a = s.Outline.Min(p => p.U), b = s.Outline.Min(p => p.V), c = s.Outline.Max(p => p.U), d = s.Outline.Max(p => p.V);
        return c > a && d > b ? (a, b, c, d) : null;
    }

    /// <summary>
    /// The sheet's image placed in the world: its fill rectangle's corners in the sheet's own frame — the plane's u is the
    /// picture's right and v its up (the one orientation rule, above) — carried by the sheet's placement and then by
    /// <paramref name="world"/> (metres; the instances above it). Null with no image or no extent.
    /// </summary>
    public static C3dPlacedImage? Placed(C3dSheet s, string absolutePath, C3dTransform world, int dbuPerMicron)
    {
        if (FillRect(s) is not { } f) return null;
        double M(long v) => C3dLowering.Metres(v, dbuPerMicron);
        var w = C3dLowering.Snapped(C3dLowering.InMetres(s.Placement.ToTransform(), dbuPerMicron).Then(world));
        double h = M(s.Offset);
        Point3 At(long u, long v) => C3dLowering.Apply(w, C3dLowering.OnPlane(s.Plane, M(u), M(v), h));
        return new C3dPlacedImage(absolutePath, At(f.U0, f.V0), At(f.U1, f.V0), At(f.U0, f.V1), s.Image?.InFront == true);
    }

    // ── Phase B: images on faces ──────────────────────────────────────────────────────────────

    /// <summary>The scene's name for a face image (a record drawn just over its face, as a face boundary's tint is): this prefix,
    /// then the face spelled <c>object/face</c>.</summary>
    public const string FacePrefix = "image:";

    /// <summary>Whether a face named <paramref name="faceName"/> is the face <paramref name="face"/> names: the same, or one of the
    /// pieces an operation split it into (<c>zmax#1</c>).</summary>
    public static bool IsFace(string faceName, string face)
        => faceName == face || faceName.StartsWith(face + "#", StringComparison.Ordinal);

    /// <summary>The image's own pixels (header only), or null when it cannot be read.</summary>
    public static (int Width, int Height)? PixelSize(string? absolute)
    {
        if (absolute is null || !System.IO.File.Exists(absolute)) return null;
        try
        {
            using var codec = SkiaSharp.SKCodec.Create(absolute);
            return codec is { Info.Width: > 0, Info.Height: > 0 } ? (codec.Info.Width, codec.Info.Height) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>The refusal for a curved face (R-em3d101-8f).</summary>
    public static string CurvedFace(string face) => $"Face {face} is curved: an image is mapped onto a flat face.";

    /// <summary>
    /// R-em3d101-8c — the frame of a flat face whose triangles are <paramref name="triangles"/> (world metres, each wound
    /// counter-clockwise seen from OUTSIDE, as the tessellation winds them): its centroid, its outward unit normal, and its in-plane
    /// right and up. Seen from outside, up is world +z projected into the face — +y on a face whose normal is ±z — and right is
    /// up × normal, so the picture is never mirrored. Null, with <paramref name="why"/>, for no area or a curved face.
    /// </summary>
    public static (Point3 Centre, Point3 Normal, Point3 Right, Point3 Up)? FaceFrame(IReadOnlyList<(Point3 A, Point3 B, Point3 C)> triangles,
                                                                                   out string? why)
    {
        why = null;
        double ax = 0, ay = 0, az = 0, cx = 0, cy = 0, cz = 0, area = 0;
        foreach (var (a, b, c) in triangles)
        {
            var n = Cross(Sub(b, a), Sub(c, a));
            double w = Math.Sqrt(Dot(n, n)) / 2;
            ax += n.X / 2; ay += n.Y / 2; az += n.Z / 2;
            cx += w * (a.X + b.X + c.X) / 3; cy += w * (a.Y + b.Y + c.Y) / 3; cz += w * (a.Z + b.Z + c.Z) / 3;
            area += w;
        }
        double nl = Math.Sqrt(ax * ax + ay * ay + az * az);
        if (!(area > 0) || !(nl > 0)) { why = "it has no area"; return null; }
        var normal = new Point3(ax / nl, ay / nl, az / nl);
        // Flat: every triangle's own normal agrees with the face's, and the area the normals add up to is the area itself.
        if (nl < area * (1 - 1e-6)) { why = "curved"; return null; }
        var centre = new Point3(cx / area, cy / area, cz / area);
        var z = new Point3(0, 0, 1);
        var up = Math.Abs(normal.Z) > 0.999 ? new Point3(0, 1, 0) : z;
        // project into the plane
        double d = Dot(up, normal);
        up = Sub(up, new Point3(normal.X * d, normal.Y * d, normal.Z * d));
        double ul = Math.Sqrt(Dot(up, up));
        up = new Point3(up.X / ul, up.Y / ul, up.Z / ul);
        var right = Cross(up, normal);
        return (centre, normal, right, up);
    }

    /// <summary>
    /// The face image <paramref name="record"/> placed on the face whose triangles are <paramref name="triangles"/> (world metres):
    /// fitted or sized, rotated and offset in the face's frame (<see cref="FaceFrame"/>). <paramref name="pixels"/> is the file's
    /// size (a fitted image keeps its aspect; null fits a 4:3 box, as a placement does when a file will not read). Null, with
    /// <paramref name="why"/>, for a face that is curved or has no area.
    /// </summary>
    public static C3dPlacedImage? OnFace(IReadOnlyList<(Point3 A, Point3 B, Point3 C)> triangles, C3dFaceImage record, string path,
                                         (int Width, int Height)? pixels, double metresPerDbu, out string? why)
    {
        if (FaceFrame(triangles, out why) is not { } f) return null;
        // the face's bounding rectangle in its own frame, about the centroid
        double r0 = double.PositiveInfinity, r1 = double.NegativeInfinity, u0 = r0, u1 = r1;
        foreach (var (a, b, c) in triangles)
            foreach (var p in new[] { a, b, c })
            {
                var q = Sub(p, f.Centre);
                double r = Dot(q, f.Right), u = Dot(q, f.Up);
                r0 = Math.Min(r0, r); r1 = Math.Max(r1, r); u0 = Math.Min(u0, u); u1 = Math.Max(u1, u);
            }
        double aspect = pixels is { Width: > 0, Height: > 0 } px ? (double)px.Width / px.Height : 4.0 / 3.0;
        double w, h;
        if (record.Width is { } rw && record.Height is { } rh) (w, h) = (rw * metresPerDbu, rh * metresPerDbu);
        else if (record.Width is { } ow) (w, h) = (ow * metresPerDbu, ow * metresPerDbu / aspect);
        else if (record.Height is { } oh) (w, h) = (oh * metresPerDbu * aspect, oh * metresPerDbu);
        else
        {
            // as large as fits in the bounding rectangle, centred on the centroid
            double hw = Math.Min(-r0, r1), hh = Math.Min(-u0, u1);
            (w, h) = hw / hh > aspect ? (2 * hh * aspect, 2 * hh) : (2 * hw, 2 * hw / aspect);
        }
        double t = record.RotationDeg * Math.PI / 180, cos = Math.Cos(t), sin = Math.Sin(t);
        var right = Add(Scale(f.Right, cos), Scale(f.Up, sin));
        var up = Add(Scale(f.Up, cos), Scale(f.Right, -sin));
        var centre = Add(f.Centre, Add(Scale(f.Right, record.Offset.U * metresPerDbu), Scale(f.Up, record.Offset.V * metresPerDbu)));
        var origin = Sub(centre, Add(Scale(right, w / 2), Scale(up, h / 2)));
        return new C3dPlacedImage(path, origin, Add(origin, Scale(right, w)), Add(origin, Scale(up, h)));
    }

    /// <summary>brief-em3d-101 R-em3d101-8g — a face image whose face its object no longer has, by the face names the elaboration
    /// gave it: kept, never dropped, and said. An object that did not elaborate has no names to judge by and is not judged here.</summary>
    public static IEnumerable<Diagnostics.Diagnostic> FaceImageFindings(C3dElaboration e)
    {
        foreach (var use in e.FaceImages)
            if (Unresolved(e, use)) yield return C3dDiagnostics.FaceImageUnresolved(use.Object, use.Record.Face);
    }

    /// <summary>Whether <paramref name="use"/>'s face is not among its object's elaborated faces (false when the object has none known).</summary>
    public static bool Unresolved(C3dElaboration e, C3dFaceImageUse use)
        => e.Provenance.TryGetValue(use.Object, out var p) && p.FaceNames.Count > 0 && !p.FaceNames.Any(n => IsFace(n, use.Record.Face));

    private static Point3 Sub(Point3 a, Point3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static Point3 Add(Point3 a, Point3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    private static Point3 Scale(Point3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);
    private static double Dot(Point3 a, Point3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static Point3 Cross(Point3 a, Point3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    /// <summary>Every image record <paramref name="objects"/> hold at any depth — a sheet's and each face image's: what a copy, a paste
    /// and a Save As rewrite the paths of.</summary>
    public static IEnumerable<C3dImage> AllImages(IEnumerable<C3dObject> objects)
    {
        foreach (var o in objects.SelectMany(C3dOperands.SelfAndDescendants))
        {
            if (o is C3dSheet { Image: { } sheetImage }) yield return sheetImage;
            foreach (var fi in o.FaceImages ?? []) yield return fi.Image;
        }
    }

    /// <summary>R-em3d101-1b / -5c — the images of <paramref name="objects"/>, moved from the <c>.c3d</c> at <paramref name="from"/>
    /// into the one at <paramref name="to"/> (Flatten, Group into Cell), written as <paramref name="to"/> stores them, so each still
    /// resolves to its file.</summary>
    public static void Rebase(IEnumerable<C3dObject> objects, string from, string to)
    {
        foreach (var img in AllImages(objects))
            if (Resolve(from, img.Path) is { } abs) img.Path = Store(to, abs);
    }

    /// <summary>Every sheet carrying an image in <paramref name="doc"/>, by name.</summary>
    public static IEnumerable<C3dSheet> Sheets(C3dDocument doc) => doc.Objects.OfType<C3dSheet>().Where(s => s.Image is not null);
}
