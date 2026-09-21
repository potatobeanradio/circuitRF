namespace CircuitRF.Core.Design;

/// <summary>
/// The instance parameters that describe a component's ARTWORK and nothing else — brief-footprint-2
/// R-fp2-6.
///
/// <para><b>The invariant this serves.</b> The numeric layer sees only fully-resolved parameter
/// values. <c>Footprint</c> is not a value: <c>smt:0402@N</c> is not an expression, and the
/// evaluator reads it as an identifier and fails. So it is dropped BEFORE parameter resolution, in
/// the one place every instance's overrides are collected, for every component kind alike.</para>
///
/// <para><b>Deliberately not a per-family list.</b> <c>Elaborator</c> already carries one of those —
/// <c>_snpStringParams</c>, which stores an SnP's <c>File</c>/<c>PinConfig</c>/… raw — and it is the
/// wrong shape to copy here. A universal artwork parameter added to a per-family list is a parameter
/// that leaks the first time somebody puts a footprint on a family nobody enumerated, and the leak
/// is a parse error from inside a value rather than anything that names the cause.</para>
///
/// <para><b>It still travels in the <c>.cnl</c>.</b> Dropping happens at elaboration, not at
/// extraction or at write: <c>circuitrf netlist</c> writes the netlist a run consumes, and a
/// footprint dropped there is a footprint a headless Update Layout could not see (R-fp2-6d). It is
/// written, read, and ignored by the engine.</para>
/// </summary>
public static class ArtworkParameters
{
    /// <summary>The instance parameter naming this component's land pattern. One spelling, shared by
    /// the schematic model, the parameter editor, the netlist and this drop.</summary>
    public const string FootprintName = "Footprint";

    private static readonly HashSet<string> _names =
        new(StringComparer.OrdinalIgnoreCase) { FootprintName };

    /// <summary>Every artwork-only parameter name, for a caller that has to state the same exclusion
    /// on its own side (the PCell dimension dictionary, for one).</summary>
    public static IReadOnlyCollection<string> Names => _names;

    /// <summary>True when <paramref name="name"/> describes artwork and must never be evaluated.</summary>
    public static bool IsArtworkOnly(string? name) => name is not null && _names.Contains(name);

    /// <summary>
    /// <paramref name="instance"/> with every artwork-only override removed — or
    /// <paramref name="instance"/> itself when it carries none, which is the ordinary case and costs
    /// one scan.
    /// </summary>
    public static Instance WithoutArtworkOverrides(Instance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        bool any = false;
        foreach (var ov in instance.Overrides)
            if (_names.Contains(ov.Name)) { any = true; break; }
        if (!any) return instance;

        return new Instance(
            instance.InstanceName,
            instance.Reference,
            instance.NetBindings,
            instance.Overrides.Where(o => !_names.Contains(o.Name)))
        {
            // An init-only property is not carried by the constructor, and an SnP's floating
            // reference net is the whole N-or-N+1 rule. Dropping it here would re-ground every
            // SnP that also carried a footprint.
            RefNetBinding = instance.RefNetBinding,
        };
    }
}
