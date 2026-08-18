using System.Globalization;

namespace CircuitRF.WBond;

/// <summary>
/// Imports a wirebond table — the normal interchange for packaging flows (R-wb-12,
/// <c>mom-wirebond-kernel.md</c> RW16).
///
/// <para><b>This is how a 600-wire design actually arrives.</b> Hand-placing 600 wires in a GUI is
/// not a workflow anyone will use, and every packaging flow already has this table. It is also what
/// makes WB-A demonstrable before any editor exists.</para>
///
/// <h3>Format</h3>
/// <code>
/// # units: mil
/// array,x1,y1,z1,x2,y2,z2,diameter,material
/// G1,0,0,4,100,0,1,1.0,Gold
/// G1,0,6,4,100,6,1,1.0,Gold
/// </code>
/// <para>Column order is taken from the header, not assumed. <c>diameter</c> and <c>material</c> are
/// optional and fall back to the import defaults. A <c>#</c> line is a comment;
/// <c># units: &lt;unit&gt;</c> sets the unit for every coordinate and diameter that follows.</para>
///
/// <h3>Every wire is generated from the seed arch (2026-08-18)</h3>
/// <para>Loop profiles — and the ball/wedge designation with them — no longer exist: a wire's points
/// are the only truth about its shape (<see cref="LoopShape"/>). Every imported wire is therefore
/// arched with <see cref="LoopShape.Seed"/> at the table's <c>loopheight</c>, and the user reshapes
/// whichever ones they want to.</para>
///
/// <para><b>A <c>profile</c> column is READ AND IGNORED, not refused.</b> This importer used to write
/// that header itself, so a table carrying it is a file this tool produced — starting to reject it
/// would break exactly the users who followed the documented format.</para>
///
/// <h3>Errors</h3>
/// <para><b>A malformed row reports its line number and what was expected — it is never skipped
/// silently.</b> A silently dropped wire is an inductance that is quietly too high, which is the
/// worst kind of wrong: plausible, and in the optimistic direction.</para>
/// </summary>
public static class WireTableCsv
{
    private static readonly string[] RequiredColumns = ["array", "x1", "y1", "z1", "x2", "y2", "z2"];

    /// <summary>
    /// Import settings; each is the fallback for a column the table does not carry.
    ///
    /// <para><b>A class, not a record struct with defaulted primary-constructor parameters.</b>
    /// For a struct, <c>new ImportSettings()</c> and <c>default</c> both bypass the primary
    /// constructor and produce all-zero fields — so "1 mil gold, 7 points" would silently become
    /// "0 nm, no material, 0 points". Property initialisers on a class actually run.</para>
    /// </summary>
    public sealed class ImportSettings
    {
        /// <summary>Unit for coordinates and diameters, unless the file declares its own.</summary>
        public WBondUnit Units { get; init; } = WBondUnit.Mil;

        /// <summary>Wire diameter, in <see cref="Units"/>.</summary>
        public double DefaultDiameter { get; init; } = 1.0;

        /// <summary>Metal name; gold by default (D7).</summary>
        public string DefaultMaterial { get; init; } = WireMaterials.Default.Name;

        /// <summary>Loop height, in <see cref="Units"/>, every generated wire is arched to.</summary>
        public double DefaultLoopHeight { get; init; } = 20.0;

        /// <summary>Points in a generated wire; 7 is the shipped default.</summary>
        public int PointsPerWire { get; init; } = 7;
    }

    /// <summary>Parses a wirebond table into a design.</summary>
    /// <exception cref="InvalidDataException">
    /// The header is missing a required column, or a row is malformed. The message names the line.
    /// </exception>
    public static WBondDesign Read(string csv, ImportSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(csv);
        settings ??= new ImportSettings();

        var units = settings.Units;
        var design = new WBondDesign();
        var arraysByName = new Dictionary<string, WireArray>(StringComparer.OrdinalIgnoreCase);

        Dictionary<string, int>? columns = null;
        var lines = csv.Split('\n');

        for (int lineNumber = 1; lineNumber <= lines.Length; lineNumber++)
        {
            string line = lines[lineNumber - 1].Trim().TrimEnd('\r');
            if (line.Length == 0) continue;

            if (line.StartsWith('#'))
            {
                TryReadUnitsDirective(line, lineNumber, ref units);
                continue;
            }

            var fields = line.Split(',').Select(f => f.Trim()).ToArray();

            if (columns is null)
            {
                columns = ReadHeader(fields, lineNumber);
                continue;
            }

            ReadRow(fields, columns, lineNumber, units, settings, design, arraysByName);
        }

        if (columns is null)
            throw new InvalidDataException(
                "The wirebond table has no header row. Expected a line naming at least: " +
                string.Join(", ", RequiredColumns) + ".");

        return design;
    }

    public static WBondDesign ReadFile(string path, ImportSettings? settings = null) =>
        Read(File.ReadAllText(path), settings);

    private static void TryReadUnitsDirective(string line, int lineNumber, ref WBondUnit units)
    {
        int colon = line.IndexOf(':');
        if (colon < 0) return;

        string key = line[1..colon].Trim();
        if (!key.Equals("units", StringComparison.OrdinalIgnoreCase)) return;

        string value = line[(colon + 1)..].Trim();
        if (!WBondUnits.TryParseUnit(value, out units))
            throw new InvalidDataException(
                $"Line {lineNumber}: '# units: {value}' names an unknown unit. " +
                "Expected one of nm, um, mm, mil, in.");
    }

    private static Dictionary<string, int> ReadHeader(string[] fields, int lineNumber)
    {
        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < fields.Length; i++)
        {
            if (fields[i].Length == 0) continue;
            if (!columns.TryAdd(fields[i], i))
                throw new InvalidDataException(
                    $"Line {lineNumber}: the header names column '{fields[i]}' twice.");
        }

        var missing = RequiredColumns.Where(c => !columns.ContainsKey(c)).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException(
                $"Line {lineNumber}: the wirebond table header is missing required column(s) " +
                $"{string.Join(", ", missing)}. Found: {string.Join(", ", columns.Keys)}.");

        return columns;
    }

    private static void ReadRow(
        string[] fields, Dictionary<string, int> columns, int lineNumber, WBondUnit units,
        ImportSettings settings, WBondDesign design,
        Dictionary<string, WireArray> arraysByName)
    {
        string arrayName = Field(fields, columns, "array", lineNumber)
            ?? throw new InvalidDataException($"Line {lineNumber}: the 'array' column is empty. Every wire belongs to exactly one array.");

        var start = new Point3(
            Coordinate(fields, columns, "x1", lineNumber, units),
            Coordinate(fields, columns, "y1", lineNumber, units),
            Coordinate(fields, columns, "z1", lineNumber, units));

        var end = new Point3(
            Coordinate(fields, columns, "x2", lineNumber, units),
            Coordinate(fields, columns, "y2", lineNumber, units),
            Coordinate(fields, columns, "z2", lineNumber, units));

        if (start == end)
            throw new InvalidDataException(
                $"Line {lineNumber}: the wire's two feet are at the same point. A wire needs a span.");

        string material = Field(fields, columns, "material", lineNumber)
                          ?? settings.DefaultMaterial;

        long diameter = columns.ContainsKey("diameter")
            ? Coordinate(fields, columns, "diameter", lineNumber, units)
            : WBondUnits.ToNm(settings.DefaultDiameter, units);

        if (diameter <= 0)
            throw new InvalidDataException($"Line {lineNumber}: wire diameter must be positive, got {diameter} nm.");

        if (!arraysByName.TryGetValue(arrayName, out var array))
        {
            array = new WireArray { Name = arrayName };
            arraysByName[arrayName] = array;
            design.Arrays.Add(array);
        }

        array.Wires.Add(LoopShape.CreateSeedWire(
            start, end, diameter, material,
            WBondUnits.ToNm(settings.DefaultLoopHeight, units), settings.PointsPerWire));
    }

    private static string? Field(string[] fields, Dictionary<string, int> columns, string name, int lineNumber)
    {
        if (!columns.TryGetValue(name, out int index)) return null;
        if (index >= fields.Length)
            throw new InvalidDataException(
                $"Line {lineNumber}: has {fields.Length} field(s) but the header declares column '{name}' " +
                $"at position {index + 1}.");

        string value = fields[index];
        return value.Length == 0 ? null : value;
    }

    private static long Coordinate(string[] fields, Dictionary<string, int> columns, string name,
                                   int lineNumber, WBondUnit units)
    {
        string? text = Field(fields, columns, name, lineNumber)
            ?? throw new InvalidDataException($"Line {lineNumber}: column '{name}' is empty; a coordinate is required.");

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            throw new InvalidDataException(
                $"Line {lineNumber}: column '{name}' contains '{text}', which is not a number.");

        if (!double.IsFinite(value))
            throw new InvalidDataException($"Line {lineNumber}: column '{name}' is {text}, which is not finite.");

        return WBondUnits.ToNm(value, units);
    }
}
