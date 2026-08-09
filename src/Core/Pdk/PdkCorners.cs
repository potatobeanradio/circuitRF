using System.Text.RegularExpressions;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist.Spice;

namespace CircuitRF.Core.Pdk;

/// <summary>
/// One axis of corner choice a kit offers: the alternatives declared by ONE of its files.
/// </summary>
/// <param name="AxisId">
/// The declaring file, as the kit refers to it. Stable identity — a selection is recorded against
/// this, so it must not be a display string.
/// </param>
/// <param name="DisplayName">The file's own stem, which is the only name the kit gives an axis.</param>
/// <param name="Options">Section names, verbatim and in declaration order — the kit's own vocabulary.</param>
public sealed record PdkCornerAxis(string AxisId, string DisplayName, IReadOnlyList<string> Options);

/// <summary>
/// The corners a kit offers, and what choosing one actually binds.
///
/// <para><b>A corner is a named set of global variable bindings — nothing else.</b> Measured across a
/// kit's capacitor, resistor, diode, bipolar and both MOS corner files: every section binds a
/// handful of process parameters and then includes the SAME shared model file every other section of
/// that file includes. The subcircuits and model cards are identical across corners. That is what
/// makes this a substitution into the testbench's globals rather than a different netlist, a
/// re-import, or a variant of the parts.</para>
///
/// <para><b>One axis per FILE, which is structural rather than a naming convention.</b> A kit states
/// its corners one file per device family, so choosing a capacitor corner and a resistor corner are
/// two independent choices. Flattening them into one list would offer a single pick where the kit
/// offers several.</para>
///
/// <para><b>Nothing here decides which sections are "really" corners.</b> The kit declaring them as
/// alternatives is the whole semantic; filtering on <c>_typ</c>/<c>_wcs</c> would encode one
/// supplier's habits and go blank on the next kit.</para>
/// </summary>
public static class PdkCorners
{
    /// <summary>
    /// The axes a set of netlists declares. A file declaring no section contributes none, which is
    /// nearly every netlist — an axis per file regardless would put an empty picker in front of every
    /// user of every kit.
    /// </summary>
    /// <param name="netlists">
    /// Absolute paths, paired with the identity a selection should be recorded against. The caller
    /// owns that identity because only it knows what a path is relative TO — a kit that moves must
    /// not lose the corner its designs are set to.
    /// </param>
    public static IReadOnlyList<PdkCornerAxis> Discover(
        IEnumerable<(string AbsolutePath, string AxisId)> netlists)
    {
        var axes = new List<PdkCornerAxis>();

        foreach (var (path, axisId) in netlists)
        {
            // A kit's netlists are mostly model libraries — megabytes of subcircuits and model cards
            // that declare no section at all. Parsing every one of them to learn that costs the whole
            // import; a scan for the directive that OPENS a section costs a read. The check is
            // deliberately the same shape the reader's own section handling keys on (a `.lib` with
            // exactly one word after it — two words is a REQUEST, not a declaration), so a file this
            // skips is one the reader would have reported no sections for.
            if (!DeclaresAnySection(path)) continue;

            SpiceNetlistResult read;
            try { read = SpiceNetlistReader.ReadFile(path); }
            catch { continue; }          // a file that will not read declares no corners we can trust

            foreach (var set in read.Sections)
            {
                if (set.Names.Count == 0) continue;
                axes.Add(new PdkCornerAxis(axisId, Path.GetFileNameWithoutExtension(path), set.Names));
            }
        }

        // Stable order, so a panel does not reshuffle between opens.
        return [.. axes.OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Largest file this will read to look for a section declaration. Past it the file is a
    /// data set, not a corner file, and reading it whole would cost more than the answer is worth.</summary>
    private const long SectionScanLimitBytes = 8L * 1024 * 1024;

    private static readonly Regex SectionOpener =
        new(@"^\s*\.lib\s+\S+\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    private static bool DeclaresAnySection(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > SectionScanLimitBytes) return false;
            return SectionOpener.IsMatch(File.ReadAllText(path));
        }
        catch { return false; }
    }

    /// <summary>
    /// What choosing <paramref name="section"/> on <paramref name="axisFile"/> binds.
    ///
    /// <para><b>Requested the way the dialect itself requests a corner</b> — <c>.lib &lt;file&gt;
    /// &lt;section&gt;</c> — rather than by reaching into the file and reading its parameters
    /// directly. That is the format's own mechanism for "read this one alternative", and using it
    /// means the section's conditionals, nested includes and parameter forms are handled by the one
    /// reader that already handles them, instead of by a second grammar that would drift.</para>
    ///
    /// <para><b>What comes back is the section AND whatever it includes, deliberately.</b> A corner
    /// file's section IS the entry point to the model library — that is what it exists to be — so a
    /// caller uses this INSTEAD of reading that library separately, never in addition, or the two
    /// reads bind the same names twice. Measured the overlap is empty in practice: its
    /// model files declare every parameter inside a subcircuit, so a corner's bindings are exactly
    /// its own two or three process constants.</para>
    /// </summary>
    /// <param name="problems">Anything the read could not use, by file and line. Never silently dropped.</param>
    public static IReadOnlyList<Variable> BindingsFor(
        string axisFile, string section, out IReadOnlyList<string> problems)
    {
        problems = [];

        if (string.IsNullOrWhiteSpace(section))
            return [];

        string? dir = Path.GetDirectoryName(Path.GetFullPath(axisFile));
        string file = Path.GetFileName(axisFile);

        SpiceNetlistResult read;
        try
        {
            // Quoted: a kit's own folder may hold a space, and an unquoted path would split into a
            // file and a section that are neither.
            read = SpiceNetlistReader.Read($".lib \"{file}\" \"{section}\"", dir);
        }
        catch (Exception ex)
        {
            problems = [$"'{section}' could not be read from '{file}': {ex.Message}"];
            return [];
        }

        problems = [.. read.Notes.Select(n => n.ToString())];
        return read.Variables;
    }

    /// <summary>
    /// Whether a section is one this axis actually offers. A recorded selection outlives the kit it
    /// was made against — a kit is updated, or repaired to a different copy — so a stale name must be
    /// caught and reported rather than silently binding nothing, which would leave the design at a
    /// corner nobody chose and every number plausible.
    /// </summary>
    public static bool Offers(PdkCornerAxis axis, string section)
        => axis.Options.Any(o => o.Equals(section, StringComparison.OrdinalIgnoreCase));
}
