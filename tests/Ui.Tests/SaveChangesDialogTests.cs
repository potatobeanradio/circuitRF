using System.Reflection;
using CircuitRF.Ui.Views.Dialogs;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate: SaveChangesDialog title parameter exists, is the last string, and defaults to "Save Changes".
/// Only <c>destructive</c> follows it (a remove's confirm, where Cancel answers Enter).
/// Constructing the dialog requires the Avalonia runtime (Window ctor), so we verify the
/// API contract via reflection rather than constructing an instance.
/// </summary>
public sealed class SaveChangesDialogTests
{
    private static ParameterInfo TitleParam()
    {
        // (message, saveLabel, dontSaveLabel, cancelLabel, title, destructive, bullets, footer)
        var ctor = typeof(SaveChangesDialog).GetConstructor(
            [typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(bool),
             typeof(IReadOnlyList<string>), typeof(string)]);
        Assert.NotNull(ctor);
        var p = ctor.GetParameters();
        Assert.Equal(8, p.Length);
        Assert.Equal("destructive", p[5].Name);
        return p[4];
    }

    [Fact]
    public void SaveChangesDialog_TitleParam_IsNamedTitle()
    {
        Assert.Equal("title", TitleParam().Name);
    }

    [Fact]
    public void SaveChangesDialog_TitleParam_DefaultIsSaveChanges()
    {
        var p = TitleParam();
        Assert.True(p.HasDefaultValue, "title parameter must have a default value");
        Assert.Equal("Save Changes", p.DefaultValue);
    }
}
