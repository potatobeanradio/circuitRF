// The ONE place LayoutInstance's (translate + ARBITRARY rotation + mirror-X + magnification + array)
// transform is defined — every consumer (bbox math in CellHierarchy, hit-test's point-into-local-frame
// test, the renderer's per-placement SKMatrix) derives from this canonical DBU-space (Y-up) definition
// so none of them can silently disagree with each other about what an instance actually looks like.
//
// L3d generalized the rotation from a four-value enum to a real angle
// (brief-L3d-arbitrary-angle-instances.md R-L3d-2). Every formula below reduces TERM FOR TERM to the
// pre-L3d rotation table at 0/90/180/270 deg, and does so EXACTLY rather than to within a tolerance —
// LayoutAngle.CosSin returns exact literals at the cardinals precisely so that this stays true, since
// Math.Cos(PI/2) is 6.1e-17 and would otherwise shift every existing cardinal placement in every
// existing design. That exactness is pinned by L3dCardinalIdentityTests against written-out expected
// values, not against the old implementation.
// Framework-free — no Skia; the renderer additionally exposes PathSpaceLinearCoefficients for building
// its own SKMatrix directly in path-space (Y-down, real microns) without re-deriving the rotation
// signs a second time — see that method's doc comment for the derivation.

namespace CircuitRF.Design.Layout;

public static class LayoutInstanceTransform
{
    /// <summary>MirrorX negates local X before rotation — the SAME convention
    /// <c>SchematicGeometry.LocalToWorld</c> already established for component MirrorX ("layout
    /// borrows patterns from Schematic, not types" — this file's header repeats that rule for the
    /// transform math specifically).</summary>
    private static (double Sx, double Sy) MirrorMagScale(LayoutInstance inst) =>
        ((inst.MirrorX ? -1.0 : 1.0) * inst.Mag, inst.Mag);

    /// <summary>Array cell origin, in the PARENT's own (unrotated) DBU frame — pitch is deliberately
    /// NOT rotated with the instance (a simpler, more predictable "rows/cols/pitch X/Y" semantics for
    /// a properties-panel field than GDSII AREF's rotated row/column vectors would be), documented as
    /// a deliberate simplification in the L3a completion note.</summary>
    public static (long X, long Y) ArrayCellOrigin(LayoutInstance inst, int row, int col) =>
        (inst.X + (long)col * inst.PitchX, inst.Y + (long)row * inst.PitchY);

    /// <summary>Transforms a cell-local DBU point (Y-up, the sub-cell's own coordinate system) into
    /// the PARENT's DBU space (also Y-up) for array cell (<paramref name="row"/>, <paramref
    /// name="col"/>) of <paramref name="inst"/>. Mirror-then-rotate-then-scale-then-translate, exactly
    /// mirroring <c>SchematicGeometry.LocalToWorld</c>'s ordering.</summary>
    public static (long X, long Y) TransformPoint(long lx, long ly, LayoutInstance inst, int row, int col)
    {
        var (sx, sy) = MirrorMagScale(inst);
        double mx = sx * lx, my = sy * ly;

        // R-L3d-2. At 90 deg (c, s) = (0, 1) gives exactly (-my, mx); at 180 deg (-mx, -my); at 270 deg
        // (my, -mx) — the pre-L3d table, recovered exactly rather than approximately.
        var (c, s) = LayoutAngle.CosSin(inst.RotationDegrees);
        double rx = mx * c - my * s;
        double ry = mx * s + my * c;

        var (originX, originY) = ArrayCellOrigin(inst, row, col);
        return (originX + (long)Math.Round(rx), originY + (long)Math.Round(ry));
    }

    /// <summary>Inverse of <see cref="TransformPoint"/> (as doubles, no rounding) — maps a PARENT-space
    /// point back into the sub-cell's own local DBU frame, e.g. so hit-test can transform a click point
    /// once and then run the sub-cell's ordinary per-shape tests against it, rather than transforming
    /// every candidate shape's geometry into parent space.</summary>
    public static (double X, double Y) InverseTransformPoint(double px, double py, LayoutInstance inst, int row, int col)
    {
        var (originX, originY) = ArrayCellOrigin(inst, row, col);
        double rx = px - originX, ry = py - originY;

        // The transpose of TransformPoint's rotation (a rotation's inverse IS its transpose), so the
        // two stay inverses by construction rather than by two independently-maintained tables.
        var (c, s) = LayoutAngle.CosSin(inst.RotationDegrees);
        double mx = rx * c + ry * s;
        double my = -rx * s + ry * c;

        var (sx, sy) = MirrorMagScale(inst);
        if (sx == 0 || sy == 0) return (0, 0);
        return (mx / sx, my / sy);
    }

    /// <summary>
    /// The renderer's per-placement matrix, expressed directly in PATH-SPACE units (real microns,
    /// Y-down) so a cached cell-local <c>SKPath</c> (built once via that same Y-down convention — see
    /// <c>LayoutRenderer.PathSpace</c>) can be drawn under this matrix with no further per-point work.
    /// <br/><br/>
    /// Derivation: path-space's Y is a pure negation of DBU Y (<c>PathSpace.Y(dbu) = -(dbu-origin)*scale</c>),
    /// and both the sub-cell's and the parent's path-space are expressed in the SAME real-micron units
    /// (each view's own <c>dbuToUm</c> cancels out once coordinates are converted to physical microns) —
    /// so composing the canonical DBU-space linear transform above with a Y-negation on both the input
    /// and output reduces to swapping the sign of whichever coefficient multiplies the Y-carrying term.
    /// Substituting <c>lx=lpx, ly=-lpy</c> into <see cref="TransformPoint"/>'s rotation and negating
    /// the result's Y component (full algebra in the L3a completion note) yields, for a rotation of
    /// <c>theta</c> with <c>c = cos(theta)</c> and <c>s = sin(theta)</c>:
    /// <code>
    /// wpx =  sx*c*lpx + sy*s*lpy
    /// wpy = -sx*s*lpx + sy*c*lpy
    /// </code>
    /// which at the four cardinals is exactly the pre-L3d table it replaces (R-L3d-2):
    /// <code>
    /// R0:   (sx, 0, 0, sy)      R90:  (0, sy, -sx, 0)
    /// R180: (-sx, 0, 0, -sy)    R270: (0, -sy, sx, 0)
    /// </code>
    /// Returned as <c>(A, B, C, D)</c> for <c>wpx = A*lpx + B*lpy</c>, <c>wpy = C*lpx + D*lpy</c> — the
    /// renderer supplies the translation (the parent path-space position of this placement's origin)
    /// separately, since that depends on the PARENT's per-frame path-space origin, not on the instance.
    /// An arbitrary angle costs the renderer nothing: it is still one SKMatrix per placement over a
    /// cached cell-local SKPath, with different numbers in it.
    /// </summary>
    public static (double A, double B, double C, double D) PathSpaceLinearCoefficients(LayoutInstance inst)
    {
        var (sx, sy) = MirrorMagScale(inst);
        var (c, s) = LayoutAngle.CosSin(inst.RotationDegrees);
        return (sx * c, sy * s, -(sx * s), sy * c);
    }

    // ── Composition (brief-L3c-flatten-and-group.md §2, "the sub-cell's own instances still become
    //    instances of the parent") ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Composes <paramref name="outer"/> (the instance being flattened, at array cell
    /// (<paramref name="row"/>, <paramref name="col"/>)) with <paramref name="inner"/> (one of the
    /// sub-cell's OWN instances, in the sub-cell's local frame) into a single equivalent
    /// <see cref="LayoutInstance"/> expressed directly in the PARENT's frame — the exact "re-parent
    /// a nested instance" step Flatten Hierarchy needs so a rotated/mirrored/scaled outer instance
    /// still renders its sub-cell's own instances pixel-identically once flattened.
    /// <br/><br/>
    /// Mirror-rotate-scale is a similarity transform, and similarities are closed under composition at
    /// ANY rotation angle — derived via the complex-number form
    /// <c>TransformPoint(z) = e^(i·theta) · Mag · s(z) + T</c> (<c>s(z)=z</c> unmirrored,
    /// <c>s(z)=-conj(z)</c> mirrored): composing outer∘inner yields <c>MirrorC = MirrorOuter ⊕
    /// MirrorInner</c>, <c>MagC = MagOuter·MagInner</c>, and <c>thetaC = MirrorOuter ? (thetaOuter −
    /// thetaInner) : (thetaOuter + thetaInner)</c> — a mirror on the OUTER side reverses the sense the
    /// INNER rotation composes in, because conjugation reverses the direction complex multiplication
    /// rotates. The translation is simply <see cref="TransformPoint"/> applied to the inner instance's
    /// own origin through the outer instance's full transform — the inner origin is already a point in
    /// exactly the frame <paramref name="outer"/>'s transform maps.
    /// <br/><br/>
    /// <b>Pre-L3d this method spoke of "the dihedral group of order 8" and composed a mod-4 integer.</b>
    /// That closure property was a CONSEQUENCE of the rotation being an enum, never a requirement of the
    /// math — the identical derivation above holds over the reals, which is why generalizing it was
    /// substitution rather than redesign (R-L3d-2).
    /// <br/><br/>
    /// <b>R-L3d-3: coordinates round to integer DBU exactly once, here, at the outermost transform.</b>
    /// A non-cardinal rotation is not exactly representable in integer DBU, so a composition that
    /// rounded at every rung of a deep hierarchy would drift visibly. This method composes ANGLES and
    /// applies <see cref="TransformPoint"/> ONE time, to the inner origin — preserve that when editing
    /// it.
    /// <br/><br/>
    /// <b>Does NOT set <see cref="LayoutInstance.CellRef"/></b> (path rebasing is a filesystem
    /// concern, out of scope for this framework-free file) — the caller must set it. Copies
    /// <paramref name="inner"/>'s <c>Rows</c>/<c>Cols</c> verbatim (an array nested inside a flattened
    /// instance stays an array) and scales <c>PitchX</c>/<c>PitchY</c> by <c>outer.Mag</c> ONLY — pitch
    /// is a length applied in the array's OWN unrotated frame (the existing, deliberate L3a
    /// simplification — see <see cref="ArrayCellOrigin"/>'s doc comment), carried into the new parent
    /// unrotated and unmirrored, just rescaled.
    /// </summary>
    public static LayoutInstance ComposeInstances(LayoutInstance outer, int row, int col, LayoutInstance inner)
    {
        bool mirrorC = outer.MirrorX ^ inner.MirrorX;
        double magC = outer.Mag * inner.Mag;
        double thetaOuter = outer.RotationDegrees, thetaInner = inner.RotationDegrees;
        double thetaC = outer.MirrorX ? thetaOuter - thetaInner : thetaOuter + thetaInner;
        var (tx, ty) = TransformPoint(inner.X, inner.Y, outer, row, col);

        return new LayoutInstance
        {
            CellRef  = inner.CellRef,   // caller rebases
            X        = tx,
            Y        = ty,
            RotationDegrees = thetaC,   // normalizes into [0, 360)
            MirrorX  = mirrorC,
            Mag      = magC,
            Rows     = inner.Rows,
            Cols     = inner.Cols,
            PitchX   = (long)Math.Round(inner.PitchX * outer.Mag),
            PitchY   = (long)Math.Round(inner.PitchY * outer.Mag),
        };
    }
}
