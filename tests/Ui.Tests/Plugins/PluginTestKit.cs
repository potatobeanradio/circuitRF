using System;
using System.Collections.Generic;
using System.IO;
using CircuitRF.Design.Plugins;
using CircuitRF.Ui.Tests.Layout.PCells;

namespace CircuitRF.Ui.Tests.Plugins;

/// <summary>Skips, with the reason, on a machine missing Python or git — placement's gate needs both.</summary>
public sealed class PythonAndGitFactAttribute : Xunit.FactAttribute
{
    public PythonAndGitFactAttribute()
    {
        if (PythonRunner.Interpreter is null) Skip = "No python3 on PATH — the example plugins are Python.";
        else if (CircuitRF.Ui.Tests.Revision.GitProbe.Reason is { } why) Skip = why;
    }
}

internal static class PluginTestKit
{
    /// <summary><c>tools/plugin-examples</c>, found above the test binary.</summary>
    public static string ExamplesRoot { get; } = Find();

    public static PluginEntry Example(string name) => PluginCatalog.Read(Path.Combine(ExamplesRoot, name), PluginSource.Linked);

    /// <summary>Options that allow <paramref name="entry"/> and nothing else, with plugins on and Python from the
    /// test machine — never the developer's own preferences.</summary>
    public static PluginStartOptions Allowed(PluginEntry entry, string scratch, params string[] extra) => new()
    {
        Trust          = new PluginTrustStore([new KeyValuePair<string, bool>(entry.Directory, true)]),
        Policy         = PluginPolicy.Resolve(null, null, _ => null),
        ScratchRoot    = scratch,
        PythonCommand  = PythonRunner.Interpreter,
        ExtraArguments = extra,
    };

    public static string NewScratch()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-plg-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Find()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            string candidate = Path.Combine(dir, "tools", "plugin-examples");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("tools/plugin-examples not found above the test binary.");
    }
}
