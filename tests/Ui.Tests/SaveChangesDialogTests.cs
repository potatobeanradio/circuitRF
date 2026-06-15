using System.Reflection;
using CircuitRF.Ui.Views.Dialogs;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate: SaveChangesDialog title parameter exists, is last, and defaults to "Save Changes".
/// Constructing the dialog requires the Avalonia runtime (Window ctor), so we verify the
/// API contract via reflection rather than constructing an instance.
/// </summary>
public sealed class SaveChangesDialogTests
{
    private static ParameterInfo TitleParam()
    {
        // 5-string overload: (message, saveLabel, dontSaveLabel, cancelLabel, title)
        var ctor = typeof(SaveChangesDialog).GetConstructor(
            [typeof(string), typeof(string), typeof(string), typeof(string), typeof(string)]);
        Assert.NotNull(ctor);
        var p = ctor.GetParameters();
        Assert.Equal(5, p.Length);
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
