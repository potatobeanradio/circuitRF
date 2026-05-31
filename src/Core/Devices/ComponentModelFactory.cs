namespace CircuitRF.Core.Devices;

/// <summary>
/// Looks up a primitive type name and returns a ComponentModel instance.
/// Only primitive type strings appear here; sub-cell instances are resolved
/// by the Elaborator directly from the Library.
/// </summary>
public static class ComponentModelFactory
{
    private static readonly Dictionary<string, Func<ComponentModel>> _registry =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { "R",    () => new ResistorModel()  },
            { "C",    () => new CapacitorModel() },
            { "L",    () => new InductorModel()  },
            { "Port", () => new PortModel()      },
            { "Term", () => new TermModel()      },
        };

    /// <summary>
    /// Returns a new ComponentModel for the given primitive type name,
    /// or null if the name is not a registered primitive (likely a sub-cell reference).
    /// </summary>
    public static ComponentModel? TryCreate(string typeName)
        => _registry.TryGetValue(typeName, out var factory) ? factory() : null;

    public static bool IsPrimitive(string typeName)
        => _registry.ContainsKey(typeName);

    /// <summary>Register additional primitive types (used by tests and future phases).</summary>
    public static void Register(string typeName, Func<ComponentModel> factory)
        => _registry[typeName] = factory;
}
