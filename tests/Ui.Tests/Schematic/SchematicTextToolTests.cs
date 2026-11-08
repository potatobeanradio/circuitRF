// The schematic's Text tool and free text: the symbol editor's text primitive, placed on a schematic. One
// test per claim — the tool's gesture, editing in place, the Properties rows, the .csch record (and the
// pre-existing records written before text was drawn), what the renderer draws, and R on a lone text.

using System.Linq;
using System.Text.Json.Nodes;
using Avalonia.Input;
using CircuitRF.Render;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.Schematic;

public sealed class SchematicTextToolTests
{
    [Fact]
    public void TextTool_ClickTypeEnter_PlacesOneUndoableText_AndLettersAreNotShortcuts()
    {
        var model = new SchematicEditModel();
        var vm = new SchematicViewModel(model);
        vm.SetTextTool();

        vm.OnPointerPressed(1000, 500, KeyModifiers.None);
        Assert.True(vm.IsTypingText);
        vm.OnTextInput("Ws");
        Assert.True(vm.OnKeyDown(Key.S, KeyModifiers.None));   // typed, not "switch to Select"
        Assert.Equal(SchematicViewModel.Tool.Text, vm.ActiveTool);
        vm.OnKeyDown(Key.Back, KeyModifiers.None);
        vm.OnTextInput("!");
        vm.OnKeyDown(Key.Enter, KeyModifiers.None);

        var text = Assert.IsType<EditableText>(Assert.Single(model.CanvasObjects));
        Assert.Equal(("W!", 1000.0, 500.0), (text.Text, text.X, text.Y));
        Assert.Equal((SymbolTextAlign.Left, SymbolTextVAlign.Top), (text.Align, text.VAlign));
        Assert.False(vm.IsTypingText);
        Assert.Equal(SchematicViewModel.Tool.Text, vm.ActiveTool);   // ready for the next one

        vm.UndoRedo.Undo();
        Assert.Empty(model.CanvasObjects);
    }

    [Fact]
    public void TextTool_ShiftEnter_StartsALine_AndEnterPlacesOneTextOfThem()
    {
        var model = new SchematicEditModel();
        var vm = new SchematicViewModel(model);
        vm.SetTextTool();
        vm.OnPointerPressed(0, 0, KeyModifiers.None);
        vm.OnTextInput("one");
        vm.OnKeyDown(Key.Enter, KeyModifiers.Shift);
        vm.OnTextInput("two");
        Assert.True(vm.IsTypingText);
        vm.OnKeyDown(Key.Enter, KeyModifiers.Shift);   // a trailing line break is not kept
        vm.OnKeyDown(Key.Enter, KeyModifiers.None);

        var text = Assert.IsType<EditableText>(Assert.Single(model.CanvasObjects));
        Assert.Equal("one\ntwo", text.Text);
        var (_, y0, _, y1) = SymbolGeometry.BboxOf(text.ToPrimitive());
        Assert.Equal(2 * SymbolGeometry.TextBoxSize(new TextPrimitive { Content = "one", FontSize = text.FontSize }).H, y1 - y0, 6);
    }

    /// <summary>A multi-line text's hit box is as tall as the stack the renderer draws: lines are stacked at the drawing
    /// face's ascent + descent, so the box's line height has to be that, or the lower lines fall outside it.</summary>
    [Fact]
    public void MultiLineText_HitBox_CoversEveryDrawnLine()
    {
        using var font = new SkiaSharp.SKFont(SkiaFonts.PlexRegular, 100f);
        font.GetFontMetrics(out var fm);
        double drawnLine = -fm.Ascent + fm.Descent;

        var t = new TextPrimitive { Content = "one\ntwo\nthree\nfour\nfive", FontSize = 100, AnchorX = 0, AnchorY = 0,
                                   Align = SymbolTextAlign.Left, VAlign = SymbolTextVAlign.Top };
        var (_, y0, _, y1) = SymbolGeometry.BboxOf(t);
        Assert.Equal(5 * drawnLine, y1 - y0, 0.5);
        Assert.True(SymbolGeometry.HitTest(t, 50, 4.5 * drawnLine, 0));   // the middle of the last drawn line
    }

    /// <summary>The inline box shows all its text with no scrollbar: it draws in the face its width is measured in, it is as wide as
    /// its longest line, and that width holds its padding and border as well as the text.</summary>
    [Fact]
    public void InlineBox_FitsItsLongestLine_InTheFaceItMeasures_WithNoScrollbar()
    {
        var box = new CircuitRF.Ui.Controls.SchematicInlineEditBox();
        Assert.Contains("IBMPlexSans-Regular", box.FontFamily.ToString(), StringComparison.Ordinal);
        Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden, Avalonia.Controls.ScrollViewer.GetHorizontalScrollBarVisibility(box));

        const double size = 9;
        const string longest = "Gate bias from the 3.3 V rail through R4.";
        double width = CircuitRF.Ui.Controls.SchematicInlineEditBox.WidthFor("short\n" + longest + "\nshort", size);
        Assert.Equal(CircuitRF.Ui.Controls.SchematicInlineEditBox.WidthFor(longest, size), width, 6);
        using var font = new SkiaSharp.SKFont(CircuitRF.Render.SkiaFonts.PlexRegular, (float)size);
        Assert.True(width >= font.MeasureText(longest) + 2 * CircuitRF.Ui.Controls.SchematicInlineEditBox.LeftPad + 2);
    }

    [Fact]
    public void TextTool_Escape_AbandonsTheText_AndReturnsToSelect()
    {
        var model = new SchematicEditModel();
        var vm = new SchematicViewModel(model);
        vm.SetTextTool();
        vm.OnPointerPressed(0, 0, KeyModifiers.None);
        vm.OnTextInput("gone");

        vm.OnKeyDown(Key.Escape, KeyModifiers.None);

        Assert.Empty(model.CanvasObjects);
        Assert.False(vm.IsTypingText);
        Assert.Null(vm.Overlay.TextPreview);
        Assert.Equal(SchematicViewModel.Tool.Select, vm.ActiveTool);
    }

    [Fact]
    public void DoubleClickEdit_ChangesTheContent_Undoably()
    {
        var model = new SchematicEditModel();
        var text = new EditableText { Text = "old", X = 0, Y = 0 };
        model.CanvasObjects.Add(text);
        var vm = new SchematicViewModel(model);

        vm.BeginCanvasTextEdit(text.Id, 10, 10);
        Assert.Equal("old", vm.InlineEditValue);
        vm.InlineEditValue = "new";
        vm.CommitInlineEdit();

        Assert.Equal("new", text.Text);
        vm.UndoRedo.Undo();
        Assert.Equal("old", text.Text);
    }

    [Fact]
    public void Inspector_ShowsALoneText_AndEachRowIsOneUndoableCommand()
    {
        var model = new SchematicEditModel();
        var text = new EditableText { Text = "note", X = 100, Y = 200 };
        var part = new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor };
        model.CanvasObjects.Add(text);
        model.Components.Add(part);
        var vm = new SchematicViewModel(model);
        var inspector = new SchematicTextInspectorViewModel();
        inspector.SetContext(vm);

        vm.Selection.SetAll([text.Id, part.Id]);
        Assert.False(inspector.IsActive);
        vm.Selection.SetAll([text.Id]);
        Assert.True(inspector.IsActive);
        Assert.False(inspector.ShowTextForceReadable);
        Assert.Equal(("note", 100.0, 200.0), (inspector.TextContent, inspector.TextAnchorX, inspector.TextAnchorY));

        inspector.TextFontSize  = 120;
        inspector.TextFontStyle = SymbolFontStyle.Bold;
        inspector.TextRotation  = SymbolRotation.R270;
        Assert.Equal((120.0, SymbolFontStyle.Bold, SymbolRotation.R270), (text.FontSize, text.FontStyle, text.Rotation));

        vm.UndoRedo.Undo();
        vm.UndoRedo.Undo();
        Assert.Equal((120.0, SymbolFontStyle.Regular, SymbolRotation.R0), (text.FontSize, text.FontStyle, text.Rotation));
        Assert.Equal(SymbolFontStyle.Regular, inspector.TextFontStyle);   // the rows follow the undo
    }

    [Fact]
    public void Csch_RoundTripsEveryField_AndAPreTextRecordReadsCentredOnItsPoint()
    {
        var model = new SchematicEditModel();
        model.CanvasObjects.Add(new EditableText
        {
            Text = "a\nb", X = 300, Y = -400, FontSize = 55, FontStyle = SymbolFontStyle.Italic,
            Align = SymbolTextAlign.Right, VAlign = SymbolTextVAlign.Baseline,
            ColorRole = SymbolColorRole.SymbolPlus, Rotation = SymbolRotation.R90,
        });
        string json = SchematicPersistence.Serialize(model, "Cell");

        var back = Assert.IsType<EditableText>(Assert.Single(SchematicPersistence.Deserialize(json).model.CanvasObjects));
        Assert.Equal(("a\nb", 300.0, -400.0, 55.0), (back.Text, back.X, back.Y, back.FontSize));
        Assert.Equal((SymbolFontStyle.Italic, SymbolTextAlign.Right, SymbolTextVAlign.Baseline),
                     (back.FontStyle, back.Align, back.VAlign));
        Assert.Equal((SymbolColorRole.SymbolPlus, SymbolRotation.R90), (back.ColorRole, back.Rotation));

        // A record written before text was drawn has none of the new keys: its X/Y was a box centre.
        var root = JsonNode.Parse(json)!;
        var rec  = root["CanvasObjects"]!.AsArray()[0]!.AsObject();
        foreach (var key in new[] { "FontStyle", "Align", "VAlign", "ColorRole" }) rec.Remove(key);
        rec["IsBold"] = true;
        var legacy = Assert.IsType<EditableText>(
            Assert.Single(SchematicPersistence.Deserialize(root.ToJsonString()).model.CanvasObjects));
        Assert.Equal((SymbolTextAlign.Center, SymbolTextVAlign.Middle, SymbolFontStyle.Bold),
                     (legacy.Align, legacy.VAlign, legacy.FontStyle));
    }

    [Fact]
    public void Renderer_DrawsFreeText_OneLineBoxPerLine()
    {
        var model = new SchematicEditModel();
        var text = new EditableText { Text = "first\nsecond", X = 0, Y = 0 };
        model.CanvasObjects.Add(text);
        var (render, index) = model.BuildRenderModel();

        var placed = SchematicRenderer.MeasureTexts((800, 600), render, index, -100, -100, 1.0, SchematicRenderTheme.Light)
            .Where(p => p.Role == SchematicTextRole.Text).ToList();

        Assert.Equal(["first", "second"], placed.Select(p => p.Text));
        Assert.All(placed, p => Assert.Equal(text.Id, p.ComponentId));
        Assert.True(placed[1].TopLeft.Y > placed[0].TopLeft.Y);                       // stacked downward
        Assert.Equal(placed[0].TopLeft.X, placed[1].TopLeft.X, 2);                    // Left-aligned
        Assert.Equal(100f, placed[0].TopLeft.X, 1);                                   // anchor is the left edge
    }

    [Fact]
    public void RotateKey_OnALoneText_TurnsItAboutItsOwnAnchor()
    {
        var model = new SchematicEditModel();
        var text = new EditableText { Text = "t", X = 1234, Y = 567 };   // off the connection grid
        model.CanvasObjects.Add(text);
        var vm = new SchematicViewModel(model);
        vm.Selection.SetAll([text.Id]);

        Assert.True(vm.OnKeyDown(Key.R, KeyModifiers.None));

        Assert.Equal((1234.0, 567.0, SymbolRotation.R90), (text.X, text.Y, text.Rotation));
    }
}
