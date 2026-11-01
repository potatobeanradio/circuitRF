// brief-em3d-75 — the thermal editor: drawing heat sources, probes and mesh regions (R-em3d75-1), boundaries and contacts
// from faces (-2), Plot Temperature and its readouts (-4), and the probe table (-4e).
//
// A THERMAL PLACE IS A RECORD, NOT AN OBJECT (overview §1c): it is drawn in the overlay — never in the scene — so it is never
// a solid, never a face to snap onto, and never seen by an EM lowering. Every edit of one is a records edit (C3dRecordsEdit
// carries the four lists), one undo entry, and writes exactly brief 73's record. A place's hide is the view's alone: the
// format has no Hidden for a place, and a hidden probe still reads.
//
// BOUNDARIES AND CONTACTS ARE THE ACTIVE THERMAL SETUP'S (-2): a face's Fixed Temperature… or Convection… writes that
// setup's Boundaries; a contact override writes the document's ContactResistances (brief 73 R-em3d73-4d), prefilled with the
// technology's pair value when it states one. Nothing here holds state a file does not: the Setups dialog's thermal page
// (EmSetupEditorViewModel.Thermal) edits the same Thermal section.

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Thermal;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Render.Scene3D.Fields;
using CircuitRF.Ui.ThreeD.Tools;
using CircuitRF.Ui.Viewer3D;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ThreeD;

/// <summary>R-em3d75-4e — one row of the probe table: a probe statistic or a measure, at the step shown and (on request) at
/// every step.</summary>
public sealed record C3dProbeTableRow(string Name, string Unit, string Value, IReadOnlyList<string> PerPoint, bool Crossed, bool IsMeasure)
{
    public string AllPoints => string.Join("   ", PerPoint);
}

/// <summary>R-em3d75-4d — a line of temperature: a line probe's, or Temperature Along's. brief-em3d-79: or a probe row against the
/// sweep, or against a carried circuit cube — then <see cref="XLabel"/> names the x axis, and the x values are plotted as they are
/// (a distance is metres, drawn in µm).</summary>
public sealed record C3dThermalLine(string Title, double[] DistanceM, double[] ValuesC, string? XLabel = null, string YLabel = "T (°C)",
                                    bool LogX = false, double UnitsPerMetre = 1e6, string UnitSuffix = "µm")
{
    /// <summary>The temperature AT each end — NaN when that end lies outside every solid (a pick on an exterior face rounded a hair
    /// out): the readout then says so, rather than quoting ΔT between two samples that are not the points picked.</summary>
    public double FromC => ValuesC.Length > 0 ? ValuesC[0] : double.NaN;
    public double ToC => ValuesC.Length > 0 ? ValuesC[^1] : double.NaN;
    public double DeltaC => ToC - FromC;
    public double LengthM => DistanceM.Length > 0 ? DistanceM[^1] : 0;
}

public sealed partial class C3dEditorViewModel
{
    /// <summary>The tree's kinds for the thermal rows.</summary>
    public const string HeatSourceKind = "Heat source", ProbeKind = "Probe", MeshRegionKind = "Mesh region", ThermalBoundaryKindName = "Thermal boundary",
                        EffectiveBlockKind = "Effective block", SymmetryPlaneKind = "Symmetry plane";

    // ── visibility (R-em3d75-1b) ─────────────────────────────────────────────────────────────

    /// <summary>View ▸ Heat Sources: the heat sources drawn in the overlay.</summary>
    [ObservableProperty] private bool _showHeatSources = true;
    /// <summary>View ▸ Probes.</summary>
    [ObservableProperty] private bool _showProbes = true;
    /// <summary>View ▸ Mesh Regions.</summary>
    [ObservableProperty] private bool _showMeshRegions = true;

    private readonly HashSet<string> _hiddenPlaces = new(StringComparer.Ordinal);

    partial void OnShowHeatSourcesChanged(bool value) => Viewer.RequestFrame();
    partial void OnShowProbesChanged(bool value) => Viewer.RequestFrame();
    partial void OnShowMeshRegionsChanged(bool value) => Viewer.RequestFrame();

    /// <summary>Whether the place <paramref name="name"/> is drawn: its kind's switch, and its own row's tick.</summary>
    public bool IsPlaceShown(string name) => !_hiddenPlaces.Contains(name);

    // brief-em3d-90 R-em3d90-2 — a thermal boundary's and a symmetry plane's tick are the VIEW's, as a place's is: neither
    // record has a Hidden in its file and should not gain one, so nothing is saved and nothing is undoable. A boundary's key
    // is its tint's name qualified by the active setup's, so one setup's hidden boundary never hides another setup's on the
    // same face.
    private readonly HashSet<string> _hiddenBoundaries = new(StringComparer.Ordinal);
    private readonly HashSet<C3dAxis> _hiddenPlanes = [];

    private string BoundaryKey(string row) => (ActiveSetupName ?? "") + "\n" + row;

    /// <summary>Whether the active setup's boundary <paramref name="row"/> (<c>thermal:die/zmin</c>) is drawn.</summary>
    public bool IsBoundaryShown(string row) => !_hiddenBoundaries.Contains(BoundaryKey(row));

    /// <summary>A thermal boundary's tick: its tint is left out of the next build (ThermalTints), or put back.</summary>
    public void SetBoundaryShown(string row, bool shown)
    {
        bool changed = shown ? _hiddenBoundaries.Remove(BoundaryKey(row)) : _hiddenBoundaries.Add(BoundaryKey(row));
        if (AllTreeItems().FirstOrDefault(t => t.Kind == ThermalBoundaryKindName && t.Name == row) is { } r) r.Sync(shown);
        if (changed) Viewer.Regenerate();
    }

    /// <summary>The active setup's hidden boundaries, by tint name, for the build's snapshot (data: the build thread never
    /// reads the editor's sets); null for none.</summary>
    private IReadOnlySet<string>? HiddenTintsOfActiveSetup()
    {
        if (_hiddenBoundaries.Count == 0) return null;
        string prefix = (ActiveSetupName ?? "") + "\n";
        var set = _hiddenBoundaries.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Select(k => k[prefix.Length..]).ToHashSet(StringComparer.Ordinal);
        return set.Count > 0 ? set : null;
    }

    /// <summary>Whether the symmetry plane normal to <paramref name="axis"/> is drawn.</summary>
    public bool IsSymmetryPlaneShown(C3dAxis axis) => !_hiddenPlanes.Contains(axis);

    /// <summary>A symmetry plane's tick (its row is <c>symmetry:X</c>): the overlay draws it, or not.</summary>
    public void SetSymmetryPlaneShown(string row, bool shown)
    {
        if (!row.StartsWith(SymmetryRowPrefix, StringComparison.Ordinal) || !Enum.TryParse<C3dAxis>(row[SymmetryRowPrefix.Length..], out var axis)) return;
        if (shown) _hiddenPlanes.Remove(axis); else _hiddenPlanes.Add(axis);
        if (AllTreeItems().FirstOrDefault(t => t.Kind == SymmetryPlaneKind && t.Name == row) is { } r) r.Sync(shown);
        Viewer.RequestFrame();
    }

    /// <summary>Every thermal place's name, which is unique across the document's names (brief 73 R-em3d73-4).</summary>
    private IEnumerable<string> ThermalPlaceNames()
        => Document.HeatSources.Select(h => h.Name).Concat(Document.Probes.Select(p => p.Name)).Concat(Document.MeshRegions.Select(m => m.Name))
                   .Concat(Document.EffectiveBlocks.Select(b => b.Name));

    // ── the tools (R-em3d75-1a) ──────────────────────────────────────────────────────────────

    /// <summary>R-em3d75-1a — the cursor's point ON a face, and that face as the document names it (<c>object/face</c>): a
    /// geometry snap where one is in force, else the surface under the cursor. Null, with the reason, off every face.</summary>
    public C3dPoint3? SurfacePoint(in C3dDrawInput input, out string? face, out string? refusal)
    {
        face = null;
        refusal = null;
        uint id;
        int f;
        C3dPoint3 at;
        if (input.Snap is { } s && input.SnapOnGeometry && Viewer.Snap.IsSnap)
        {
            (id, f, at) = (Viewer.Snap.Object, Viewer.Snap.Face, s);
        }
        else if (Viewer.CursorWorld is { } w && Viewer.LastPick is { Object: > 0 } pick)
        {
            double per = C3dLowering.Metres(1, Document.DbuPerMicron);
            long R(double m) => (long)Math.Round(m / per, MidpointRounding.AwayFromZero);
            (id, f, at) = (pick.Object, pick.Face, new C3dPoint3(R(w.X), R(w.Y), R(w.Z)));
        }
        else
        {
            refusal = "Click on a face of a solid.";
            return null;
        }
        if (Viewer.Scene.Object(id) is { } o && f >= 0 && o.Kind is not (Scene3DKind.Port or Scene3DKind.Boundary or Scene3DKind.Air))
            face = $"{o.Name}/{o.FaceName(f)}";
        return at;
    }

    /// <summary>A record tool finished: its place inserted (one entry), and what it asks after — a heat source's name and
    /// power, a mesh region's element size.</summary>
    private void CommitRecordTool(IC3dRecordTool tool)
    {
        if (tool.TakeMade() is not { } made) return;
        AddThermalPlace(made);
        ToolCommits++;
        switch (made)
        {
            case C3dHeatSource h:
                AskHeatSource(h.Name);
                break;
            case C3dEffectiveBlock b:
                StatusMessage = $"Drew effective block '{b.Name}', DISABLED: the geometry is solved as drawn until you enable it (its row's menu, " +
                                "or Properties). It replaces board dielectric, planes and via barrels only.";
                break;
            case C3dMeshRegion m:
                TextRequested?.Invoke($"Mesh region {m.Name}", $"Target element size inside the box ({LayoutUnits.Suffix(Document.DisplayUnit)}, or an expression):",
                    SpellMicrons(m.SizeUm), text => SetPlaceFieldText(m.Name, nameof(C3dMeshRegion.SizeUm), text));
                break;
        }
    }

    /// <summary>R-em3d75-1a — a new heat source's name, then its default power.</summary>
    private void AskHeatSource(string name)
        => TextRequested?.Invoke($"Heat source {name}", "Name:", name, text =>
        {
            string n = text.Trim();
            if (RenameThermalPlace(name, n) is { } why) return why;
            TextRequested?.Invoke($"Heat source {n}", "Default power, W (a number or an expression such as Pdiss; a thermal setup may override it):",
                Document.HeatSources.FirstOrDefault(h => h.Name == n)?.Power ?? "1", p => SetPlaceText(n, "Power", p));
            return null;
        });

    private string SpellMicrons(double um)
        => (um * 1e-6 / (CircuitRF.Core.Expressions.Units.Scale(LayoutUnits.AsciiSuffix(Document.DisplayUnit)) ?? 1e-6))
           .ToString("0.#######", CultureInfo.InvariantCulture);

    /// <summary>Inserts a heat source, probe or mesh region: one undo entry.</summary>
    public void AddThermalPlace(object place)
    {
        // a new place starts shown: a name handed out again (the lowest free one) must not inherit a deleted place's hidden tick
        if (place switch { C3dHeatSource h => h.Name, C3dProbe p => p.Name, C3dMeshRegion m => m.Name, C3dEffectiveBlock b => b.Name, _ => null } is { } added)
            _hiddenPlaces.Remove(added);
        switch (place)
        {
            case C3dHeatSource h:
                ChangeRecords($"Add heat source {h.Name}", d => d.HeatSources.Add(h));
                StatusMessage = $"Added heat source '{h.Name}'. Its power is the document's default; a thermal setup may override it.";
                break;
            case C3dProbe p:
                ChangeRecords($"Add probe {p.Name}", d => d.Probes.Add(p));
                StatusMessage = $"Added probe '{p.Name}' ({string.Join(", ", p.Kinds()).ToLowerInvariant()}).";
                break;
            case C3dMeshRegion m:
                ChangeRecords($"Add mesh region {m.Name}", d => d.MeshRegions.Add(m));
                StatusMessage = $"Added mesh region '{m.Name}'.";
                break;
            case C3dEffectiveBlock b:
                ChangeRecords($"Add effective block {b.Name}", d => d.EffectiveBlocks.Add(b));
                break;
        }
    }

    /// <summary>Renames a thermal place, and every setup's reference to it: a source override's name, a probe's name in
    /// a measure, the Rth and Z_th lists, and a submodel's region. Null on success, else why not.</summary>
    public string? RenameThermalPlace(string old, string name)
    {
        name = name.Trim();
        if (name == old) return null;
        if (name.Length == 0) return "A name cannot be empty.";
        if (!Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$"))
            return $"'{name}' cannot be a place's name: use letters, digits and _, starting with a letter — a measure names a probe by it.";
        // check's own uniqueness rule: across objects, instances, ports and thermal places, IGNORING CASE — so a probe renamed
        // 'Die' beside an object 'die' is refused here, not by check after it was written. The place itself may change case.
        var used = Document.Objects.Select(o => o.Name).Concat(Document.Instances.Select(i => i.Name)).Concat(Document.Ports.Select(p => p.Name))
                                   .Concat(ThermalPlaceNames()).Concat(NestedNames())
                                   .Where(n => !string.Equals(n, old, StringComparison.Ordinal));
        if (used.Contains(name, StringComparer.OrdinalIgnoreCase)) return $"This 3D view already has something named '{name}'.";
        // the hidden set learns the new name BEFORE the edit rebuilds the tree (so its tick reads hidden), and keeps the old
        // one, so an undo of the rename finds the place still hidden
        if (_hiddenPlaces.Contains(old)) _hiddenPlaces.Add(name);
        ChangeRecords($"Rename {old} to {name}", d =>
        {
            foreach (var h in d.HeatSources.Where(h => h.Name == old)) h.Name = name;
            foreach (var p in d.Probes.Where(p => p.Name == old)) p.Name = name;
            foreach (var m in d.MeshRegions.Where(m => m.Name == old)) m.Name = name;
            foreach (var b in d.EffectiveBlocks.Where(b => b.Name == old)) b.Name = name;
            for (int i = 0; i < d.Setups.Count; i++)
            {
                if (EmSetupPersistence.FromEmbedded(d.Setups[i]) is not { Thermal: { } t } s) continue;
                bool changed = false;
                foreach (var src in t.Sources ?? []) if (src.Name == old) { src.Name = name; changed = true; }
                changed |= Rename(t.Rth?.Sources) | Rename(t.Zth?.Sources) | Rename(t.Zth?.Probes);
                if (t.Submodel is { } sm && sm.Region == old) { sm.Region = name; changed = true; }
                // a measure reads a probe inside Tmax(…)/T(…): only there is it renamed, through the tokenizer, on the right-hand
                // side — never a VAR or a measure's own name that happens to be spelled the same
                if (t.Measures is { } ms)
                    for (int k = 0; k < ms.Count; k++)
                        if (ms[k].IndexOf('=') is > 0 and var eq &&
                            C3dExpressionText.RenameProbe(ms[k][(eq + 1)..], old, name) is var rhs && rhs != ms[k][(eq + 1)..])
                        { ms[k] = ms[k][..(eq + 1)] + rhs; changed = true; }
                if (changed) d.Setups[i] = EmSetupPersistence.ToEmbedded(s);
            }
        });
        return null;

        bool Rename(List<string>? names)
        {
            if (names is null) return false;
            bool any = false;
            for (int k = 0; k < names.Count; k++) if (names[k] == old) { names[k] = name; any = true; }
            return any;
        }
    }

    /// <summary>Deletes a thermal place: one undo entry. A setup that still names it says so through <c>check</c>.</summary>
    public void DeleteThermalPlace(string name)
        => ChangeRecords($"Delete {name}", d =>
        {
            d.HeatSources.RemoveAll(h => h.Name == name);
            d.Probes.RemoveAll(p => p.Name == name);
            d.MeshRegions.RemoveAll(m => m.Name == name);
            d.EffectiveBlocks.RemoveAll(b => b.Name == name);
        });

    /// <summary>The place named <paramref name="name"/>, or null.</summary>
    public object? ThermalPlace(string name)
        => (object?)Document.HeatSources.FirstOrDefault(h => h.Name == name) ?? (object?)Document.Probes.FirstOrDefault(p => p.Name == name)
           ?? (object?)Document.MeshRegions.FirstOrDefault(m => m.Name == name) ?? Document.EffectiveBlocks.FirstOrDefault(b => b.Name == name);

    /// <summary>R-em3d75-1c — one DIMENSION of a place (a number in the display unit, or an expression bound to it), through
    /// the one field write every object's dimension goes through. Null on success.</summary>
    public string? SetPlaceFieldText(string name, string path, string text)
    {
        text = text.Trim();
        if (text.Length == 0) return "Type a number or an expression.";
        return EditNames($"Set {name} {path} = {text}", (doc, _) =>
        {
            object? item = (object?)doc.HeatSources.FirstOrDefault(h => h.Name == name) ?? (object?)doc.Probes.FirstOrDefault(p => p.Name == name)
                           ?? (object?)doc.MeshRegions.FirstOrDefault(m => m.Name == name) ?? doc.EffectiveBlocks.FirstOrDefault(b => b.Name == name);
            return item is null ? $"This 3D view has no thermal place named '{name}'." : WriteField(doc, item, name, path, text, PlaceFieldLabel(item, path));
        });
    }

    /// <summary>The Inspector's label of a place's dimension: <c>Corner x</c>, <c>Diameter</c>, <c>Element size</c>.</summary>
    public static string PlaceFieldLabel(object item, string path)
    {
        static string Xyz(char k) => k switch { '0' => "x", '1' => "y", _ => "z" };
        int b = path.IndexOf('[');
        char k = b >= 0 && b + 1 < path.Length ? path[b + 1] : '0';
        string head = b >= 0 ? path[..b] : path;
        return head switch
        {
            "Sheet.Offset" => "Plane offset",
            "Sheet.Rect.Min" => $"Corner {(k == '0' ? "u" : "v")}",
            "Sheet.Rect.Size" => $"{(k == '0' ? "U" : "V")} size",
            "Point" => $"Point {Xyz(k)}",
            "Spot.Center" => $"Centre {Xyz(k)}",
            "Spot.Diameter" => "Diameter",
            "Line.From" => $"From {Xyz(k)}",
            "Line.To" => $"To {Xyz(k)}",
            "Min" => $"Corner {Xyz(k)}",
            "Size" => $"{Xyz(k).ToUpperInvariant()} size",
            "SizeUm" => "Element size",
            _ => path,
        };
    }

    /// <summary>The Inspector's line and letter of a place's dimension (a vector's components share a line).</summary>
    public static (string Group, string Axis) PlaceFieldGroup(object item, string path)
    {
        string label = PlaceFieldLabel(item, path);
        if (label.EndsWith(" size", StringComparison.Ordinal) && label.Length == 6) return ("Size", label[..1].ToLowerInvariant());
        int space = label.LastIndexOf(' ');
        if (space > 0 && label.Length - space == 2 && "xyzuv".Contains(label[^1], StringComparison.Ordinal)) return (label[..space], label[^1..]);
        return (label, "");
    }

    /// <summary>
    /// R-em3d75-1c — a place's non-dimension field, as text: a heat source's <c>Power</c> (an expression) and <c>Density</c>;
    /// a probe's <c>Stat</c>, <c>LimitC</c> and its <c>Face</c>, <c>Solid</c> or <c>Wire</c>; a mesh region's <c>Grading</c>.
    /// Empty clears an optional one. One undo entry; null on success, else why not.
    /// </summary>
    public string? SetPlaceText(string name, string key, string text)
    {
        text = text.Trim();
        // Everything is checked before anything is written: a refused value leaves no entry, and nothing to redo.
        double? number = null;
        if (key is "LimitC" or "Grading" && text.Length > 0)
        {
            bool positive = key == "Grading";
            if (!NumericText.TryParseDouble(text, out double v) || !double.IsFinite(v) || (positive && v <= 0))
                return $"{(key == "Grading" ? "The grading" : "A limit")} is {(positive ? "a positive number" : "a number")}.";
            number = v;
        }
        bool enabled = false;
        if (key == "Enabled")
        {
            if (text.ToLowerInvariant() is "true" or "on" or "yes" or "1") enabled = true;
            else if (text.ToLowerInvariant() is not ("false" or "off" or "no" or "0")) return "Enabled is true or false.";
        }
        C3dHeatDensity density = default;
        C3dProbeStat? stat = null;
        switch (key)
        {
            case "Power" when text.Length > 0 && C3dThermal.Unparsable(text, ThermalQuantity.Power) is { } powerWhy:
                return $"The power '{text}' cannot be read: {powerWhy}.";
            case "Density" when !Enum.TryParse(text, true, out density):
                return "The density is Total (W), PerArea (W/m²) or PerVolume (W/m³).";
            case "Stat" when text.Length > 0:
                if (!Enum.TryParse<C3dProbeStat>(text, true, out var st)) return "The statistic is Max, Min or Avg.";
                stat = st;
                break;
            case "Face" or "Solid" or "Wire" or "SpotFace" when text.Length == 0 && !(key == "Solid" && Document.HeatSources.Any(h => h.Name == name)):
                return $"A {key.Replace("SpotFace", "spot", StringComparison.Ordinal).ToLowerInvariant()} probe names what it reads.";
            // a heat source's Solid may be emptied only where a sheet is left to carry it: a volumetric source with neither
            // wrote a record nothing draws and only check refused
            case "Solid" when text.Length == 0 && Document.HeatSources.FirstOrDefault(h => h.Name == name) is { Sheet: null }:
                return "A volumetric heat source names the solid it is spread through; delete the source instead.";
        }
        if (ThermalPlace(name) is not { } place) return $"This 3D view has no thermal place named '{name}'.";
        bool applies = (place, key) switch
        {
            (C3dHeatSource, "Power" or "Density" or "Solid") => true,
            (C3dProbe, "Stat" or "LimitC" or "Face" or "Solid" or "Wire") => true,
            (C3dProbe p, "SpotFace") => p.Spot is not null,
            (C3dMeshRegion, "Grading") => true,
            (C3dEffectiveBlock, "Enabled") => true,
            _ => false,
        };
        if (!applies) return $"'{name}' has no {key}.";
        ChangeRecords($"Set {name} {key}", d =>
        {
            if (d.HeatSources.FirstOrDefault(h => h.Name == name) is { } h)
                switch (key)
                {
                    case "Power": h.Power = text.Length == 0 ? null : text; break;
                    case "Density": h.Density = density; break;
                    case "Solid":
                        h.Solid = text.Length == 0 ? null : text;
                        if (h.Solid is not null) h.Sheet = null;
                        break;
                }
            else if (d.Probes.FirstOrDefault(p => p.Name == name) is { } p)
                switch (key)
                {
                    case "Stat": p.Stat = stat; break;
                    case "LimitC": p.LimitC = number; break;
                    case "Face": p.Face = [.. text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)]; break;
                    case "Solid": p.Solid = text; break;
                    case "Wire": p.Wire = text; break;
                    case "SpotFace": p.Spot!.Face = text; break;
                }
            else if (d.MeshRegions.FirstOrDefault(m => m.Name == name) is { } m) m.Grading = number;
            else if (d.EffectiveBlocks.FirstOrDefault(b => b.Name == name) is { } b) b.Enabled = enabled;
        });
        return null;
    }

    // ── the active thermal setup (R-em3d75-2) ────────────────────────────────────────────────

    /// <summary>The active setup, when it is an embedded thermal one: its index in the document and the setup.</summary>
    private (int Index, EmSetup Setup)? ActiveThermalSetup()
    {
        if (IsExternalActive || ActiveSetupName is not { } name) return null;
        return C3dSetups.Read(Document).FirstOrDefault(s => s.Name == name) is { Setup: { IsThermal: true } setup } read ? (read.Index, setup) : null;
    }

    /// <summary>Why the Thermal items are greyed: no thermal setup is active. Null when one is.</summary>
    public string? ThermalSetupRefusal()
        => ActiveThermalSetup() is not null ? null
         : ActiveSetup is { } s ? $"The active setup '{ActiveSetupName}' is {s.Problem3D.ToString().ToLowerInvariant()}: make a thermal setup active (Simulate ▸ Setup Analyses…) to set thermal boundaries."
         : "No setup is active: add a thermal setup in Simulate ▸ Setup Analyses… and make it active.";

    /// <summary>One edit of the active thermal setup's Thermal section: one undo entry. Null on success, else why not.</summary>
    private string? EditActiveThermal(string description, Func<CemThermal, string?> mutate)
    {
        if (ActiveThermalSetup() is not { } active) return ThermalSetupRefusal();
        var t = active.Setup.Thermal ??= new CemThermal();
        if (mutate(t) is { } why) return why;
        ChangeRecords($"{description} ({ActiveSetupName})", d => d.Setups[active.Index] = EmSetupPersistence.ToEmbedded(active.Setup));
        return null;
    }

    /// <summary>R-em3d75-2 — a face's thermal condition in the active thermal setup: Fixed Temperature, Convection, or (null)
    /// none — insulated, which every face is unless a boundary names it.</summary>
    public string? SetThermalBoundary(string face, ThermalBoundaryKind? kind, string? tempC = null, string? h = null, string? ambientC = null)
    {
        // the values the kind needs are stated (an empty one wrote a boundary check then refused), and each is read as its own
        // quantity: a temperature is bare °C, h a bare W/(m²·K) — the run's rule, so "85 m" is refused here, not read as 0.085
        foreach (var (v, what, q, needed) in new[]
                 {
                     (tempC, "The temperature", ThermalQuantity.Temperature, kind == ThermalBoundaryKind.FixedT),
                     (h, "h", ThermalQuantity.Plain, kind == ThermalBoundaryKind.Convection),
                     (ambientC, "The ambient temperature", ThermalQuantity.Temperature, kind == ThermalBoundaryKind.Convection),
                 })
        {
            if (string.IsNullOrWhiteSpace(v)) { if (needed) return $"{what} is empty; state a value."; continue; }
            if (C3dThermal.Unparsable(v, q) is { } why) return $"{what} '{v}' cannot be read: {why}.";
        }
        string what2 = kind switch { ThermalBoundaryKind.FixedT => $"{tempC} °C", ThermalBoundaryKind.Convection => $"convection {h} W/(m²·K) to {ambientC} °C", _ => "insulated" };
        return EditActiveThermal($"Thermal boundary on {face}: {what2}", t =>
        {
            var list = t.Boundaries ??= [];
            list.RemoveAll(b => b.Face == face);
            if (kind is { } k)
                list.Add(new CemThermalBoundary
                {
                    Face = face, Kind = k,
                    TempC = k == ThermalBoundaryKind.FixedT ? tempC : null,
                    H = k == ThermalBoundaryKind.Convection ? h : null,
                    AmbientC = k == ThermalBoundaryKind.Convection ? ambientC : null,
                });
            if (list.Count == 0) t.Boundaries = null;
            return null;
        });
    }

    /// <summary>The active thermal setup's condition on <paramref name="face"/>, or null.</summary>
    public CemThermalBoundary? ThermalBoundaryOn(string face)
        => ActiveThermalSetup()?.Setup.Thermal?.Boundaries?.FirstOrDefault(b => b.Face == face);

    /// <summary>R-em3d75-2 — the technology's interface resistance between two objects' materials, and where it comes from; null
    /// when it states none.</summary>
    public (double Value, string Source)? TechnologyContact(string a, string b)
    {
        if (Elaboration is not { Technology: { } tech } e) return null;
        string? ma = e.Solids.FirstOrDefault(s => s.Name == a)?.Material, mb = e.Solids.FirstOrDefault(s => s.Name == b)?.Material;
        return tech.FindThermalInterface(ma, mb) is { } i
            ? (i.ResistanceM2KW, $"the technology's {i.MaterialA}–{i.MaterialB} interface{(i.Source is { Length: > 0 } src ? $" ({src})" : "")}")
            : null;
    }

    /// <summary>R-em3d75-2 — the contact between <paramref name="a"/> and <paramref name="b"/> overridden (brief 73's
    /// ContactResistances); an empty text removes the override. Null on success.</summary>
    public string? SetContactResistance(string a, string b, string text)
    {
        text = text.Trim();
        double? r = null;
        if (text.Length > 0)
        {
            if (!NumericText.TryParseDouble(text, out double v) || !(v >= 0) || !double.IsFinite(v))
                return "A contact resistance is zero (perfect contact) or a positive number, m²·°C/W (1e-5 is a thin solder).";
            r = v;
        }
        static bool Same(C3dContactResistance c, string a, string b)
            => c.Between.Count == 2 && (c.Between[0] == a && c.Between[1] == b || c.Between[0] == b && c.Between[1] == a);
        ChangeRecords(r is null ? $"Clear the contact {a}–{b}" : $"Contact {a}–{b}: {text} m²·°C/W", d =>
        {
            d.ContactResistances.RemoveAll(c => Same(c, a, b));
            if (r is { } rv) d.ContactResistances.Add(new C3dContactResistance { Between = [a, b], ResistanceM2KW = rv });
        });
        return null;
    }

    // ── Plot Temperature (R-em3d75-4) ────────────────────────────────────────────────────────

    /// <summary>
    /// Gate 2 — why Plot Temperature is greyed, or null when it can plot: the active setup is thermal, its run left a
    /// temperature field, and the model has not changed since that run (the stale banner is the same comparison).
    /// </summary>
    public string? PlotTemperatureRefusal()
    {
        if (ActiveSetup is not { IsThermal: true }) return "Plot Temperature reads a thermal run: make a thermal setup active.";
        // the ACTIVE setup's run, on disk: the viewer may have another setup's run open for the plot it is drawing
        if (!ActiveThermalResultExists()) return $"There is no thermal result for '{ActiveSetupName}': run it first (Simulate ▸ Run).";
        if (FieldsStaleText is not null) return "The thermal result is stale — the model has changed since it was run. Run it again to plot it.";
        return null;
    }

    /// <summary>The active thermal setup's run left a temperature field.</summary>
    private bool ActiveThermalResultExists()
        => ActiveSetup is { IsThermal: true } && ActiveRunDirectories().FirstOrDefault() is { } dir &&
           File.Exists(Path.Combine(ThermalFieldFiles.Folder(dir), ThermalFieldFiles.Problem + ".pvd"));

    /// <summary>The run the viewer has open is the active thermal setup's: its table and temperatures answer for that setup.
    /// A visible plot pinned to another setup opens that setup's run instead (brief-em3d-83).</summary>
    private bool ViewerReadsActiveThermal()
        => Viewer.IsThermalRun && Viewer.FieldsAvailable && Viewer.FieldRunDirectory is { } open &&
           ActiveSetup is { IsThermal: true } && ActiveRunDirectories().FirstOrDefault() is { } dir &&
           string.Equals(Path.GetFullPath(open).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar),
                         OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    /// <summary>The thermal table of the active setup's run, or null when the viewer has no such run open.</summary>
    private ThermalResultTable? ActiveThermalTable => ViewerReadsActiveThermal() ? Viewer.ThermalTable : null;

    /// <summary>Why there is no <see cref="ActiveThermalTable"/>.</summary>
    private string NoActiveThermalTable()
        => ActiveThermalResultExists() && Viewer.FieldsAvailable && !ViewerReadsActiveThermal()
            ? $"The fields open are another setup's run: show a plot of '{ActiveSetupName}' to read its result."
            : "No thermal result: run the active thermal setup.";

    /// <summary>The toolbar's Along… and Probes: shown while the active thermal setup's temperatures are the fields read.</summary>
    public bool ShowThermalRunTools => ViewerReadsActiveThermal();

    /// <summary>Gate 2 — the stale banner changing changes what Plot Temperature can do: the menus re-ask. The sentence is
    /// shown on the selected field plot in the Properties Inspector, which reads it again.</summary>
    partial void OnFieldsStaleTextChanged(string? value)
    {
        RaiseMenuStateChanged();
        Properties?.RefreshPlotProblem();
    }

    /// <summary>
    /// 3D ▸ View ▸ Temperature and the visibility switches, by name — what the menu bar calls: <c>AllFaces</c>, <c>OnClip</c>,
    /// <c>FixRange</c>, <c>Along</c>, <c>Table</c>, <c>Clear</c>; <c>HeatSources</c>, <c>Probes</c>, <c>MeshRegions</c>.
    /// </summary>
    public void RunTemperature(string which)
    {
        switch (which)
        {
            case "AllFaces": Report(SetTemperatureAllFaces(!Viewer.TemperatureAllFaces)); break;
            case "OnClip": Report(SetTemperatureOnClip(!Viewer.TemperatureOnClip)); break;
            // brief-em3d-83 — the drawn temperature plot's own switch, one undo entry
            case "FixRange":
                if (VisibleFieldPlot is { IsTemperature: true } tp) SetFieldPlot(tp.Name, $"Fix the range of {tp.Name}", p => p.FixRange = !p.FixRange);
                break;
            case "Along": StartTemperatureAlong(); break;
            case "Table": ProbeTableOpen = !ProbeTableOpen; break;
            case "Clear": ChangePlots("Hide the temperature", plots => { foreach (var p in plots.Where(p => p.IsTemperature)) p.Hidden = true; }); break;
            case "HeatSources": ShowHeatSources = !ShowHeatSources; break;
            case "Probes": ShowProbes = !ShowProbes; break;
            case "MeshRegions": ShowMeshRegions = !ShowMeshRegions; break;
            case "Mirror": Viewer.MirrorSymmetry = !Viewer.MirrorSymmetry; break;
        }
    }

    /// <summary>R-em3d75-4a — right-click a face ▸ Plot Temperature: that face added to the drawn temperature Faces plot (a
    /// second time takes it away), or a new one made with it (brief-em3d-83).</summary>
    public string? PlotTemperatureOnFace(uint objectId, int face)
    {
        if (Viewer.Scene.Object(objectId) is not { } o || face < 0) return "There is no face under the cursor.";
        // taking a face OFF is always allowed — a stale result must not pin its paint on — and only adding one is gated
        if (!Viewer.IsTemperatureFace(o.Name, face) && PlotTemperatureRefusal() is { } why) return why;
        PaintFace(o, face, 0, temperature: true);
        return null;
    }

    /// <summary>View ▸ Temperature ▸ All Faces: a temperature plot on every exposed face drawn (made when there is none), or
    /// hidden (brief-em3d-83).</summary>
    public string? SetTemperatureAllFaces(bool on)
    {
        if (on && PlotTemperatureRefusal() is { } why) return why;
        ShowTemperaturePlot(C3dFieldPlotOn.Surfaces, on);
        return null;
    }

    /// <summary>View ▸ Temperature ▸ On Clip Plane: a temperature plot cut where the view's clip plane cuts (the plane is turned on
    /// first), drawn or hidden (brief-em3d-83).</summary>
    public string? SetTemperatureOnClip(bool on)
    {
        if (on && PlotTemperatureRefusal() is { } why) return why;
        if (on && !Viewer.View.Clip.Enabled) Viewer.ClipEnabled = true;
        ShowTemperaturePlot(C3dFieldPlotOn.ClipPlane, on);
        return null;
    }

    /// <summary>R-em3d75-4d — Temperature Along… ▸ pick two points: arms the two-point pick.</summary>
    [RelayCommand]
    public void StartTemperatureAlong()
    {
        if (PlotTemperatureRefusal() is { } why) { StatusMessage = why; return; }
        if (!ViewerReadsActiveThermal()) { StatusMessage = NoActiveThermalTable(); return; }
        Viewer.EnsureTemperatureLoaded();
        SetTool(new TemperatureAlongTool(this));
    }

    /// <summary>The line panel: a line probe's T(s) or Temperature Along's; null closes it.</summary>
    [ObservableProperty] private C3dThermalLine? _thermalLine;

    /// <summary>The line panel's plot, built from <see cref="ThermalLine"/> for the existing plotting control.</summary>
    public CircuitRF.Render.DataDisplay.Plot? ThermalLinePlot => ThermalLine is { } l ? ThermalPlots.Line(l) : null;

    /// <summary>The line panel's readout: distance, both ends and their difference.</summary>
    public string ThermalLineText => ThermalLine is { } l
        ? l.XLabel is null ? $"{l.Title}: {Viewer.FormatLength(l.LengthM)} long; {C(l.FromC)} → {C(l.ToC)}, ΔT = {C(l.DeltaC)}" : l.Title
        : "";

    public bool HasThermalLine => ThermalLine is not null;

    partial void OnThermalLineChanged(C3dThermalLine? value)
    {
        OnPropertyChanged(nameof(ThermalLinePlot));
        OnPropertyChanged(nameof(ThermalLineText));
        OnPropertyChanged(nameof(HasThermalLine));
    }

    [RelayCommand]
    private void CloseThermalLine() => ThermalLine = null;

    private static string C(double v) => double.IsFinite(v) ? v.ToString("0.00", CultureInfo.InvariantCulture) + " °C" : "—";

    /// <summary>The line plot's distance axis in the document's display unit, as the readout beside it: (units per metre,
    /// suffix).</summary>
    private (double PerMetre, string Suffix) LineDistanceUnit()
        => (1e6 * (double)LayoutUnits.FromDbu(Document.DbuPerMicron, Document.DisplayUnit, Document.DbuPerMicron),
            LayoutUnits.Suffix(Document.DisplayUnit));

    /// <summary>Samples along a Temperature Along line.</summary>
    public const int AlongSamples = 201;

    /// <summary>R-em3d75-4d — T from <paramref name="a"/> to <paramref name="b"/> (DBU) on the step shown, into the line panel.
    /// Null on success, else why not.</summary>
    public string? PlotTemperatureAlong(C3dPoint3 a, C3dPoint3 b)
    {
        if (!ViewerReadsActiveThermal()) return NoActiveThermalTable();
        double M(long v) => C3dLowering.Metres(v, Document.DbuPerMicron);
        var line = Viewer.TemperatureAlong((M(a.X), M(a.Y), M(a.Z)), (M(b.X), M(b.Y), M(b.Z)), AlongSamples, out string? why);
        if (line is null) return why;
        var (per, suffix) = LineDistanceUnit();
        ThermalLine = new C3dThermalLine($"Temperature along ({Viewer.TemperatureStepLabel})", line.Distance, line.Values,
                                         UnitsPerMetre: per, UnitSuffix: suffix);
        StatusMessage = ThermalLineText;
        return null;
    }

    /// <summary>R-em3d75-4d — a line probe's T(s), from the run's table, at the step shown.</summary>
    public string? PlotLineProbe(string probe)
    {
        if (ActiveThermalTable is not { } table) return NoActiveThermalTable();
        if (table.Lines.FirstOrDefault(l => l.Probe == probe) is not { } line)
            return $"There is no line result for '{probe}': run the active thermal setup.";
        if (Document.Probes.FirstOrDefault(p => p.Name == probe)?.Line is not { } seg) return $"'{probe}' is not a line probe.";
        // the distance axis is the segment as drawn NOW: after an edit it could stretch the old samples over a moved line
        if (FieldsStaleText is not null) return "The thermal result is stale — the model has changed since it was run. Run it again to plot T(s).";
        var d = seg.To - seg.From;
        double length = C3dLowering.Metres(1, Document.DbuPerMicron) * Math.Sqrt((double)d.X * d.X + (double)d.Y * d.Y + (double)d.Z * d.Z);
        int step = Math.Clamp(Viewer.TemperatureStep, 0, line.PerPoint.Length - 1);
        var (per, suffix) = LineDistanceUnit();
        ThermalLine = new C3dThermalLine($"{probe} ({table.PointLabel(step)})", [.. line.Fraction.Select(f => f * length)], line.PerPoint[step],
                                         UnitsPerMetre: per, UnitSuffix: suffix);
        return null;
    }

    // ── the probe table (R-em3d75-4e) ────────────────────────────────────────────────────────

    public ObservableCollection<C3dProbeTableRow> ProbeTable { get; } = [];

    /// <summary>A column per sweep point, on request.</summary>
    [ObservableProperty] private bool _probeTableAllPoints;

    [ObservableProperty] private bool _probeTableOpen;

    /// <summary>The table's heading: the step shown, and how many probes crossed their limit.</summary>
    [ObservableProperty] private string _probeTableHeading = "";

    /// <summary>brief-em3d-79 R-em3d79-4 — what a row is plotted against: the sweep's variable (the innermost axis), or a circuit
    /// cube the run carried (Pout, PAE, a pin current).</summary>
    [ObservableProperty] private IReadOnlyList<string> _probeTableXChoices = [];
    [ObservableProperty] private string? _probeTableX;

    public bool HasProbeTableX => ProbeTableXChoices.Count > 0;

    partial void OnProbeTableXChoicesChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(HasProbeTableX));

    /// <summary>R-em3d79-4 — row <paramref name="name"/> against <see cref="ProbeTableX"/>, over the sweep row holding the step
    /// shown, into the line panel. Null on success, else why not.</summary>
    public string? PlotProbeRow(string name)
    {
        if (ActiveThermalTable is not { } t) return NoActiveThermalTable();
        if (t.Axes.Count == 0) return "The run has one point: there is nothing to plot it against.";
        if (t.Rows.FirstOrDefault(r => r.Name == name) is not { } row) return $"The result has no row '{name}'.";
        var inner = t.Axes[^1];
        int n = inner.Length, start = Math.Clamp(Viewer.TemperatureStep, 0, Math.Max(0, t.Points - 1)) / n * n;
        string xName = ProbeTableX ?? inner.Name;
        double[] x;
        string xLabel;
        if (xName == inner.Name) { x = inner.Values; xLabel = inner.Name + (inner.Unit is { Length: > 0 } u ? $" ({u})" : ""); }
        else if (t.Circuit.FirstOrDefault(c => c.Name == xName) is { } c)
        {
            x = c.Values.AsSpan(start, n).ToArray();
            xLabel = c.Name + (c.Unit is { Length: > 0 } cu && cu != "1" ? $" ({cu})" : "");
        }
        else return $"The result has no '{xName}' to plot against.";
        var y = row.Values.AsSpan(start, n).ToArray();
        string where = t.Axes.Count > 1 ? $" ({t.PointLabel(start).Split(", ")[0]})" : "";
        ThermalLine = new C3dThermalLine($"{name} against {xName}{where}", x, y, xLabel, row.Name + (row.Unit is { Length: > 0 } ru && ru != "1" ? $" ({ru})" : ""));
        return null;
    }

    /// <summary>
    /// brief-em3d-80 R-em3d80-2c — Z_th of <paramref name="place"/> (a heat source's own place, or a probe) per watt in
    /// <paramref name="source"/>, from the run's table, into the line panel: |Z_th| in K/W, or its phase in degrees, against a
    /// logarithmic frequency. Null on success, else why not.
    /// </summary>
    public string? PlotZth(string place, string source, bool phase)
    {
        if (ActiveThermalTable is not { } t) return NoActiveThermalTable();
        if (t.Zth.FirstOrDefault(z => z.Place == place && z.Source == source) is not { } zth)
            return $"The result has no Z_th of '{place}' per watt in '{source}': state it in the setup's Zth and run it.";
        var keep = Enumerable.Range(0, zth.FrequencyHz.Length).Where(i => zth.FrequencyHz[i] > 0).ToList();
        double[] f = [.. keep.Select(i => zth.FrequencyHz[i])];
        double[] y = [.. keep.Select(i => phase ? zth.Z[i].Phase * 180 / Math.PI : zth.Z[i].Magnitude)];
        string who = place == source ? $"'{place}'" : $"'{place}' per watt in '{source}'";
        ThermalLine = new C3dThermalLine($"Z_th of {who}{(phase ? " — phase" : "")}", f, y, "Frequency (Hz)", phase ? "arg Z_th (°)" : "|Z_th| (°C/W)", LogX: true);
        return null;
    }

    [RelayCommand]
    private void PlotProbeTableRow(string? name) { if (name is not null) StatusMessage = PlotProbeRow(name) ?? StatusMessage; }

    partial void OnProbeTableAllPointsChanged(bool value) => RefreshProbeTable();
    partial void OnProbeTableOpenChanged(bool value) { if (value) RefreshProbeTable(); }

    [RelayCommand]
    private void ToggleProbeTable() => ProbeTableOpen = !ProbeTableOpen;

    /// <summary>Re-reads the table from the run's .npy (through the viewer's reading of it) at the step shown.</summary>
    public void RefreshProbeTable()
    {
        ProbeTable.Clear();
        if (ActiveThermalTable is not { } t)
        {
            ProbeTableHeading = NoActiveThermalTable();
            return;
        }
        int step = Math.Clamp(Viewer.TemperatureStep, 0, Math.Max(0, t.Points - 1));
        // R-em3d79-4 — the x axis: the sweep variable, then each circuit cube the run carried
        List<string> xs = t.Axes.Count > 0 ? [t.Axes[^1].Name, .. t.Circuit.Select(c => c.Name)] : [];
        if (!xs.SequenceEqual(ProbeTableXChoices)) ProbeTableXChoices = xs;
        if (ProbeTableX is null || !xs.Contains(ProbeTableX)) ProbeTableX = xs.FirstOrDefault();
        static string V(double v, string unit) => double.IsFinite(v) ? v.ToString("G6", CultureInfo.InvariantCulture) + (unit.Length > 0 && unit != "1" ? " " + unit : "") : "—";
        foreach (var r in t.Rows)
        {
            bool crossed = r.Crossed is { } c && step < c.Length && c[step];
            var all = ProbeTableAllPoints ? r.Values.Select((v, i) => (r.Crossed is { } cc && i < cc.Length && cc[i] ? "⚠ " : "") + V(v, r.Unit)).ToList() : [];
            ProbeTable.Add(new C3dProbeTableRow(r.Name, r.Unit, step < r.Values.Length ? V(r.Values[step], r.Unit) : "—", all, crossed, r.IsMeasure));
        }
        int flagged = t.Rows.Count(r => r.AnyCrossed);
        ProbeTableHeading = $"{t.PointLabel(step)}" + (flagged > 0 ? $" · {flagged} probe(s) reach their limit somewhere in the sweep" : "");
    }

    // ── the context menu (R-em3d75-1a, -2, -4a) ──────────────────────────────────────────────

    private IEnumerable<Viewer3DMenuItem> ThermalMenuItems()
    {
        var sel = Viewer.Selection;
        string? thermalWhy = ThermalSetupRefusal();
        string? plotWhy = PlotTemperatureRefusal();
        if (Viewer.SelectMode == Scene3DSelectMode.Face && sel.Count == 1 && sel[0].Face >= 0 && Viewer.Scene.Object(sel[0].Object) is { } o
            && o.Kind is not (Scene3DKind.Port or Scene3DKind.Boundary or Scene3DKind.Air) && BoxFaceOf(o) is null)
        {
            string face = $"{o.Name}/{o.FaceName(sel[0].Face)}";
            uint id = o.Id;
            int fi = sel[0].Face;
            bool painted = Viewer.IsTemperatureFace(o.Name, fi);
            yield return new Viewer3DMenuItem((painted ? "✓ " : "") + "Plot Temperature", () => Report(PlotTemperatureOnFace(id, fi)),
                Enabled: plotWhy is null || painted,
                Tip: painted ? "Take the temperature off this face." : plotWhy ?? "Temperature on this face; again to take it off. Several faces accumulate.");
            var now = ThermalBoundaryOn(face);
            string tip = thermalWhy ?? $"Writes setup '{ActiveSetupName}': a face no boundary names is insulated.";
            yield return new Viewer3DMenuItem("Thermal", Enabled: thermalWhy is null, Tip: tip, Children:
            [
                new Viewer3DMenuItem((now?.Kind == ThermalBoundaryKind.FixedT ? "● " : "") + "Fixed Temperature…", () => TextRequested?.Invoke(
                    $"Fixed temperature on {face}", "Temperature, °C (a number or an expression):", now?.TempC ?? "25",
                    text => SetThermalBoundary(face, ThermalBoundaryKind.FixedT, tempC: text.Trim())), Tip: tip),
                new Viewer3DMenuItem((now?.Kind == ThermalBoundaryKind.Convection ? "● " : "") + "Convection…", () => TextRequested?.Invoke(
                    $"Convection from {face}", "h in W/(m²·K), then the ambient in °C, separated by a comma (10, 25):",
                    now?.Kind == ThermalBoundaryKind.Convection ? $"{now.H}, {now.AmbientC}" : "10, 25", text =>
                    {
                        var parts = text.Split(',', StringSplitOptions.TrimEntries);
                        return parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0
                            ? SetThermalBoundary(face, ThermalBoundaryKind.Convection, h: parts[0], ambientC: parts[1])
                            : "Type h and the ambient temperature, separated by a comma: 10, 25.";
                    }), Tip: tip),
                new Viewer3DMenuItem((now is null ? "● " : "") + "Insulated", () => Report(SetThermalBoundary(face, null)),
                    Tip: "Every face no boundary names is insulated (adiabatic): this removes the face's condition."),
                new Viewer3DMenuItem("Clear", () => Report(SetThermalBoundary(face, null)), Enabled: now is not null,
                    Tip: now is null ? "This face has no thermal condition to clear." : "Remove this face's condition from the setup."),
            ]);
            yield return new Viewer3DMenuItem("Add Probe", Children:
            [
                new Viewer3DMenuItem("Face", () => AddThermalPlace(new C3dProbe { Name = NextName("probe"), Face = [face], Stat = C3dProbeStat.Max }),
                    Tip: "The face's maximum, minimum and average temperature."),
            ]);
            // brief-em3d-76 R-em3d76-4a — the face the modelled half was cut on
            var (onPlane, symWhy) = SymmetryOfFace(id, fi);
            bool declared = onPlane is { } sp0 && Document.SymmetryPlanes.Any(p => p.Axis == sp0.Axis && p.At == sp0.At);
            yield return new Viewer3DMenuItem((declared ? "✓ " : "") + "Symmetry Plane", () => Report(ToggleSymmetryPlane(id, fi)),
                Enabled: symWhy is null, Tip: symWhy ?? (declared ? "Remove the symmetry plane on this face." :
                    "The modelled part is half the device, cut on this face: the face stays insulated, measures read SymmetryFactor, and a " +
                    "plotted temperature is drawn mirrored."));
            yield return Viewer3DMenuItem.Separator;
        }
        if (Viewer.SelectMode != Scene3DSelectMode.Object) yield break;
        var objs = Viewer.SelectedObjects().Where(x => x.Kind is not (Scene3DKind.Port or Scene3DKind.Boundary)).ToList();
        if (objs.Count == 1)
        {
            var only = objs[0];
            bool wire = only.Kind == Scene3DKind.Wire;
            var probes = new List<Viewer3DMenuItem>
            {
                wire ? new Viewer3DMenuItem("Wire", () => AddThermalPlace(new C3dProbe { Name = NextName("probe"), Wire = only.Name, Stat = C3dProbeStat.Max }),
                                            Tip: "T along the wire and its maximum (its 1D solution, from a later version's wire results).")
                     : new Viewer3DMenuItem("Solid", () => AddThermalPlace(new C3dProbe { Name = NextName("probe"), Solid = only.Name, Stat = C3dProbeStat.Max }),
                                            Tip: "The solid's maximum, minimum and volume-average temperature."),
            };
            yield return new Viewer3DMenuItem("Add Probe", Children: probes);
            if (!wire)
                yield return new Viewer3DMenuItem("Heat Source from Solid", () =>
                {
                    string n = NextName("source");
                    AddThermalPlace(new C3dHeatSource { Name = n, Solid = only.Name });
                    AskHeatSource(n);
                }, Tip: "Heat spread through the whole solid (a volumetric source): a channel, a resistor's film.");
        }
        if (objs.Count == 2)
        {
            string a = objs[0].Name, b = objs[1].Name;
            var tech = TechnologyContact(a, b);
            var have = Document.ContactResistances.FirstOrDefault(c => c.Between.Count == 2 && c.Between.Contains(a) && c.Between.Contains(b));
            string prefill = have?.ResistanceM2KW.ToString("G6", CultureInfo.InvariantCulture) ?? tech?.Value.ToString("G6", CultureInfo.InvariantCulture) ?? "";
            string says = have is not null ? "an override this document already holds" : tech is { } t ? t.Source : "no value: the technology states none for this pair";
            yield return new Viewer3DMenuItem("Thermal", Children:
            [
                new Viewer3DMenuItem("Contact Resistance…", () => TextRequested?.Invoke($"Contact resistance {a}–{b}",
                    $"m²·°C/W, for this contact only (prefilled from {says}; empty removes the override):", prefill,
                    text => SetContactResistance(a, b, text)), Tip: "The two must touch; the value overrides the technology's pair value for this contact."),
            ]);
        }
    }

    // ── drawing the places (R-em3d75-1b) ─────────────────────────────────────────────────────

    private void FillThermalOverlay(Viewer3DDrawOverlay overlay)
    {
        int dbu = Document.DbuPerMicron;
        double per = C3dLowering.Metres(1, dbu);
        Point3 M(C3dPoint3 p) => new(p.X * per, p.Y * per, p.Z * per);
        string? selected = SelectedTreeItem is { Kind: HeatSourceKind or ProbeKind or MeshRegionKind or EffectiveBlockKind } row ? row.Name : null;
        if (ShowHeatSources)
            foreach (var h in Document.HeatSources)
            {
                if (!IsPlaceShown(h.Name)) continue;
                var into = h.Name == selected ? overlay.Selected : overlay.HeatSources;
                if (h.Sheet is { } s)
                {
                    var plane = new DrawingPlane(s.Plane, s.Offset);
                    var outline = s.Rect is { } r
                        ? new List<C3dPoint2> { r.Min, new(r.Min.U + r.Size.U, r.Min.V), new(r.Min.U + r.Size.U, r.Min.V + r.Size.V), new(r.Min.U, r.Min.V + r.Size.V) }
                        : s.Outline;
                    if (outline.Count < 3) continue;
                    DrawGeometry.Chain([.. outline.Select(plane.FromUv)], true, dbu, into);
                    foreach (var (a, b) in ThermalPlots.Hatch(outline))
                        into.Add(new DrawSegment(M(plane.FromUvw(a.U, a.V, s.Offset)), M(plane.FromUvw(b.U, b.V, s.Offset))));
                    overlay.Labels.Add((M(plane.FromUv(new C3dPoint2(outline.Sum(p => p.U) / outline.Count, outline.Sum(p => p.V) / outline.Count))), h.Name));
                }
                else if (h.Solid is { } solid && SceneObject(solid) is { } so)
                {
                    var (mn, mx) = Viewer.Scene.ToWorld(so.Min) is var w0 && Viewer.Scene.ToWorld(so.Max) is var w1 ? (w0, w1) : default;
                    AddBox(into, new Point3(mn.X, mn.Y, mn.Z), new Point3(mx.X, mx.Y, mx.Z));
                    overlay.Labels.Add((new Point3((mn.X + mx.X) / 2, (mn.Y + mx.Y) / 2, mx.Z), $"{h.Name} (in {solid})"));
                }
            }
        if (ShowProbes)
            foreach (var p in Document.Probes)
            {
                if (!IsPlaceShown(p.Name)) continue;
                var into = p.Name == selected ? overlay.Selected : overlay.Probes;
                Point3? at = null;
                if (p.Point is { } pt) at = M(pt);
                else if (p.Spot is { } spot)
                {
                    at = M(spot.Center);
                    var n = FaceNormal(spot.Face) ?? new System.Numerics.Vector3(0, 0, 1);
                    Ring(into, at.Value, n, spot.Diameter * per / 2);
                }
                else if (p.Line is { } line)
                {
                    into.Add(new DrawSegment(M(line.From), M(line.To)));
                    at = M(line.From);
                    overlay.ProbeMarks.Add(M(line.To));
                }
                // General designer feedback round 14 — a face probe marks its FACE: at its solid's centre, die/zmax and two probes on
                // flange/zmin sat on the die and flange centres, one above the other, and nothing said which face each one reads.
                else if (p.Face is [var face, ..] && FaceCentre(face) is { } fc) at = fc;
                else if ((p.Solid ?? p.Wire ?? (p.Face is [var f0, ..] ? f0[..Math.Max(0, f0.LastIndexOf('/'))] : null)) is { } target && SceneObject(target) is { } so)
                {
                    var c = Viewer.Scene.ToWorld(so.Centroid);
                    at = new Point3(c.X, c.Y, c.Z);
                }
                if (at is { } a)
                {
                    overlay.ProbeMarks.Add(a);
                    overlay.Labels.Add((a, p.Name + (p.LimitC is { } lim ? $" (≤ {lim.ToString("G4", CultureInfo.InvariantCulture)} °C)" : "")));
                }
            }
        if (ShowMeshRegions)
            foreach (var m in Document.MeshRegions)
            {
                if (!IsPlaceShown(m.Name)) continue;
                var lo = M(m.Min);
                var hi = M(m.Min + m.Size);
                AddBox(m.Name == selected ? overlay.Selected : overlay.MeshRegions, lo, hi);
                overlay.Labels.Add((hi, $"{m.Name}: {SpellMicrons(m.SizeUm)} {LayoutUnits.Suffix(Document.DisplayUnit)}"));
            }
        // brief-em3d-76 — an effective block is drawn as a mesh region is, labelled with whether it is on
        if (ShowMeshRegions)
            foreach (var b in Document.EffectiveBlocks)
            {
                if (!IsPlaceShown(b.Name)) continue;
                var lo = M(b.Min);
                var hi = M(b.Min + b.Size);
                AddBox(b.Name == selected ? overlay.Selected : overlay.MeshRegions, lo, hi);
                overlay.Labels.Add((hi, $"{b.Name}: effective block, {(b.Enabled ? "enabled" : "disabled")}"));
            }
        FillSymmetryPlanes(overlay);
        FillSelectedTint(overlay);
    }

    /// <summary>
    /// brief-em3d-90 R-em3d90-2 — each declared symmetry plane, drawn: a rectangle across the meshed model's extent in its two
    /// other axes, at its coordinate, outlined and hatched as the air box draws a symmetry face (Scene3DBuilder.Hatch), and
    /// labelled <c>Symmetry X = 120 µm</c>. Its row selected, it is drawn in the selection's colour. An overlay, never a scene
    /// object: it is selected from the tree.
    /// </summary>
    private void FillSymmetryPlanes(Viewer3DDrawOverlay overlay)
    {
        if (Document.SymmetryPlanes.Count == 0 || Elaboration is not { Ok: true } e || C3dThermal.SymmetryExtent(e) is not { } ext) return;
        double per = C3dLowering.Metres(1, Document.DbuPerMicron);
        C3dAxis? selected = SelectedTreeItem is { Kind: SymmetryPlaneKind } row && Enum.TryParse<C3dAxis>(row.Name[SymmetryRowPrefix.Length..], out var a) ? a : null;
        foreach (var sp in Document.SymmetryPlanes)
        {
            if (!IsSymmetryPlaneShown(sp.Axis)) continue;
            var c = SymmetryPlaneCorners(sp.Axis, sp.At * per, ext);
            var into = sp.Axis == selected ? overlay.Selected : overlay.SymmetryPlanes;
            for (int k = 0; k < 4; k++) into.Add(new DrawSegment(c[k], c[(k + 1) % 4]));
            var hatch = Scene3DBuilder.Hatch(c);
            for (int k = 0; k + 1 < hatch.Count; k += 2) into.Add(new DrawSegment(hatch[k], hatch[k + 1]));
            overlay.Labels.Add((c[2], $"Symmetry {sp.Axis} = {SpellMicrons(sp.At / (double)Document.DbuPerMicron)} {LayoutUnits.Suffix(Document.DisplayUnit)}"));
        }
    }

    /// <summary>A symmetry plane's rectangle (metres, corners in order) across <paramref name="ext"/>, at <paramref name="at"/>.</summary>
    internal static Point3[] SymmetryPlaneCorners(C3dAxis axis, double at, (double X0, double Y0, double Z0, double X1, double Y1, double Z1) ext)
        => axis switch
        {
            C3dAxis.X => [new(at, ext.Y0, ext.Z0), new(at, ext.Y1, ext.Z0), new(at, ext.Y1, ext.Z1), new(at, ext.Y0, ext.Z1)],
            C3dAxis.Y => [new(ext.X0, at, ext.Z0), new(ext.X1, at, ext.Z0), new(ext.X1, at, ext.Z1), new(ext.X0, at, ext.Z1)],
            _ => [new(ext.X0, ext.Y0, at), new(ext.X1, ext.Y0, at), new(ext.X1, ext.Y1, at), new(ext.X0, ext.Y1, at)],
        };

    /// <summary>brief-em3d-90 R-em3d90-3 — a boundary's row selected (thermal, or an EM face boundary): its tint's outline in the
    /// selection's colour, as a selected heat source is drawn. A hidden tint is in no scene, and is not drawn.</summary>
    private void FillSelectedTint(Viewer3DDrawOverlay overlay)
    {
        string? name = SelectedTreeItem switch
        {
            { Kind: ThermalBoundaryKindName } r => Scene3DBuilder.FaceTintPrefix + r.Name,
            { Kind: EmBoundaryKind } r => r.Name,
            _ => null,
        };
        if (name is null || SceneObject(name) is not { } tint || !Viewer.View.IsVisible(tint.Id)) return;
        var scene = Viewer.Scene;
        foreach (var b in scene.LineBatches)
        {
            if (b.ObjectId != tint.Id) continue;
            for (int k = b.FirstVertex; k + 1 < b.FirstVertex + b.VertexCount; k += 2)
            {
                var (ax, ay, az) = scene.ToWorld(new System.Numerics.Vector3(scene.LineVertices[k].X, scene.LineVertices[k].Y, scene.LineVertices[k].Z) + b.Offset);
                var (bx, by, bz) = scene.ToWorld(new System.Numerics.Vector3(scene.LineVertices[k + 1].X, scene.LineVertices[k + 1].Y, scene.LineVertices[k + 1].Z) + b.Offset);
                overlay.Selected.Add(new DrawSegment(new Point3(ax, ay, az), new Point3(bx, by, bz)));
            }
        }
    }

    /// <summary>The area-weighted centre of face <paramref name="face"/> (<c>solid/name</c>), world metres, or null when there is none.</summary>
    private Point3? FaceCentre(string face)
    {
        int slash = face.LastIndexOf('/');
        if (slash <= 0 || SceneObject(face[..slash]) is not { } o) return null;
        int fi = -1;
        for (int k = 0; k < o.FaceNames.Count; k++) if (o.FaceNames[k] == face[(slash + 1)..]) { fi = k; break; }
        if (fi < 0) return null;
        double area = 0;
        var sum = System.Numerics.Vector3.Zero;
        foreach (var (a, b, c) in Scene3DFaces.Triangles(Viewer.Scene, o.Id, fi))
        {
            float t = 0.5f * System.Numerics.Vector3.Cross(b - a, c - a).Length();
            area += t;
            sum += t * (a + b + c) / 3;
        }
        if (!(area > 0)) return null;
        var (x, y, z) = Viewer.Scene.ToWorld(sum / (float)area);
        return new Point3(x, y, z);
    }

    private System.Numerics.Vector3? FaceNormal(string face)
    {
        int slash = face.LastIndexOf('/');
        if (slash <= 0 || SceneObject(face[..slash]) is not { } o) return null;
        int fi = -1;
        for (int k = 0; k < o.FaceNames.Count; k++) if (o.FaceNames[k] == face[(slash + 1)..]) { fi = k; break; }
        return fi < 0 ? null : Scene3DFaces.AreaAndNormal(Viewer.Scene, o.Id, fi).Normal;
    }

    private static void AddBox(List<DrawSegment> into, Point3 lo, Point3 hi)
    {
        Point3 C(int k) => new((k & 1) == 0 ? lo.X : hi.X, (k & 2) == 0 ? lo.Y : hi.Y, (k & 4) == 0 ? lo.Z : hi.Z);
        foreach (var (a, b) in new[] { (0, 1), (2, 3), (4, 5), (6, 7), (0, 2), (1, 3), (4, 6), (5, 7), (0, 4), (1, 5), (2, 6), (3, 7) })
            into.Add(new DrawSegment(C(a), C(b)));
    }

    private static void Ring(List<DrawSegment> into, Point3 c, System.Numerics.Vector3 n, double r)
    {
        var nn = System.Numerics.Vector3.Normalize(n);
        var u = System.Numerics.Vector3.Normalize(Math.Abs(nn.Z) < 0.9f ? System.Numerics.Vector3.Cross(nn, System.Numerics.Vector3.UnitZ)
                                                                         : System.Numerics.Vector3.Cross(nn, System.Numerics.Vector3.UnitX));
        var v = System.Numerics.Vector3.Cross(nn, u);
        const int k = 32;
        Point3 P(int i)
        {
            double t = 2 * Math.PI * i / k;
            return new Point3(c.X + r * (Math.Cos(t) * u.X + Math.Sin(t) * v.X), c.Y + r * (Math.Cos(t) * u.Y + Math.Sin(t) * v.Y),
                              c.Z + r * (Math.Cos(t) * u.Z + Math.Sin(t) * v.Z));
        }
        for (int i = 0; i < k; i++) into.Add(new DrawSegment(P(i), P(i + 1)));
    }

    // ── the tree (R-em3d75-1b) ───────────────────────────────────────────────────────────────

    private static readonly C3dTreeGroupRole[] ThermalRoles =
        [C3dTreeGroupRole.HeatSources, C3dTreeGroupRole.Probes, C3dTreeGroupRole.MeshRegions, C3dTreeGroupRole.EffectiveBlocks,
         C3dTreeGroupRole.SymmetryPlanes, C3dTreeGroupRole.ThermalBoundaries];

    /// <summary>The thermal groups, rebuilt with the records: Heat sources, Probes, Mesh regions, and the active thermal
    /// setup's boundaries (the tint legend: blue a fixed temperature, green convection).</summary>
    private void RebuildThermalTree()
    {
        foreach (var g in Tree.Where(g => ThermalRoles.Contains(g.Role)).ToList())
        {
            DetachExpansion([g]);
            Tree.Remove(g);
        }
        C3dTreeItem Row(string name, string kind, string detail) => new(this, name, kind, detail, -1, -1, IsPlaceShown(name));
        var sources = Document.HeatSources.Select(h => Row(h.Name, HeatSourceKind,
            (h.Solid is { } s ? $"in {s}" : "sheet") + $", {h.Power ?? "no default power"}{(h.Density == C3dHeatDensity.Total ? (h.Power is null ? "" : " W") : h.Density == C3dHeatDensity.PerArea ? " W/m²" : " W/m³")}")).ToList();
        var probes = Document.Probes.Select(p => Row(p.Name, ProbeKind,
            string.Join(", ", p.Kinds()).ToLowerInvariant() + (p.Stat is { } st ? $", {st.ToString().ToLowerInvariant()}" : "") +
            (p.LimitC is { } l ? $", limit {l.ToString("G4", CultureInfo.InvariantCulture)} °C" : ""))).ToList();
        var regions = Document.MeshRegions.Select(m => Row(m.Name, MeshRegionKind, $"{SpellMicrons(m.SizeUm)} {LayoutUnits.Suffix(Document.DisplayUnit)} elements")).ToList();
        if (sources.Count > 0) Tree.Add(new C3dTreeGroup("Heat sources", sources, C3dTreeGroupRole.HeatSources));
        if (probes.Count > 0) Tree.Add(new C3dTreeGroup("Probes", probes, C3dTreeGroupRole.Probes));
        if (regions.Count > 0) Tree.Add(new C3dTreeGroup("Mesh regions", regions, C3dTreeGroupRole.MeshRegions));
        // brief-em3d-76 — effective blocks (with whether each is on) and the symmetry planes
        var blocks = Document.EffectiveBlocks.Select(b => Row(b.Name, EffectiveBlockKind, b.Enabled ? "enabled (an approximation)" : "disabled: solved as drawn")).ToList();
        if (blocks.Count > 0) Tree.Add(new C3dTreeGroup("Effective blocks", blocks, C3dTreeGroupRole.EffectiveBlocks));
        // brief-em3d-90 — a plane is drawn in the overlay and ticked in the view; it is selected here, not in the view
        var planes = Document.SymmetryPlanes.Select(sp => new C3dTreeItem(this, SymmetryRowName(sp.Axis), SymmetryPlaneKind,
            $"{sp.Axis} = {SpellMicrons(sp.At / (double)Document.DbuPerMicron)} {LayoutUnits.Suffix(Document.DisplayUnit)}", -1, -1, IsSymmetryPlaneShown(sp.Axis))
        {
            IsReadOnly = true,
            RowTip = "Drawn hatched across the model where it cuts it; select it from the tree (the view does not pick it). Properties edits where it lies.",
        }).ToList();
        if (planes.Count > 0)
            Tree.Add(new C3dTreeGroup($"Symmetry planes (1/{1 << planes.Count} of the device is modelled)", planes, C3dTreeGroupRole.SymmetryPlanes));
        RebuildFieldPlotTree();                    // brief-em3d-83 — after Probes, before the regions
        if (ActiveThermalSetup()?.Setup.Thermal?.Boundaries is { Count: > 0 } bs)
            Tree.Add(new C3dTreeGroup($"Thermal boundaries (blue fixed, green convection) · {ActiveSetupName}", [.. bs.Select(b => new C3dTreeItem(this,
                ThermalTintPrefix + b.Face, ThermalBoundaryKindName, b.Kind == ThermalBoundaryKind.FixedT ? $"{b.Face}: {b.TempC} °C"
                    : $"{b.Face}: h {b.H} W/(m²·K) to {b.AmbientC} °C", -1, -1, IsBoundaryShown(ThermalTintPrefix + b.Face)) { IsReadOnly = true })],
                C3dTreeGroupRole.ThermalBoundaries));
        RestoreExpansion();
    }

    /// <summary>A thermal boundary's tint is named this, then its face (<c>thermal:flange/zmin</c>).</summary>
    public const string ThermalTintPrefix = Scene3DBuilder.ThermalTintPrefix;

    /// <summary>The tree's thermal row's menu: rename, hide, delete, and a line probe's plot.</summary>
    private List<Viewer3DMenuItem> ThermalTreeItems(C3dTreeItem item)
    {
        var items = new List<Viewer3DMenuItem>();
        // brief-em3d-90 — every row's Hide / Show is the tick's own function
        var hideShow = new Viewer3DMenuItem(item.IsVisible ? "Hide" : "Show", () => SetRowsVisible([item], !item.IsVisible, $"{(item.IsVisible ? "Hide" : "Show")} {item.Name}"));
        if (item.Kind == ThermalBoundaryKindName)
        {
            string face = item.Name[ThermalTintPrefix.Length..];
            items.Add(new Viewer3DMenuItem("Select Face", () => Report(SelectBoundaryFace(face)), Tip: "The face this boundary conditions, in Face mode."));
            items.Add(hideShow);
            items.Add(Viewer3DMenuItem.Separator);
            items.Add(new Viewer3DMenuItem("Delete", () => Report(SetThermalBoundary(face, null))));
            return items;
        }
        if (item.Kind == SymmetryPlaneKind)
        {
            var axis = Enum.Parse<C3dAxis>(item.Name[SymmetryRowPrefix.Length..]);
            items.Add(hideShow);
            items.Add(Viewer3DMenuItem.Separator);
            items.Add(new Viewer3DMenuItem("Delete", () => ClearSymmetryPlane(axis)));
            return items;
        }
        string name = item.Name;
        items.Add(new Viewer3DMenuItem("Rename…", () => TextRequested?.Invoke($"Rename {name}", "Name:", name, text => RenameThermalPlace(name, text))));
        if (item.Kind == HeatSourceKind)
            items.Add(new Viewer3DMenuItem("Default Power…", () => TextRequested?.Invoke($"Heat source {name}", "Default power, W (a number or an expression):",
                Document.HeatSources.FirstOrDefault(h => h.Name == name)?.Power ?? "", text => SetPlaceText(name, "Power", text))));
        // brief-em3d-93 D1 — a heat source's Model: kept, drawn and editable, and no power in any run
        if (item.Kind == HeatSourceKind && Document.HeatSources.FirstOrDefault(h => h.Name == name) is { } source)
            items.Add(new Viewer3DMenuItem(source.Model ? "✓ Model" : "Model", () => SetHeatSourceModel(name, !source.Model),
                Tip: "Unticked, the source is kept and puts no power in any thermal run."));
        if (item.Kind == ProbeKind && Document.Probes.FirstOrDefault(p => p.Name == name) is { Line: not null })
            items.Add(new Viewer3DMenuItem("Plot T(s)", () => Report(PlotLineProbe(name)), Enabled: ActiveThermalTable is not null,
                Tip: ActiveThermalTable is null ? NoActiveThermalTable() : "The line's temperature at the step shown."));
        // brief-em3d-80 — a place the run computed Z_th of: its magnitude and phase, per watt in each source (a source's own first)
        if (item.Kind is HeatSourceKind or ProbeKind && ActiveThermalTable?.Zth.Where(z => z.Place == name).OrderBy(z => z.Source == name ? 0 : 1).Take(8).ToList() is { Count: > 0 } zs)
            foreach (var z in zs)
            {
                string per = z.Source == name ? "" : $" per watt in '{z.Source}'";
                items.Add(new Viewer3DMenuItem($"Plot |Z_th|{per}", () => Report(PlotZth(name, z.Source, phase: false)),
                    Tip: "The thermal impedance's magnitude against frequency, from the active setup's run."));
                items.Add(new Viewer3DMenuItem($"Plot Z_th phase{per}", () => Report(PlotZth(name, z.Source, phase: true))));
            }
        if (item.Kind == EffectiveBlockKind && Document.EffectiveBlocks.FirstOrDefault(b => b.Name == name) is { } block)
            items.Add(new Viewer3DMenuItem(block.Enabled ? "Disable" : "Enable", () => Report(SetPlaceText(name, "Enabled", block.Enabled ? "false" : "true")),
                Tip: block.Enabled ? "Solve the geometry under it as drawn." : "Replace the board, planes and vias inside it with one anisotropic block (an approximation)."));
        items.Add(hideShow);
        items.Add(Viewer3DMenuItem.Separator);
        items.Add(new Viewer3DMenuItem("Delete", () => DeleteThermalPlace(name)));
        return items;
    }

    /// <summary>A place's row tick: the view's alone (a place has no Hidden in the file, and a hidden probe still reads).</summary>
    public void SetPlaceShown(string name, bool shown)
    {
        if (shown) _hiddenPlaces.Remove(name); else _hiddenPlaces.Add(name);
        if (AllTreeItems().FirstOrDefault(t => t.Name == name && t.Kind is HeatSourceKind or ProbeKind or MeshRegionKind or EffectiveBlockKind or LumpedElementKind) is { } row) row.Sync(shown);
        Viewer.RequestFrame();
    }

    // ── the scene's thermal tints (R-em3d75-2) ───────────────────────────────────────────────

    /// <summary>On the build's thread: the active thermal setup's conditioned faces as tints — blue a fixed temperature, green
    /// convection — placed by the lowering's own face placement.</summary>
    /// <para>brief-em3d-90 R-em3d90-2 — a boundary whose row is unticked (<paramref name="hidden"/>, by tint name, handed in as
    /// data by the snapshot) is left out: its tint is neither drawn nor picked.</para>
    private static IReadOnlyList<Scene3DFaceTint> ThermalTints(C3dDocument doc, C3dElaboration e, EmSetup? setup, IReadOnlySet<string>? hidden = null)
    {
        if (setup is not { IsThermal: true, Thermal.Boundaries: { Count: > 0 } bs } || !e.Ok) return [];
        var list = new List<Scene3DFaceTint>();
        var solids = e.Solids;
        foreach (var b in bs)
        {
            if (b.Face == C3dThermal.ExposedFaces || hidden?.Contains(ThermalTintPrefix + b.Face) == true) continue;
            if (ThermalLowerings.FacePieces(doc, e, solids, b.Face, out _, out _) is not { } pieces) continue;
            var colour = b.Kind == ThermalBoundaryKind.FixedT ? ((byte)60, (byte)120, (byte)235) : ((byte)60, (byte)185, (byte)95);
            list.Add(new Scene3DFaceTint(ThermalTintPrefix + b.Face, Em3dFaceBoundaryKind.Pec, pieces, colour));
        }
        return list;
    }
}

/// <summary>Plots and hatches the thermal editor draws.</summary>
public static class ThermalPlots
{
    /// <summary>A heat source's hatch: 45° lines across its outline (u, v), a tenth of its larger side apart, each clipped to
    /// the outline by the even-odd rule.</summary>
    public static IEnumerable<(C3dPoint2 A, C3dPoint2 B)> Hatch(IReadOnlyList<C3dPoint2> outline)
    {
        long u0 = outline.Min(p => p.U), u1 = outline.Max(p => p.U), v0 = outline.Min(p => p.V), v1 = outline.Max(p => p.V);
        double step = Math.Max(1, Math.Max(u1 - u0, v1 - v0) / 10.0);
        // Lines u − v = c.
        for (double c = u0 - v1 + step / 2; c < u1 - v0; c += step)
        {
            var hits = new List<double>();   // the v of each crossing
            for (int i = 0; i < outline.Count; i++)
            {
                var a = outline[i];
                var b = outline[(i + 1) % outline.Count];
                double fa = a.U - a.V - c, fb = b.U - b.V - c;
                if ((fa < 0) == (fb < 0) || fa == fb) continue;
                double t = fa / (fa - fb);
                hits.Add(a.V + t * (b.V - a.V));
            }
            hits.Sort();
            for (int k = 0; k + 1 < hits.Count; k += 2)
            {
                long R(double x) => (long)Math.Round(x);
                yield return (new C3dPoint2(R(c + hits[k]), R(hits[k])), new C3dPoint2(R(c + hits[k + 1]), R(hits[k + 1])));
            }
        }
    }

    /// <summary>R-em3d75-4d — a temperature line as the existing plotting control draws a trace: distance against °C.</summary>
    public static CircuitRF.Render.DataDisplay.Plot Line(C3dThermalLine line)
    {
        var plot = new CircuitRF.Render.DataDisplay.Plot(CircuitRF.Render.DataDisplay.PlotType.Rect, CircuitRF.Render.DataDisplay.FreqUnit.GHz)
        {
            ShowWatermark = false,
            CustomTitleOn = true, CustomTitle = line.Title, CustomTitleBold = true,
            CustomXLabelOn = true, CustomXLabel = $"Distance ({line.UnitSuffix})",
            CustomYLabelOn = true, CustomYLabel = "T (°C)",
        };
        // brief-em3d-79: a probe row against the sweep (or a carried cube) names its own axes and plots x as it is
        if (line.XLabel is not null) { plot.CustomXLabel = line.XLabel; plot.CustomYLabel = line.YLabel; }
        // brief-em3d-80: Z_th against a logarithmic frequency
        if (line.LogX) plot.Axes.XScale = CircuitRF.Render.DataDisplay.AxisScale.Log;
        double xScale = line.XLabel is null ? line.UnitsPerMetre : 1;
        var keep = Enumerable.Range(0, line.DistanceM.Length).Where(i => double.IsFinite(line.ValuesC[i]) && double.IsFinite(line.DistanceM[i]))
                             .OrderBy(i => line.DistanceM[i]).ToList();
        double[] x = [.. keep.Select(i => line.DistanceM[i] * xScale)], y = [.. keep.Select(i => line.ValuesC[i])];
        if (x.Length < 2) return plot;
        var t = new CircuitRF.Render.DataDisplay.Trace(new RfCore.SNP([1e9], 1), RfCore.MatrixType.S, 0, 0, CircuitRF.Render.DataDisplay.DependentVarFormat.Real);
        t.Properties.LineColorStorage = new SkiaSharp.SKColor(230, 90, 40);
        t.Properties.LineWidth = 1.8;
        t.Properties.LineEnabled = true;
        t.SetCubeData(x, null, y, "x", null, CircuitRF.Render.DataDisplay.PlotType.Rect, CircuitRF.Render.DataDisplay.FreqUnit.GHz);
        plot.Traces.Add(t);
        double lo = y.Min(), hi = y.Max(), h = hi > lo ? hi - lo : 1;
        plot.Axes.Window = new CircuitRF.Render.DataDisplay.PlotRect(x[0], lo - 0.05 * h, x[^1] - x[0], 1.1 * h);
        return plot;
    }
}
