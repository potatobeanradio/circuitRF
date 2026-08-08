using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using CircuitRF.WBond;

namespace CircuitRF.Ui.WBond;

/// <summary>
/// Copy and paste of wires within (and between) wBond editors — wbond.md §6.7.
///
/// <h3>Array membership is carried, and re-created where it is missing</h3>
/// <para>"Preserving array membership where the target array exists and creating it where it does
/// not" is the whole requirement, and it is not cosmetic: the array reduction sums over arrays
/// (§3.4), so a pasted wire that landed in the wrong array — or in none — would be drawn, would be
/// measured, and would report its inductance against the wrong pin.</para>
///
/// <h3>Marker-guarded text, like every other clipboard in this application</h3>
/// <para>The payload rides <c>DataFormat.Text</c> as marker-prefixed JSON, so it survives crossing a
/// process boundary and a foreign paste is silently ignored rather than half-parsed. The same shape
/// <c>SchematicClipboard</c> and <c>LayoutFragment</c> already use — and, as they found the hard way,
/// an in-process typed format writes NOTHING to the macOS pasteboard and crashes the drag.</para>
/// </summary>
public static class WBondClipboard
{
    /// <summary>Checked before anything else is trusted.</summary>
    public const string Marker = "circuitrf/wbond-clipboard-v1";

    /// <summary>One copied wire, plus the array it came from.</summary>
    public sealed class WireEntry
    {
        public string ArrayName { get; set; } = "";

        /// <summary>The source array's own profile, so a re-created array binds the same one.</summary>
        public string? ArrayProfile { get; set; }

        public string? ProfileBinding { get; set; }

        public long DiameterNm { get; set; }

        public string Material { get; set; } = "";

        /// <summary>Flat x,y,z triples — the same convention the `.wBond` file uses.</summary>
        public long[] Points { get; set; } = [];
    }

    public sealed class Payload
    {
        [JsonPropertyName("marker")] public string? Marker { get; set; }

        /// <summary>Profiles the copied wires are bound to, so a paste into another design can rebind.</summary>
        public List<LoopProfile> Profiles { get; set; } = [];

        public List<WireEntry> Wires { get; set; } = [];
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serialises the selected wires. Returns null when the selection carries no whole wire.</summary>
    public static string? Copy(WBondDesign design, WireSelection selection)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(selection);

        var payload = new Payload { Marker = Marker };
        int flat = -1;

        foreach (var array in design.Arrays)
        {
            foreach (var wire in array.Wires)
            {
                flat++;
                if (!selection.TouchedWires().Contains(flat)) continue;

                payload.Wires.Add(new WireEntry
                {
                    ArrayName = array.Name,
                    ArrayProfile = array.Profile,
                    ProfileBinding = wire.ProfileBinding,
                    DiameterNm = wire.DiameterNm,
                    Material = wire.Material,
                    Points = [.. wire.Points.SelectMany(p => new[] { p.X, p.Y, p.Z })],
                });
            }
        }

        if (payload.Wires.Count == 0) return null;

        // Only the profiles actually referenced travel — a paste should not drag a design's entire
        // profile library along with two wires.
        var needed = payload.Wires
            .Select(w => w.ProfileBinding)
            .Concat(payload.Wires.Select(w => w.ArrayProfile))
            .Where(n => n is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var name in needed)
            if (design.ProfileByName(name!) is { } profile) payload.Profiles.Add(profile);

        return JsonSerializer.Serialize(payload, JsonOpts);
    }

    /// <summary>
    /// Parses a payload. Returns null for anything that is not ours — a foreign clipboard is a
    /// no-op, never a half-applied paste.
    /// </summary>
    public static Payload? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.Contains(Marker, StringComparison.Ordinal))
            return null;

        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(text, JsonOpts);
            return payload?.Marker == Marker && payload.Wires.Count > 0 ? payload : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Adds the payload's wires to <paramref name="design"/>, offset by the given displacement.
    ///
    /// <para>Each wire rejoins an array of its original NAME — found, or created carrying the same
    /// profile. A missing profile is added from the payload, so a paste between two designs keeps the
    /// loop shape rather than silently rebinding to whatever the destination happened to call its
    /// first profile.</para>
    /// </summary>
    /// <returns>How many wires were added.</returns>
    public static int Paste(WBondDesign design, Payload payload, long dxNm, long dyNm, long dzNm = 0)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(payload);

        foreach (var profile in payload.Profiles)
            if (design.ProfileByName(profile.Name) is null)
                design.Profiles.Add(profile);

        int added = 0;

        foreach (var entry in payload.Wires)
        {
            if (entry.Points.Length < 6 || entry.Points.Length % 3 != 0) continue;   // needs 2+ points

            var array = design.Arrays.FirstOrDefault(
                a => string.Equals(a.Name, entry.ArrayName, StringComparison.OrdinalIgnoreCase));

            if (array is null)
            {
                array = new WireArray { Name = entry.ArrayName, Profile = entry.ArrayProfile };
                design.Arrays.Add(array);
            }

            var wire = new Wire
            {
                DiameterNm = entry.DiameterNm > 0 ? entry.DiameterNm : WBondDefaults.ShippedDiameterNm,
                Material = string.IsNullOrWhiteSpace(entry.Material) ? WBondDefaults.ShippedMaterial : entry.Material,
                ProfileBinding = entry.ProfileBinding,
            };

            for (int i = 0; i + 2 < entry.Points.Length; i += 3)
                wire.Points.Add(new Point3(entry.Points[i] + dxNm,
                                           entry.Points[i + 1] + dyNm,
                                           entry.Points[i + 2] + dzNm));

            array.Wires.Add(wire);
            added++;
        }

        return added;
    }
}
