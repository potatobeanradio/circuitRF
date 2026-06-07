using System;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui;

[RequiresUnreferencedCode(
    "Default ViewLocator uses reflection which may be trimmed away.",
    Url = "https://docs.avaloniaui.net/docs/concepts/view-locator")]
public class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null) return null;

        var name = param.GetType().FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
        var type = Type.GetType(name);

        if (type is not null)
            return (Control)Activator.CreateInstance(type)!;

        return new TextBlock { Text = "View not found: " + name };
    }

    public bool Match(object? data) => data is ViewModelBase;
}
