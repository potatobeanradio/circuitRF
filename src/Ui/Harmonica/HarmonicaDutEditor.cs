using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Devices.External;
using CircuitRF.Harmonica;

namespace CircuitRF.Ui.Harmonica;

/// <summary>
/// The staged edit behind <i>Set DUT…</i> — everything the dialog decides, with none of the window.
///
/// <para>Framework-free on purpose, the same split <c>ScaleFieldLinker</c> and <c>InstanceCellChoices</c>
/// already established here: a <c>Window</c> subclass cannot be constructed in this project's headless
/// tests, so any logic that lives in one is logic nothing can check. What lives in the window is the
/// file picker and the folder picker, and nothing else.</para>
///
/// <para><b>It produces a <see cref="DutSpec"/> and stops</b> (R-h8-1). Applying it is
/// <see cref="HarmonicaViewModel.ApplyDut"/>'s job, which is a sibling of H7's own structural
/// write-back rather than a second mechanism.</para>
/// </summary>
public sealed class HarmonicaDutEditor
{
    private readonly Dictionary<string, string> _parameters = new(StringComparer.Ordinal);

    /// <summary>Starts from the DUT a document is currently carrying, so re-opening the dialog and
    /// pressing Set DUT is a no-op rather than a reset.</summary>
    public HarmonicaDutEditor(DutSpec current)
    {
        ArgumentNullException.ThrowIfNull(current);
        Kind         = current.Kind;
        TypeName     = current.TypeName;
        Provider     = current.Provider;
        Multiplicity = current.Multiplicity;
        SddPortCount = current.SddPortCount is 3 ? 3 : 2;
        GateNode     = current.IntrinsicMapping?.GateNode;
        DrainNode    = current.IntrinsicMapping?.DrainNode;
        SourcePin    = current.IntrinsicMapping?.SourcePin;
        foreach (var (k, v) in current.Parameters) _parameters[k] = v;
    }

    public DutKind Kind         { get; private set; }
    public string  TypeName     { get; private set; }
    public string? Provider     { get; private set; }
    public double  Multiplicity { get; set; }

    /// <summary>R-h9c-11 — 2 or 3. Meaningful only while <see cref="Kind"/> is
    /// <see cref="DutKind.Sdd"/>; carried regardless so re-selecting SDD after a detour through
    /// another kind remembers the user's last choice.</summary>
    public int SddPortCount { get; set; } = 2;

    public string? GateNode  { get; set; }
    public string? DrainNode { get; set; }
    public string? SourcePin { get; set; }

    /// <summary>The staged parameter values, keyed as the model spells them.</summary>
    public IReadOnlyDictionary<string, string> Parameters => _parameters;

    public void SetParameter(string name, string value) => _parameters[name] = value ?? "";

    // ── choosing a device ─────────────────────────────────────────────────────

    /// <summary>
    /// Switches kind. Parameters are RESEEDED from the newly-chosen model's own declaration, never
    /// carried across: an SDD's parameters are equation text and a FET's are scalars with the same
    /// spellings meaning different quantities, so carrying them would produce a device configured
    /// with another device's numbers.
    /// </summary>
    public void SetKind(DutKind kind)
    {
        if (kind == Kind) return;
        Kind = kind;

        switch (kind)
        {
            case DutKind.Sdd:
                TypeName = "SDD";
                Provider = null;
                Reseed(HarmonicaViewModel.DefaultModel().Dut.Parameters);
                break;

            case DutKind.NativeFet:
                Provider = null;
                SetNativeLaw(HarmonicaDutCatalog.NativeFetLaws[0].TypeName);
                break;

            case DutKind.Diode:
                TypeName = "Diode";
                Provider = null;
                Reseed(HarmonicaDutCatalog.DefaultParametersFor("Diode"));
                break;

            case DutKind.External:
                TypeName = "";
                Provider = null;
                _parameters.Clear();
                break;
        }
    }

    /// <summary>Picks one of the five native laws, reseeding its OWN declared parameter set — each
    /// law has a different one, so this is not optional bookkeeping.</summary>
    public void SetNativeLaw(string engineTypeName)
    {
        Kind     = DutKind.NativeFet;
        Provider = null;
        if (string.Equals(TypeName, engineTypeName, StringComparison.Ordinal) && _parameters.Count > 0)
            return;
        TypeName = engineTypeName;
        Reseed(HarmonicaDutCatalog.DefaultParametersFor(engineTypeName));
    }

    /// <summary>
    /// Points at one device type of one external provider, reseeding from the model's own declared
    /// defaults. Re-selecting the type already chosen keeps whatever the user typed.
    /// </summary>
    public void SetExternal(string provider, ExternalDeviceDescriptor? descriptor)
    {
        Kind = DutKind.External;

        bool same = string.Equals(Provider, provider, StringComparison.Ordinal)
                 && string.Equals(TypeName, descriptor?.TypeId ?? "", StringComparison.Ordinal);

        Provider = provider;
        TypeName = descriptor?.TypeId ?? "";
        if (same) return;

        // A DIFFERENT model means a different intrinsic plane. Carrying the old node names across
        // would silently point the mapping at labels this model may not declare — and the mapping is
        // the one thing here whose wrong answer is invisible.
        GateNode = DrainNode = SourcePin = null;

        _parameters.Clear();
        if (descriptor is null) return;
        foreach (var p in descriptor.Parameters)
            if (p.DefaultText is { Length: > 0 } d) _parameters[p.Name] = d;
    }

    private void Reseed(IReadOnlyDictionary<string, string> defaults)
    {
        _parameters.Clear();
        foreach (var (k, v) in defaults) _parameters[k] = v;
    }

    // ── the result ────────────────────────────────────────────────────────────

    /// <summary>
    /// Why this cannot be applied yet, or null. The intrinsic mapping is deliberately NOT in here:
    /// an external DUT with no mapping is a legitimate, applyable state (§4.5.5 — the panels draw
    /// empty and say why), not an error to block on.
    /// </summary>
    public string? Validate()
    {
        if (Multiplicity <= 0) return "The device multiplier must be greater than zero.";

        if (Kind == DutKind.External)
        {
            if (string.IsNullOrWhiteSpace(Provider))
                return "Choose a model file or a kit before setting the DUT.";
            if (string.IsNullOrWhiteSpace(TypeName))
                return "Choose which of the model's device types this DUT is.";
        }
        else if (string.IsNullOrWhiteSpace(TypeName))
        {
            return "Choose a device.";
        }

        // A partly-named intrinsic plane is the one genuinely wrong state: two thirds of a mapping
        // resolves to nothing and would be indistinguishable from having named none of it.
        int named = (GateNode is { Length: > 0 } ? 1 : 0)
                  + (DrainNode is { Length: > 0 } ? 1 : 0)
                  + (SourcePin is { Length: > 0 } ? 1 : 0);
        if (named is > 0 and < 3)
            return "Name all three of the intrinsic gate, drain and source — or none of them.";

        return null;
    }

    /// <summary>The staged DUT. Throws nothing; call <see cref="Validate"/> first.</summary>
    public DutSpec Build() => new()
    {
        Kind         = Kind,
        TypeName     = TypeName,
        Provider     = Kind == DutKind.External ? Provider : null,
        Multiplicity = Multiplicity,
        SddPortCount = Kind == DutKind.Sdd && SddPortCount == 3 ? 3 : 2,
        Parameters   = new Dictionary<string, string>(_parameters, StringComparer.Ordinal),
        IntrinsicMapping =
            Kind == DutKind.External
            && GateNode  is { Length: > 0 } g
            && DrainNode is { Length: > 0 } d
            && SourcePin is { Length: > 0 } s
                ? new IntrinsicMapping(g, d, s)
                : null,
    };

    /// <summary>
    /// The node names a mapping may be chosen from — the model's OWN labels
    /// (<c>ExternalNodeDescriptor.Label</c>), never a list circuitRF invented. An unlabelled node is
    /// offered by its index, which is the only other name it has.
    /// </summary>
    public static IReadOnlyList<string> NodeChoices(ExternalDeviceDescriptor? descriptor)
        => descriptor is null
            ? []
            : [.. descriptor.Nodes.OrderBy(n => n.Index)
                            .Select(n => string.IsNullOrWhiteSpace(n.Label)
                                ? n.Index.ToString() : n.Label)];
}
