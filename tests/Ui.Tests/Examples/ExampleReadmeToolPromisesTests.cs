// brief-paa-12 R-paa12-4 — what example READMEs promise about the Phased Array tool, opened the way the README tells a user to open it.
//
// The list comes from the READMEs: every paragraph that names the Phased Array tool is a promise. Its bench is the cell its section heading
// names, or the example's one bench with an array; its arrays are the ones the paragraph names in backticks, or every array on the bench;
// and each quantity it says the aperture is coloured by ("colour the aperture by …", "the aperture coloured by …") must be on the tool's
// Colour by list. A promise this cannot resolve fails rather than being skipped, so a new example that promises the tool is held without
// editing this file. The tool opens on a copy of the example (the application saves the schematic before it prepares it), as the
// application opens it: the drawing, prepared through PreparedCircuit.FromSchematic, with a settled frame.

using System.Text.RegularExpressions;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.PhasedArray;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.Examples;

[Collection(Sar.FieldCosimulationRunsCollection.Name)]
public sealed partial class ExampleReadmeToolPromisesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("crf-paa12-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static string Examples => Path.Combine(Em3d.PalaceBackendTests.RepoRoot(), "examples");

    /// <summary>One promise: the example, the section it is in, and the paragraph's text with its whitespace collapsed.</summary>
    public sealed record Promise(string Example, string Section, string Text)
    {
        public override string ToString() => $"{Example} / {Section}";
    }

    [GeneratedRegex(@"Phased Array tool|Tools ▸ Phased Array")]
    private static partial Regex ToolPhrase();

    [GeneratedRegex(@"(?:colou?r the aperture by|aperture colou?red by)\s+([^:.;]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ColourPhrase();

    public static IEnumerable<Promise> All()
    {
        foreach (string readme in Directory.EnumerateFiles(Examples, "README.md", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string example = Path.GetFileName(Path.GetDirectoryName(readme)!);
            string section = "";
            foreach (string para in Regex.Split(File.ReadAllText(readme).ReplaceLineEndings("\n"), @"\n\s*\n"))
            {
                string text = Regex.Replace(para.Trim(), @"\s+", " ");
                if (text.StartsWith("## ", StringComparison.Ordinal)) { section = text[3..].Trim(); continue; }
                if (ToolPhrase().IsMatch(text)) yield return new Promise(example, section, text);
            }
        }
    }

    public static TheoryData<string, string, string> Promises()
    {
        var d = new TheoryData<string, string, string>();
        foreach (var p in All()) d.Add(p.Example, p.Section, p.Text);
        return d;
    }

    /// <summary>The finder finds something: an empty list would pass every promise vacuously.</summary>
    [Fact]
    public void TheReadmesPromiseTheTool()
    {
        var promises = All().ToList();
        Assert.Contains(promises, p => p.Example == "Phased Array" && p.Section == "5 Brute Force");
        Assert.Contains(promises, p => p.Example == "mmWave Array Link");
    }

    [Theory]
    [MemberData(nameof(Promises))]
    public void EachPromise_OpensWithAFrame_AndOffersWhatItColoursBy(string example, string section, string text)
    {
        string ws = Path.Combine(_dir, example);
        CopyTree(Path.Combine(Examples, example), ws);

        // the bench: the cell the section names, else the example's one bench that holds an array
        string? bench = section.Length > 0 && File.Exists(Schematic(ws, section)) ? section : null;
        if (bench is null)
        {
            var withArray = Directory.EnumerateDirectories(ws).Select(Path.GetFileName).OfType<string>()
                                     .Where(c => File.Exists(Schematic(ws, c)) && Arrays(Load(Schematic(ws, c))).Any()).ToList();
            Assert.True(withArray.Count == 1,
                        $"{example} / {section}: the README promises the Phased Array tool, but the section names no bench and the example has " +
                        $"{withArray.Count} benches with an array ({string.Join(", ", withArray)}), so which one it means cannot be told.");
            bench = withArray[0];
        }
        string path = Schematic(ws, bench);
        var model = Load(path);
        var arrays = Arrays(model).ToList();
        Assert.True(arrays.Count > 0, $"{example} / {bench}: the README promises the Phased Array tool, and the bench has no Phased Antenna Array.");
        var named = Regex.Matches(text, "`([^`]+)`").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var open = arrays.Where(a => named.Contains(a.InstanceName)).ToList();
        if (open.Count == 0) open = arrays;

        var colours = ColourPhrase().Matches(text).SelectMany(m => Items(m.Groups[1].Value)).ToList();
        foreach (var instance in open)
        {
            var vm = new SchematicViewModel(model);
            using var tool = new PhasedArrayToolViewModel(vm, instance, v =>
            {
                SchematicPersistence.SaveToFile(path, v.EditModel, bench);
                return PreparedCircuit.FromSchematic(path);
            }, new ImmediatePhasedArrayHost());
            Assert.True(tool.Frame is not null, $"{example} / {bench} / {instance.InstanceName}: the tool opened on \"{tool.Status}\"");
            foreach (string colour in colours)
                Assert.True(tool.Quantities.Any(q => Offers(q.Label, colour)),
                            $"{example} / {bench} / {instance.InstanceName}: the README colours the aperture by '{colour}', and the tool offers " +
                            string.Join(", ", tool.Quantities.Select(q => q.Label)) + ".");
        }
    }

    /// <summary>"amplitude, phase or |Γ_act|, the beam …" → amplitude, phase, |Γ_act|: an English list ends at its "or"/"and" item.</summary>
    private static IEnumerable<string> Items(string list)
    {
        foreach (string item in list.Split(", "))
        {
            var last = Regex.Split(item, @"\s+(?:or|and)\s+");
            foreach (string part in last) yield return part.Trim();
            if (last.Length > 1) yield break;
            if (!list.Contains(" or ", StringComparison.Ordinal) && !list.Contains(" and ", StringComparison.Ordinal)) yield break;
        }
    }

    /// <summary>A picker label offers a README's word when one starts the other, letters and digits only ("|Γ_act|" is "|Γ active|").</summary>
    private static bool Offers(string label, string word)
    {
        static string Norm(string s) => new([.. s.ToLowerInvariant().Where(char.IsLetterOrDigit)]);
        string l = Norm(label), w = Norm(word);
        return w.Length > 0 && (l.StartsWith(w, StringComparison.Ordinal) || w.StartsWith(l, StringComparison.Ordinal));
    }

    private static string Schematic(string ws, string cell) => Path.Combine(ws, cell, "schematic", cell + ".csch");

    private static SchematicEditModel Load(string path) => SchematicPersistence.LoadFromFile(path).Item1;

    private static IEnumerable<EditableComponent> Arrays(SchematicEditModel model) => model.Components.Where(c => c.Symbol == SymbolKind.PhasedArray);

    private static void CopyTree(string from, string to)
    {
        foreach (string d in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
        Directory.CreateDirectory(to);
        foreach (string f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories)) File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)));
    }
}
