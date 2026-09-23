// ================================================================
//  ClayVertexPerLineTests.cs
//
//  A .clay's coordinates are written a vertex per line, indented with tabs, with LF on every
//  platform (field report, 2026-09-23: an imported board's .clay was 57 MB). The claims are the two
//  that make the smaller file safe to keep under git: it reads back to the same document, and one
//  moved vertex is still a one-line diff.
// ================================================================

using System;
using System.Linq;
using CircuitRF.Design.Layout;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout;

public sealed class ClayVertexPerLineTests
{
    [Fact]
    public void AVertexIsOneLine_TheFileReadsBack_AndOneMovedVertexIsOneChangedLine()
    {
        var view = new LayoutView();
        view.Shapes.Add(new PolygonShape
        {
            Layer = new LayerKey(1, 0),
            Xy    = [0, 0, 100000, 0, 100000, 50000, 0, 50000],
            Holes = [[10000, 10000, 20000, 10000, 20000, 20000]],
        });

        string text = LayoutPersistence.Serialize(view);

        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
        Assert.Contains("\n\t\t\t\t100000, 50000,\n", text, StringComparison.Ordinal);
        Assert.Contains("\n\t\t\t\t\t20000, 20000\n", text, StringComparison.Ordinal);   // a hole's last vertex
        Assert.Equal(text, LayoutPersistence.Serialize(LayoutPersistence.Deserialize(text)));

        ((PolygonShape)view.Shapes[0]).Xy[4] = 100500;
        string[] before = text.Split('\n');
        string[] after  = LayoutPersistence.Serialize(view).Split('\n');
        Assert.Equal(before.Length, after.Length);
        Assert.Single(Enumerable.Range(0, before.Length).Where(i => before[i] != after[i]));
    }
}
