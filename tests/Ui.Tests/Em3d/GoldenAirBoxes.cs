using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Ui.Tests.Em3d;

/// <summary>
/// The air box every generator and wire dump states, by case name. A dump is a record of what the generator did with
/// one problem, and the default padding is a policy that has changed several times; a dump that took the default was
/// rewritten every time it did, though nothing it records had changed. So each case states the box its committed dump
/// was written with, face by face, as a percentage of the content's extent where that reproduces the dump to the last
/// digit and in micrometres otherwise. A floor on a ground plane is left unstated: it comes from the geometry, and
/// stating the face would take it off the plane. A face the case's own setup states is never overridden.
/// </summary>
internal static class GoldenAirBoxes
{
    public static EmAirBox? For(string name) => Boxes.GetValueOrDefault(name);

    /// <summary>Each face <paramref name="own"/> states, else <paramref name="golden"/>'s.</summary>
    public static EmAirBox? Merge(EmAirBox? own, EmAirBox? golden)
        => golden is null ? own : own is null ? golden : new EmAirBox(
            own.XMin ?? golden.XMin, own.XMax ?? golden.XMax, own.YMin ?? golden.YMin,
            own.YMax ?? golden.YMax, own.ZMin ?? golden.ZMin, own.ZMax ?? golden.ZMax);

    private static EmAirBoxFace Um(double um) => new(um, null);
    private static EmAirBoxFace Pct(double percent) => new(null, null, percent);

    private static readonly Dictionary<string, EmAirBox> Boxes = new(StringComparer.Ordinal)
    {
        ["asymmetric array foot"] = new(XMin: Pct(100), XMax: Pct(100), YMin: Pct(1100), YMax: Pct(1100), ZMax: Pct(419.601912924886)),
        ["asymmetric overhang"] = new(XMin: Pct(100), XMax: Pct(100), YMin: Um(1340.6346146436058), YMax: Um(1340.6346146436058), ZMax: Um(1340.6346146436058)),
        ["ball end round"] = new(XMin: Pct(100), XMax: Pct(100), YMin: Pct(1100), YMax: Pct(1100), ZMax: Um(1100.0000000000002)),
        ["ball start vertical=False"] = new(XMin: Pct(100), XMax: Pct(100), YMin: Pct(1100), YMax: Pct(1100), ZMax: Um(1100.0000000000002)),
        ["ball start vertical=True"] = new(XMin: Pct(100), XMax: Pct(100), YMin: Pct(1100), YMax: Pct(1100), ZMax: Pct(401.02950788683813)),
        ["cem examples/3D EM/Via through a plane/em/Via planar.cem"] = new(XMin: Pct(40), XMax: Pct(40), YMin: Pct(80), YMax: Pct(80), ZMin: Pct(395), ZMax: Pct(790)),
        ["cem examples/Klopfenstein Taper/Taper/em/Taper-MoM.cem"] = new(XMin: Pct(100), XMax: Pct(100), YMin: Um(15000.000000000002), YMax: Um(15000.000000000002), ZMax: Um(15000.000000000004)),
        ["cem examples/PDK PCells/SpiralInductor/em/SpiralInductor.cem"] = new(XMin: Pct(20), XMax: Pct(20), YMin: Pct(25), YMax: Pct(25), ZMax: Pct(55.00000000000003)),
        ["cem examples/PDK PCells/SpiralResonator/em/SpiralResonator.cem"] = new(XMin: Pct(15), XMax: Pct(15), YMin: Pct(20), YMax: Pct(20), ZMax: Um(54.5)),
        ["cem examples/Patch Antenna/patch/em/patch-5p8GHz.cem"] = new(XMin: Um(14141.153679245284), XMax: Um(14141.15367924528), YMin: Um(14141.15367924528), YMax: Um(14141.15367924528), ZMin: Um(14141.153679245284), ZMax: Um(14141.153679245284)),
        ["cem testdata/antenna/patch/em/patch-5p8GHz.cem"] = new(XMin: Um(14141.153679245284), XMax: Um(14141.15367924528), YMin: Um(14141.15367924528), YMax: Um(14141.15367924528), ZMin: Um(14141.153679245284), ZMax: Um(14141.153679245284)),
        ["cem testdata/em3d/f0/B-via/planar/ws-meshed-plane/via/em/via.cem"] = new(XMin: Pct(40), XMax: Pct(40), YMin: Pct(80), YMax: Pct(80), ZMin: Pct(395), ZMax: Pct(790)),
        ["cem testdata/em3d/f0/B-via/planar/ws/via/em/via-both-levels.cem"] = new(XMin: Pct(40), XMax: Pct(40), YMin: Pct(80), YMax: Pct(80), ZMin: Pct(395), ZMax: Pct(790)),
        ["cem testdata/em3d/f0/B-via/planar/ws/via/em/via.cem"] = new(XMin: Pct(40), XMax: Pct(40), YMin: Pct(80), YMax: Pct(80), ZMin: Pct(395), ZMax: Pct(790)),
        ["cem testdata/portcal/coupled-asym/em/coupled-asym.cem"] = new(XMin: Pct(100), XMax: Pct(100), YMin: Um(3830.0000000000005), YMax: Um(3830.0000000000005), ZMax: Um(3830.0000000000005)),
        ["cem testdata/portcal/coupled-pair-passive/em/coupled-pair-passive.cem"] = new(XMin: Pct(100), XMax: Pct(100), YMin: Um(3830.0000000000005), YMax: Um(3830.0000000000005), ZMax: Um(3830.0000000000005)),
        ["cem testdata/portcal/coupled-pair/em/coupled-pair.cem"] = new(XMin: Pct(100), XMax: Pct(100), YMin: Um(3830.0000000000005), YMax: Um(3830.0000000000005), ZMax: Um(3830.0000000000005)),
        ["cem testdata/portcal/coupled-triple/em/coupled-triple.cem"] = new(XMin: Pct(100), XMax: Pct(100), YMin: Um(3830.0000000000005), YMax: Um(3830.0000000000005), ZMax: Um(3830.0000000000005)),
        ["cem testdata/portcal/offset-pair/em/offset-pair.cem"] = new(XMin: Pct(100), XMax: Pct(100), YMin: Um(3830.0000000000005), YMax: Um(3830.0000000000005), ZMax: Um(3830.0000000000005)),
        ["cem testdata/portcal/pad-coupled-pair/em/pad-coupled-pair.cem"] = new(XMin: Pct(100), XMax: Pct(100), YMin: Um(4388.8), YMax: Um(4388.8), ZMax: Um(4388.8)),
        ["cem testdata/portcal/separated-pair/em/separated-pair.cem"] = new(XMin: Pct(100), XMax: Pct(100), YMin: Um(11830), YMax: Um(11830), ZMax: Um(11830)),
        ["f0 case A"] = new(XMin: Pct(100), XMax: Pct(100), YMin: Pct(1100), YMax: Pct(1100), ZMax: Um(1100.0000000000002)),
        ["f0 case A round"] = new(XMin: Pct(100), XMax: Pct(100), YMin: Pct(1100), YMax: Pct(1100), ZMax: Pct(399.4190268700072)),
        ["fixture caseB-plated"] = new(XMin: Pct(40), XMax: Pct(40), YMin: Pct(80), YMax: Pct(80), ZMin: Pct(395), ZMax: Pct(790)),
        ["fixture caseB-solid"] = new(XMin: Pct(40), XMax: Pct(40), YMin: Pct(80), YMax: Pct(80), ZMin: Pct(395), ZMax: Pct(790)),
        ["fixture microstrip"] = new(XMin: Pct(100), XMax: Pct(100), YMin: Um(10000), YMax: Um(10000), ZMax: Um(10000)),
        ["fixture microstrip-Eigenmode"] = new(XMin: Pct(100), XMax: Pct(100), YMin: Um(10000), YMax: Um(10000), ZMax: Um(10000)),
        ["plan dogleg"] = new(XMin: Pct(100), XMax: Pct(100), YMin: Pct(300.67403140921823), YMax: Um(1100.0000000000002), ZMax: Pct(352.1595155105524)),
        ["through vertical"] = new(XMin: Pct(100), XMax: Pct(100), YMin: Pct(1100), YMax: Pct(1100), ZMax: Pct(179.32235661682307)),
        ["two arrays"] = new(XMin: Pct(100), XMax: Pct(100), YMin: Pct(1100), YMax: Pct(1100), ZMax: Pct(400.31015854999333)),
    };
}
