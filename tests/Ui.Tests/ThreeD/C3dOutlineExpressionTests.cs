using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Kernel;

namespace CircuitRF.Ui.Tests.ThreeD;

/// <summary>brief-hzn-13 R-hzn13-1 — a prism's and a sheet's <c>Outline</c> points may hold expressions, as a polyline's points do.</summary>
public sealed class C3dOutlineExpressionTests
{
    private const long Mm = 1_000_000;   // DBU per mm at 1000 DBU/µm

    /// <summary>A sheet outlined by w: (0,0) (w,0) (w,2 mm) (0,2 mm), with w a VAR of 3 mm.</summary>
    private static (C3dDocument Doc, C3dSheet Sheet) Doc(string w = "3mm")
    {
        var sheet = new C3dSheet
        {
            Name = "trace", Material = "pec", Plane = C3dPlane.XY, Offset = 0,
            Outline = [new(0, 0), new(0, 0), new(0, 2 * Mm), new(0, 2 * Mm)],
        };
        var spec = C3dBindings.SpecOf(typeof(C3dSheet), nameof(C3dSheet.Outline))!;
        C3dBindings.SetExpr(sheet, spec.ElementAt(1), 0, new C3dExpr("w", "Mm"));
        C3dBindings.SetExpr(sheet, spec.ElementAt(2), 0, new C3dExpr("w", "Mm"));
        var doc = new C3dDocument { Objects = [sheet], Variables = [new C3dVariable { Name = "w", Expression = w }] };
        return (doc, sheet);
    }

    [Fact]
    public void AnOutlineWithExpressions_RoundTripsByteForByte()
    {
        var (doc, _) = Doc();
        string text = C3dPersistence.Serialize(doc);
        Assert.Contains("{ \"Expr\": \"w\", \"Unit\": \"Mm\" }", text);
        Assert.Equal(text, C3dPersistence.Serialize(C3dPersistence.Deserialize(text)));
    }

    [Fact]
    public void Resolution_MovesTheVertex_WhenTheVariableChanges()
    {
        var (doc, sheet) = Doc();
        Assert.True(C3dResolver.Resolve(doc, C3dCell.None).Ok);
        Assert.Equal(3 * Mm, sheet.Outline[1].U);
        doc.Variables[0].Expression = "5mm";
        Assert.True(C3dResolver.Resolve(doc, C3dCell.None).Ok);
        Assert.Equal([5 * Mm, 5 * Mm], [sheet.Outline[1].U, sheet.Outline[2].U]);
    }

    [Fact]
    public void APointTypedInProperties_ReplacesItsExpressions()
    {
        var (doc, sheet) = Doc();
        Assert.True(C3dResolver.Resolve(doc, C3dCell.None).Ok);
        var r = new C3dFaceEditor(sheet).MoveOutlinePoint(1, new C3dPoint3(4 * Mm, 0, 0));
        var moved = Assert.IsType<C3dSheet>(r.Object);
        var spec = C3dBindings.SpecOf(typeof(C3dSheet), nameof(C3dSheet.Outline))!;
        Assert.Null(C3dBindings.GetExpr(moved, spec.ElementAt(1), 0));
        Assert.Equal("w", C3dBindings.GetExpr(moved, spec.ElementAt(2), 0)!.Expr);   // the other point keeps its own
        Assert.Equal(4 * Mm, moved.Outline[1].U);
    }

    [Fact]
    public void AnUnresolvableOutlineExpression_IsNamedByItsPoint()
    {
        var (doc, _) = Doc();
        doc.Variables.Clear();
        var res = C3dResolver.Resolve(doc, C3dCell.None);
        Assert.False(res.Ok);
        Assert.Contains(res.Errors, e => e.Contains("'trace'") && e.Contains("point 2 u"));
    }
}
