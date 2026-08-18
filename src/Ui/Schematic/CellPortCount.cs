using System.Globalization;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// How many ports a cell has, asked of the cell itself rather than of one declaration on it.
///
/// <para><b>Why this exists.</b> <c>.ccell</c>'s <see cref="CcellFile.NumPorts"/> is the declared
/// authority, but nothing in circuitRF ever DERIVES it — it is written by the Cell Parameter editor
/// and by the PDK installer, and by nothing else. So a cell whose schematic the user drew with N
/// <see cref="SymbolKind.Pin"/> components, and whose cell editor they never happened to open, declares
/// <b>zero</b> ports. Every consumer that then falls back to a fixed default (auto-symbol generation
/// used 2) produces a symbol with the wrong number of pins for a cell that says quite plainly, in its
/// own schematic, how many it has.</para>
///
/// <para>The schematic's <c>Pin</c> components ARE the cell's ports — that is what placing one means —
/// so reading them is not a guess. The declaration still wins when it says anything at all, because a
/// user who set it meant it.</para>
/// </summary>
public static class CellPortCount
{
    /// <summary>Ports for <paramref name="cellDir"/>, or 0 when neither source says. Callers that need
    /// a number regardless can pass 0 straight to <c>AutoSymbolGenerator.Generate</c>, which applies its
    /// own documented default — the fallback is stated in exactly one place that way.</summary>
    public static int Resolve(string cellDir)
    {
        if (FromCcell(cellDir) is > 0 and var declared) return declared;
        return FromSchematic(cellDir) ?? 0;
    }

    /// <summary>The cell's declared <c>NumPorts</c>, or null when there is no readable
    /// <c>.ccell</c>.</summary>
    public static int? FromCcell(string cellDir)
    {
        try
        {
            string path = Path.Combine(cellDir, CellFolder.CcellFileName);
            return File.Exists(path) ? CellPersistence.LoadFromFile(path).NumPorts : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Ports counted from the cell's PRIMARY schematic, or null when it has none / cannot be read.
    ///
    /// <para>The answer is the HIGHEST port number declared, not the number of pins: a cell whose pins
    /// are numbered 1, 2 and 4 has four ports with the third left open, and returning three would
    /// renumber the user's own port 4 into a port 3 that connects somewhere else. An unnumbered or
    /// unparseable pin still counts as a port — it is on the schematic — so the count is the larger of
    /// the two readings.</para>
    /// </summary>
    public static int? FromSchematic(string cellDir)
    {
        try
        {
            var primary = CellFolder.ResolvePrimary(cellDir, ViewType.Schematic);
            if (primary.State is not (PrimaryState.SoleFile or PrimaryState.NamedPresent)) return null;
            if (primary.ResolvedName is not { Length: > 0 } name) return null;

            string path = Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Schematic), name);
            if (!File.Exists(path)) return null;

            var (model, _, _) = SchematicPersistence.LoadFromFile(path);

            int pins = 0, highest = 0;
            foreach (var comp in model.Components)
            {
                if (comp.Symbol != SymbolKind.Pin) continue;
                if (comp.Disable is DisableState.Open or DisableState.Short) continue;
                pins++;

                var num = comp.Parameters.FirstOrDefault(p => p.Name == "Num");
                if (num is not null
                    && int.TryParse(num.Expression?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int n)
                    && n > highest)
                    highest = n;
            }

            int ports = Math.Max(pins, highest);
            return ports > 0 ? ports : null;
        }
        catch
        {
            return null;
        }
    }
}
