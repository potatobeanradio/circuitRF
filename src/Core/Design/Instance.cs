namespace CircuitRF.Core.Design;

/// <summary>
/// A placed instance inside a Cell — either a primitive component or a sub-cell.
/// NetBindings connect this instance's ports (in order) to net names in the parent cell.
/// Overrides are evaluated in the PARENT scope.
/// </summary>
public sealed class Instance
{
    /// <summary>Stable GUI identity (Phase 6 only; not serialized to .cnl).</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Instance name: "X1", "R1", …</summary>
    public string InstanceName { get; }

    /// <summary>
    /// The type string for a primitive ("R", "C", "L", "Port", …)
    /// or the Cell name for a sub-cell instance.
    /// </summary>
    public string Reference { get; }

    /// <summary>Ordered net names in the parent cell, matching the referenced type's port order.</summary>
    public IReadOnlyList<string> NetBindings { get; }

    /// <summary>Parameter overrides; each expression is evaluated in the PARENT scope.</summary>
    public IReadOnlyList<ParameterAssignment> Overrides { get; }

    public Instance(
        string instanceName,
        string reference,
        IEnumerable<string> netBindings,
        IEnumerable<ParameterAssignment>? overrides = null)
    {
        InstanceName = instanceName;
        Reference    = reference;
        NetBindings  = [.. netBindings];
        Overrides    = overrides is null ? [] : [.. overrides];
    }
}
