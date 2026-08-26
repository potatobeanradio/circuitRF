using System.Reflection;

namespace CircuitRF.Ui.Updates;

/// <summary>
/// Which of the three applications is running — <c>circuitRF</c>, <c>harmonicaRF</c> or <c>wBond</c>.
///
/// <para>Read from the assembly's <c>Product</c>, which <c>CircuitRF.Ui.csproj</c> already sets per
/// <c>CrfApp</c>. Three applications share one assembly, so the shared updater cannot infer which one
/// it is from the assembly name (always <c>CircuitRF.Ui</c>) — and it must, because a shared updater
/// that offered circuitRF's 160 MB payload to wBond would install the wrong application.</para>
///
/// <para>No name is written down here for the same reason no version is: <c>Product</c> is the single
/// source and the packaging scripts read the same three spellings.</para>
/// </summary>
public static class UpdateApp
{
    /// <summary>e.g. <c>circuitRF</c>. Never empty.</summary>
    public static string Name { get; } = Read();

    private static string Read()
    {
        string? p = typeof(UpdateApp).Assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product;
        return string.IsNullOrWhiteSpace(p) ? "circuitRF" : p.Trim();
    }
}
