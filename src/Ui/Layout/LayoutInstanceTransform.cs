// The ONE place LayoutInstance's (translate + R0/R90/R180/R270 + mirror-X + magnification + array)
// transform is defined — every consumer (bbox math in CellHierarchy, hit-test's point-into-local-frame
// test, the renderer's per-placement SKMatrix) derives from this canonical DBU-space (Y-up) definition
// so none of them can silently disagree with each other about what an instance actually looks like.
// Framework-free — no Skia; the renderer additionally exposes PathSpaceLinearCoefficients for building
// its own SKMatrix directly in path-space (Y-down, real microns) without re-deriving the rotation
// signs a second time — see that method's doc comment for the derivation.

namespace CircuitRF.Ui.Layout;

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

        (double rx, double ry) = inst.Rot switch
        {
            LayoutRotation.R90  => (-my, mx),
            LayoutRotation.R180 => (-mx, -my),
            LayoutRotation.R270 => (my, -mx),
            _                   => (mx, my), // R0
        };

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

        (double mx, double my) = inst.Rot switch
        {
            LayoutRotation.R90  => (ry, -rx),
            LayoutRotation.R180 => (-rx, -ry),
            LayoutRotation.R270 => (-ry, rx),
            _                   => (rx, ry), // R0
        };

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
    /// Substituting <c>lx=lpx, ly=-lpy</c> into <see cref="TransformPoint"/>'s rotation table and
    /// negating the result's Y component (full algebra in the L3a completion note) yields:
    /// <code>
    /// R0:   wpx =  sx*lpx           wpy =  sy*lpy
    /// R90:  wpx =  sy*lpy           wpy = -sx*lpx
    /// R180: wpx = -sx*lpx           wpy = -sy*lpy
    /// R270: wpx = -sy*lpy           wpy =  sx*lpx
    /// </code>
    /// Returned as <c>(A, B, C, D)</c> for <c>wpx = A*lpx + B*lpy</c>, <c>wpy = C*lpx + D*lpy</c> — the
    /// renderer supplies the translation (the parent path-space position of this placement's origin)
    /// separately, since that depends on the PARENT's per-frame path-space origin, not on the instance.
    /// </summary>
    public static (double A, double B, double C, double D) PathSpaceLinearCoefficients(LayoutInstance inst)
    {
        var (sx, sy) = MirrorMagScale(inst);
        return inst.Rot switch
        {
            LayoutRotation.R90  => (0, sy, -sx, 0),
            LayoutRotation.R180 => (-sx, 0, 0, -sy),
            LayoutRotation.R270 => (0, -sy, sx, 0),
            _                   => (sx, 0, 0, sy), // R0
        };
    }

    // ── Composition (brief-L3c-flatten-and-group.md §2, "the sub-cell's own instances still become
    //    instances of the parent") ──────────────────────────────────────────────────────────────────

    private static int RotToInt(LayoutRotation r) => r switch
    {
        LayoutRotation.R90  => 1,
        LayoutRotation.R180 => 2,
        LayoutRotation.R270 => 3,
        _                   => 0,
    };

    private static LayoutRotation IntToRot(int k) => (((k % 4) + 4) % 4) switch
    {
        1 => LayoutRotation.R90,
        2 => LayoutRotation.R180,
        3 => LayoutRotation.R270,
        _ => LayoutRotation.R0,
    };

    /// <summary>
    /// Composes <paramref name="outer"/> (the instance being flattened, at array cell
    /// (<paramref name="row"/>, <paramref name="col"/>)) with <paramref name="inner"/> (one of the
    /// sub-cell's OWN instances, in the sub-cell's local frame) into a single equivalent
    /// <see cref="LayoutInstance"/> expressed directly in the PARENT's frame — the exact "re-parent
    /// a nested instance" step Flatten Hierarchy needs so a rotated/mirrored/scaled outer instance
    /// still renders its sub-cell's own instances pixel-identically once flattened.
    /// <br/><br/>
    /// Mirror-rotate-scale is a similarity transform closed under composition when rotation is
    /// restricted to 90° multiples (the dihedral group of order 8) — derived via the complex-number
    /// form <c>TransformPoint(z) = i^Rot · Mag · s(z) + T</c> (<c>s(z)=z</c> unmirrored,
    /// <c>s(z)=-conj(z)</c> mirrored): composing outer∘inner yields <c>MirrorC = MirrorOuter ⊕
    /// MirrorInner</c>, <c>MagC = MagOuter·MagInner</c>, and <c>RotC = MirrorOuter ? (RotOuter −
    /// RotInner) : (RotOuter + RotInner)</c> (mod 4) — a mirror on the OUTER side reverses the sense
    /// the INNER rotation composes in, because conjugation reverses the direction complex
    /// multiplication rotates. The translation is simply <see cref="TransformPoint"/> applied to the
    /// inner instance's own origin through the outer instance's full transform — the inner origin is
    /// already a point in exactly the frame <paramref name="outer"/>'s transform maps.
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
        int k1 = RotToInt(outer.Rot), k2 = RotToInt(inner.Rot);
        int kC = outer.MirrorX ? k1 - k2 : k1 + k2;
        var (tx, ty) = TransformPoint(inner.X, inner.Y, outer, row, col);

        return new LayoutInstance
        {
            CellRef  = inner.CellRef,   // caller rebases
            X        = tx,
            Y        = ty,
            Rot      = IntToRot(kC),
            MirrorX  = mirrorC,
            Mag      = magC,
            Rows     = inner.Rows,
            Cols     = inner.Cols,
            PitchX   = (long)Math.Round(inner.PitchX * outer.Mag),
            PitchY   = (long)Math.Round(inner.PitchY * outer.Mag),
        };
    }
}
