using CircuitRF.Core.Netlist;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf netlist &lt;path.cnl&gt; --to-schematic [-o out.csch]</c> — the extraction run
/// backwards: a drawn schematic from a netlist (brief-agent-authoring-overview.md AA-6).
///
/// <para><b>It owns no reading, placement or routing.</b> The netlist is read and drawn by
/// <see cref="NetlistSchematic.FromFile"/> in <c>src/Design</c>, below the firewall — the same call a plugin's
/// <c>schematic</c> view makes (brief-plg-3 §4), so the panel and this verb cannot draw one netlist two ways. What is here is argument parsing,
/// refusals and reporting, on <c>src/Cli/Authoring.cs</c>' terms.</para>
///
/// <para><b>A flag, not an inference from <c>-o</c>'s extension.</b> Everywhere else the verb's
/// direction is "a schematic becomes a netlist", and a <c>.cnl</c> given to it is a refusal; reversing
/// the direction silently because <c>-o</c> happened to end in <c>.csch</c> would make one typo turn
/// an extraction into a drawing. The flag says which way the caller meant.</para>
///
/// <para><b>The netlist is read as written</b>, with <see cref="CnlReader"/> and NOT with the
/// technology binding a run uses: the binding writes the workspace's substrate INTO each microstrip
/// line, and a drawing that carried those numbers would carry them twice once its own extraction
/// bound the same technology again. Left unbound, the drawing means exactly what the netlist meant
/// wherever it is saved.</para>
/// </summary>
internal static class NetlistToSchematic
{
    public static int Run(string path, DocumentKind kind, string? output)
    {
        if (kind != DocumentKind.Netlist)
            return JsonRun.Fail(CliDiagnostics.NetlistToSchematicNotANetlist(path, DocumentKinds.Name(kind)));

        if (output is not null
            && !string.Equals(Path.GetExtension(output), ".csch", StringComparison.OrdinalIgnoreCase))
            return JsonRun.Fail(CliDiagnostics.NetlistToSchematicOutputNotCsch(
                output, Path.GetExtension(output) is { Length: > 0 } e ? e : "(none)"));

        string full = Path.GetFullPath(path);
        string target = output is not null ? Path.GetFullPath(output) : full;
        string targetDir = Path.GetDirectoryName(target)!;

        NetlistSchematicResult drawn;
        try { drawn = NetlistSchematic.FromFile(full, targetDir); }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.NetlistToSchematicUnreadable(path, ex.Message)); }

        if (drawn.Schematic is null)
        {
            // Every cause, not the first: a caller who fixes one and runs again to find the next
            // is doing by hand what this loop does for free.
            for (int i = 0; i < drawn.Refusals.Count - 1; i++)
                JsonRun.Report(CliDiagnostics.NetlistToSchematicRefused(path, drawn.Refusals[i]));
            return JsonRun.Fail(CliDiagnostics.NetlistToSchematicRefused(path, drawn.Refusals[^1]));
        }

        foreach (string note in drawn.Notes)
        {
            Console.Error.WriteLine("warning: " + note);
            JsonRun.Note(CliDiagnostics.NetlistToSchematicNote(note));
        }

        string cellName = Path.GetFileNameWithoutExtension(target);
        string json = SchematicPersistence.Serialize(drawn.Schematic, cellName);

        if (output is null)
        {
            // The relative references in it were written against the netlist's own folder, which
            // is where `> name.csch` beside it puts the file.
            JsonRun.Document = new RfCore.Export.DocumentJson(
                full, DocumentKinds.Name(DocumentKind.Schematic), json);
            Console.Out.Write(json);
            Console.Error.WriteLine($"[circuitRF] drew {Path.GetFileName(path)} "
                                  + $"({drawn.Schematic.Components.Count} symbol(s), {drawn.Schematic.Wires.Count} wire(s))");
            return 0;
        }

        try
        {
            Directory.CreateDirectory(targetDir);
            File.WriteAllText(target, json, new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.NetlistWriteFailed(output, ex.Message)); }

        JsonRun.AddOutput("csch", output);
        Console.WriteLine(output);
        Console.Error.WriteLine($"[circuitRF] drew {Path.GetFileName(path)} -> {output}");
        return 0;
    }
}
