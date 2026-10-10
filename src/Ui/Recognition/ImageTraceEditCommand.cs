// Create Layout from Image, into the layout the picture sits in — brief-img-3-trace-layout-image.md R-im3-7 (D14).
//
// The trace is an EDIT, never a file write: the editor's own AddShapeCommand per shape, chained into one
// CompositeCommand so a single Undo takes the whole trace back off the picture.

using CircuitRF.Design.Layout.Recognition.Image;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Layout;

namespace CircuitRF.Ui.Recognition;

internal static class ImageTraceEditCommand
{
    /// <summary>One undoable command adding every shape of <paramref name="edit"/> to <paramref name="view"/>, or null
    /// when there is nothing to add.</summary>
    public static IUiCommand? For(LayoutView view, ImageTraceEdit edit)
    {
        IUiCommand? chain = null;
        foreach (var shape in edit.Shapes)
        {
            var add = new AddShapeCommand(view, shape);
            chain = chain is null ? add : new CompositeCommand(chain, add);
        }
        return chain;
    }
}
