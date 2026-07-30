using System.Globalization;
using CircuitRF.Core.Expressions;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// docs/sonnet-briefs/brief-L5-schematic-to-layout.md §3A — "Update Schematic from Layout," the
/// mechanical inverse of <see cref="SchematicToLayoutGenerator"/>. Walks a layout's own
/// <see cref="LayoutInstance"/>s (never the schematic — the schematic is the thing being written TO)
/// and computes the schematic component creates/edits needed to match, plus the same shape of change
/// report R-L5-22 asks for ("on the same terms as §2.2").
///
/// <b>Scope, stated plainly (R-L5-19's own "place or update component instances. No wiring"):</b> only
/// PCell-backed instances (<see cref="PCellOrigin"/> non-null) participate in the create half — an
/// instance resolving to a hand-drawn, non-generated cell has no PARAMETER to push back and no
/// existing symbol this command could safely fabricate, so it is left for a future increment rather
/// than guessed at here. A LINKED (SchematicId already set) instance whose cell is NOT PCell-backed
/// is likewise left alone — there is nothing to push into its already-existing schematic component.
/// Framework-free except for the <see cref="IUiCommand"/>s it returns (never executes them itself —
/// same contract as <see cref="SchematicToLayoutGenerator.Run"/>).
/// </summary>
public static class LayoutToSchematicGenerator
{
    public sealed record GenerationResult(
        IUiCommand? Command,
        IReadOnlyList<SchematicToLayoutGenerator.ReportLine> Lines,
        int CreatedCount,
        int UpdatedCount,
        int UnchangedCount,
        int OverwrittenParameterCount)
    {
        public bool NothingChanged => Command is null;
    }

    private static readonly Dictionary<string, SymbolKind> ReverseGeneratorMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["MLIN"]   = SymbolKind.Mlin,
            ["MBEND"]  = SymbolKind.MBend,
            ["MTEE"]   = SymbolKind.MTee,
            ["MCROSS"] = SymbolKind.MCross,
            ["MTAPER"] = SymbolKind.Mtaper,
            ["MKLOPF"] = SymbolKind.Mklopf,
        };

    /// <summary>Public lookup for the same generator-id → SymbolKind map §5's Properties Inspector
    /// parameter list uses to order a PCell instance's parameters the same way the schematic's own
    /// symbol declares them (<c>ComponentTypeRegistry.DefaultParameters</c>), rather than an arbitrary
    /// dictionary order.</summary>
    public static bool TryGetSymbolKind(string generatorId, out SymbolKind kind) =>
        ReverseGeneratorMap.TryGetValue(generatorId, out kind);

    private const int GridCols = 8;
    private const double GridPitchSchematic = 400; // schematic world units — a comfortable non-overlapping spacing

    /// <param name="technology">
    /// The resolved technology governing <paramref name="source"/> (its own <c>LayoutEditorViewModel.
    /// Technology</c>) — used ONLY for a freshly-CREATED schematic component's Length-dimensioned
    /// parameters (docs/sonnet-briefs/brief-misc-termg-units-technologies.md §2, R-misc-3/4): a
    /// layout-first PCell instance has no existing schematic parameter to read a unit from, so one
    /// must be chosen, and R-misc-4's answer is the technology's own <c>DefaultDisplayUnit</c> (mil
    /// on a PCB, µm on an MMIC die) via the SAME <see cref="MicrostripSubstrateInjection.LengthUnitFor"/>
    /// helper the schematic-side placement path (<c>SchematicViewModel.CommitPlacement</c> →
    /// <c>ApplyTechnologyLengthUnit</c>) and the MKlopf entry-mode toggle already use — never a
    /// second conversion. Null (no technology resolved) falls back to "mm", matching every other
    /// technology-absent case in this codebase. An ALREADY-LINKED component's parameter keeps
    /// whatever unit it was authored with — the schematic side is never silently rewritten to the
    /// technology default on every push, only a brand-new field picks one.
    /// </param>
    public static GenerationResult Run(LayoutView source, SchematicEditModel schematic, string layoutBaseDir, Technology? technology = null)
    {
        var lines = new List<SchematicToLayoutGenerator.ReportLine>();
        IUiCommand? chain = null;
        int created = 0, updated = 0, unchanged = 0, overwritten = 0;

        var bySchematicId = new Dictionary<string, EditableComponent>(StringComparer.Ordinal);
        foreach (var c in schematic.Components)
            if (!string.IsNullOrEmpty(c.InstanceName) && !bySchematicId.ContainsKey(c.InstanceName))
                bySchematicId[c.InstanceName] = c;

        var scope = BuildVariableScope(schematic);
        var evaluator = new Evaluator();

        int newSlot = 0;
        foreach (var inst in source.Instances)
        {
            var res = CellLayoutResolver.Resolve(inst.CellRef, layoutBaseDir);
            if (res.State != CellLayoutState.Resolved || res.View!.PCellOrigin is not { } origin)
                continue; // broken instance, or not PCell-backed — nothing this command can push (see scope note above)

            if (!ReverseGeneratorMap.TryGetValue(origin.GeneratorId, out var kind))
                continue; // a foreign/unknown generator id — nothing to map to a SymbolKind

            bool linked = inst.SchematicId is { Length: > 0 } sid0 && bySchematicId.TryGetValue(sid0, out _);
            var comp = linked ? bySchematicId[inst.SchematicId!] : null;

            if (comp is null)
            {
                // R-L5-20: create half — writes SchematicId as it goes.
                comp = new EditableComponent
                {
                    Symbol       = kind,
                    InstanceName = SchematicEditModel.NextAvailableName(schematic.Components, kind),
                    X            = (newSlot % GridCols) * GridPitchSchematic,
                    Y            = (newSlot / GridCols) * GridPitchSchematic,
                };
                newSlot++;
                foreach (var dp in ComponentTypeRegistry.DefaultParameters(kind, 0))
                    comp.Parameters.Add(new EditableParameter
                    {
                        Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                        ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
                    });
                ApplyPCellParamsToComponent(comp, origin.Parameters, technology);

                chain = Chain(chain, new Commands.Schematic.PlaceComponentCommand(schematic, comp));
                inst.SchematicId = comp.InstanceName; // bookkeeping — not part of the undo entry, mirrors
                                                       // SchematicPCellSnapshots below (R-L5-13's own note:
                                                       // "what is reported is what happened").
                created++;
                lines.Add(new SchematicToLayoutGenerator.ReportLine(comp.InstanceName,
                    $"{comp.InstanceName} — created from layout", SchematicToLayoutGenerator.ReportSeverity.Info));
                source.SchematicPCellSnapshots[comp.InstanceName] = new Dictionary<string, double>(origin.Parameters);
                continue;
            }

            // R-L5-19/22: linked — push parameters back, classified the same way §2.2 classifies the
            // forward direction, roles reversed: a SCHEMATIC value that has diverged from the snapshot
            // is about to be discarded (warning); a value moving purely because the LAYOUT changed is
            // the expected case (informational).
            source.SchematicPCellSnapshots.TryGetValue(comp.InstanceName, out var snapshot);
            bool reportedThisInstance = false;
            bool anyChanged = false;

            foreach (var (name, layoutVal) in origin.Parameters)
            {
                var param = comp.Parameters.FirstOrDefault(p => p.Name == name);
                if (param is null) continue;
                if (!SchematicToLayoutGenerator.TryResolveSiValue(param.Expression, param.Unit, scope, evaluator, out var schematicVal, out _))
                    continue;

                if (SchematicToLayoutGenerator.NearlyEqual(schematicVal, layoutVal)) continue;

                bool hadSnapshot    = snapshot is not null && snapshot.TryGetValue(name, out _);
                double snap         = hadSnapshot ? snapshot![name] : schematicVal;
                bool schematicMoved = hadSnapshot && !SchematicToLayoutGenerator.NearlyEqual(snap, schematicVal);
                bool layoutMoved    = !hadSnapshot || !SchematicToLayoutGenerator.NearlyEqual(snap, layoutVal);

                bool isWarning = schematicMoved; // the schematic's own edit is what's about to be lost
                if (!schematicMoved && !layoutMoved) continue;

                string displayExpr = ToDisplayExpression(param.Unit, layoutVal);
                chain = Chain(chain, new Commands.Schematic.EditParameterCommand(schematic, param, displayExpr, param.Unit));
                anyChanged = true;

                string unitSuffix = string.IsNullOrEmpty(param.Unit) ? "" : $" {param.Unit}";
                lines.Add(new SchematicToLayoutGenerator.ReportLine(comp.InstanceName,
                    $"{comp.InstanceName} — {name} changed from {SchematicToLayoutGenerator.Fmt(SchematicToLayoutGenerator.ToDisplayValue(param.Unit, schematicVal))}{unitSuffix} " +
                    $"to {SchematicToLayoutGenerator.Fmt(SchematicToLayoutGenerator.ToDisplayValue(param.Unit, layoutVal))}{unitSuffix}" +
                    (isWarning ? " (a schematic edit is being overwritten)" : " (from layout)"),
                    isWarning ? SchematicToLayoutGenerator.ReportSeverity.Warning : SchematicToLayoutGenerator.ReportSeverity.Info));
                reportedThisInstance = true;
                if (isWarning) overwritten++;
            }

            source.SchematicPCellSnapshots[comp.InstanceName] = new Dictionary<string, double>(origin.Parameters);

            if (anyChanged)
            {
                updated++;
                if (!reportedThisInstance)
                    lines.Add(new SchematicToLayoutGenerator.ReportLine(comp.InstanceName, $"{comp.InstanceName} — updated", SchematicToLayoutGenerator.ReportSeverity.Info));
            }
            else
            {
                unchanged++;
            }
        }

        return new GenerationResult(chain, lines, created, updated, unchanged, overwritten);
    }

    /// <summary>R-misc-3/4: writes coefficient AND unit for a freshly-created component's parameters
    /// — a Length-dimensioned field's <see cref="EditableParameter.Unit"/> is rewritten from
    /// <c>DefaultParameters</c>' hardcoded "mm" baseline to <paramref name="technology"/>'s own
    /// <see cref="MicrostripSubstrateInjection.LengthUnitFor"/> BEFORE the coefficient is computed
    /// through it, so the two can never disagree (unlike writing the number first and the unit
    /// separately). Non-Length fields (Ω, dimensionless) keep whatever unit <c>DefaultParameters</c>
    /// already gave them — only length physically differs by workspace convention.</summary>
    private static void ApplyPCellParamsToComponent(EditableComponent comp, IReadOnlyDictionary<string, double> layoutParams, Technology? technology)
    {
        string lengthUnit = MicrostripSubstrateInjection.LengthUnitFor(technology);
        foreach (var param in comp.Parameters)
        {
            if (!layoutParams.TryGetValue(param.Name, out var v)) continue;
            if (param.Dimension == UnitDimension.Length) param.Unit = lengthUnit;
            param.Expression = ToDisplayExpression(param.Unit, v);
        }
    }

    /// <summary>Schematic Expression string for a PCell-SI value — <see cref="SchematicToLayoutGenerator.ToDisplayValue"/>
    /// (the shared inverse conversion) formatted the way an <see cref="EditableParameter.Expression"/>
    /// is stored: a bare number in the parameter's own unit.</summary>
    private static string ToDisplayExpression(string? unit, double siValue)
        => SchematicToLayoutGenerator.ToDisplayValue(unit, siValue).ToString("0.######", CultureInfo.InvariantCulture);

    private static Scope BuildVariableScope(SchematicEditModel schematic)
    {
        var scope = new Scope("global");
        foreach (var comp in schematic.Components)
        {
            if (comp.Disable is DisableState.Open or DisableState.Short) continue;
            if (comp.Symbol != SymbolKind.Var) continue;
            foreach (var p in comp.Parameters)
            {
                if (string.IsNullOrWhiteSpace(p.Name)) continue;
                string? unit = UnitNormalizer.ToEngineUnit(p.Unit) is { Length: > 0 } u ? u : null;
                scope.Bind(p.Name.Trim(), p.Expression, unit);
            }
        }
        return scope;
    }

    private static IUiCommand Chain(IUiCommand? existing, IUiCommand next)
        => existing is null ? next : new CompositeCommand(existing, next);
}
