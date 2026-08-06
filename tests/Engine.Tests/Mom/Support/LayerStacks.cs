using CircuitRF.Engine.Mom;

namespace CircuitRF.Engine.Tests.Mom.Support;

/// <summary>
/// <b>D8 — the multilayer stacks the L9a oracles run on are HAND-BUILT HERE, in the engine tests,
/// not shipped as a starter technology.</b> Both shipping starters are single-substrate; L9's own
/// phase gate will eventually need a multilayer one, but adding a starter technology is a
/// <c>src/Ui</c> change with <c>.ctech</c> consequences and it is L9d/L9e's. This follows the
/// <c>EmProblemBuilders</c> precedent: a handful of stacks in SI units with every layer parameter
/// written out, so a reader can check the physics without opening another file.
/// </summary>
public static class LayerStacks
{
    // --- the two shipping starters, re-expressed as one-layer stacks (D5's bridge) ---

    /// <summary>1.6 mm FR-4, εᵣ = 4.4, tanδ = 0.02, over a ground plane, air above.</summary>
    public static LayerStack Fr4Slab => LayerStack.FromGroundedSlab(GroundedSlab.Fr4Starter);

    /// <summary>100 µm GaAs, εᵣ = 12.9, tanδ = 0.002, over a ground plane, air above.</summary>
    public static LayerStack GaAsSlab => LayerStack.FromGroundedSlab(GroundedSlab.GaAsStarter);

    // --- the genuinely multilayer stacks ---

    /// <summary>
    /// A three-dielectric PCB build, bottom to top, on a ground plane with air above:
    /// <list type="bullet">
    ///   <item>0.80 mm FR-4 core,          εᵣ = 4.40, tanδ = 0.0200</item>
    ///   <item>0.50 mm low-loss laminate,  εᵣ = 3.48, tanδ = 0.0037</item>
    ///   <item>0.10 mm prepreg,            εᵣ = 3.90, tanδ = 0.0120</item>
    /// </list>
    /// Chosen so no two adjacent layers share a permittivity (every interior interface has a real
    /// reflection to get wrong) and so the total 1.40 mm is comparable to the FR-4 starter, which
    /// keeps the mode count and the ρ/λ range in the same regime the L8 measurements were taken in.
    /// </summary>
    public static LayerStack Pcb3Layer => new(
        Termination.Pec,
        [
            new MediumLayer(0.80e-3, new EmMaterial(4.40, 0.0200)),
            new MediumLayer(0.50e-3, new EmMaterial(3.48, 0.0037)),
            new MediumLayer(0.10e-3, new EmMaterial(3.90, 0.0120)),
        ],
        Termination.Air);

    /// <summary>
    /// The MMIC two-metal-level shape L9c will actually need: 100 µm GaAs on a backside ground,
    /// with a 3 µm dielectric spacer on top of it. Two metal levels would sit at z = 100 µm and
    /// z = 103 µm — the interior interface and the top surface.
    /// <list type="bullet">
    ///   <item>100 µm GaAs,  εᵣ = 12.90, tanδ = 0.0020</item>
    ///   <item>  3 µm spacer, εᵣ =  2.70, tanδ = 0.0020</item>
    /// </list>
    /// </summary>
    public static LayerStack MmicTwoLevel => new(
        Termination.Pec,
        [
            new MediumLayer(100e-6, new EmMaterial(12.90, 0.0020)),
            new MediumLayer(  3e-6, new EmMaterial( 2.70, 0.0020)),
        ],
        Termination.Air);

    /// <summary>
    /// <b>D2's second branch point, made concrete.</b> An UNGROUNDED slab: air below, 0.5 mm
    /// alumina (εᵣ = 9.8, tanδ = 0.001), air above. The bottom half-space contributes its own
    /// <c>k_b</c> to the spectrum, so this stack has two branch points rather than one — the fact
    /// L9b has to measure DCIM against, and the reason
    /// <c>PlanarExtractor</c>'s ungrounded-stack refusal stays in place until it does.
    /// </summary>
    public static LayerStack OpenBelow => new(
        Termination.Air,
        [new MediumLayer(0.5e-3, new EmMaterial(9.80, 0.0010))],
        Termination.Air);

    /// <summary>
    /// <b>L9b's D3 — the second branch point where it is not degenerate.</b>
    ///
    /// <para><see cref="OpenBelow"/> is alumina in AIR, so <c>k_b = k₀ exactly</c> and the two branch
    /// points coincide: it cannot separate "DCIM handles a second cut" from "there is only one cut".
    /// A thin film on a semi-infinite silicon substrate is the honest shape, and is the one L9b
    /// concludes from:</para>
    /// <list type="bullet">
    ///   <item>bottom half-space: high-resistivity silicon, εᵣ = 11.90, tanδ = 0.0050</item>
    ///   <item>4 µm thermal oxide,                          εᵣ =  4.10, tanδ = 0.0010</item>
    ///   <item>top half-space: air</item>
    /// </list>
    /// <para>The bottom half-space is DENSER than the top, so the second branch point
    /// <c>k_z0² = k_top² − k_b²</c> sits on the <b>negative-imaginary k_z0 axis</b>, at
    /// <c>−j k₀√(11.9 − 1) = −3.30 j k₀</c> — inside the half-plane the DCIM sampling path runs into,
    /// which is what makes it a question about the BASIS rather than about accuracy.</para>
    ///
    /// <para><b>It also carries no surface wave at all, and that is a property of the shape rather
    /// than an oversight:</b> a guided mode must be evanescent in every open termination, and no
    /// layer here (εᵣ = 4.1) is slower than the silicon below it (εᵣ = 11.9). Energy leaks downward
    /// instead — so the far field is carried entirely by the lateral wave the second cut represents,
    /// with no pole term to hide behind.</para>
    /// </summary>
    public static LayerStack FilmOnSilicon => new(
        Termination.OpenTo(new EmMaterial(11.90, 0.0050)),
        [new MediumLayer(4e-6, new EmMaterial(4.10, 0.0010))],
        Termination.Air);

    /// <summary>
    /// A degenerate control: εᵣ = 1 everywhere over a ground plane. The exact answer is free space
    /// plus ONE negative image at depth 2H, for both kernels — the direct analogue of L8a's Tier 1
    /// reduction, and the check no plausible-but-wrong cascade survives. Built with THREE layers so
    /// the interior interfaces are exercised even though they are physically invisible.
    /// </summary>
    public static LayerStack AirOverGround(double totalM = 1.6e-3) => new(
        Termination.Pec,
        [
            new MediumLayer(totalM * 0.5, EmMaterial.Air),
            new MediumLayer(totalM * 0.2, EmMaterial.Air),
            new MediumLayer(totalM * 0.3, EmMaterial.Air),
        ],
        Termination.Air);

    /// <summary>Every stack above, named, for the sweeps that report per-stack numbers.</summary>
    public static IEnumerable<(string Name, LayerStack Stack)> All()
    {
        yield return ("FR-4 slab   1.6 mm  εᵣ=4.4",                      Fr4Slab);
        yield return ("GaAs slab   100 µm  εᵣ=12.9",                     GaAsSlab);
        yield return ("PCB 3-layer 0.8/0.5/0.1 mm  εᵣ=4.4/3.48/3.9",     Pcb3Layer);
        yield return ("MMIC 2-level 100/3 µm  εᵣ=12.9/2.7",              MmicTwoLevel);
        yield return ("Alumina, OPEN below  0.5 mm  εᵣ=9.8",             OpenBelow);
        yield return ("Oxide on SILICON  4 µm εᵣ=4.1 / εᵣ=11.9 below",   FilmOnSilicon);
    }
}
