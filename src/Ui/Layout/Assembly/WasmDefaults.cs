using CircuitRF.WBond;

namespace CircuitRF.Ui.Layout.Assembly;

/// <summary>
/// The starter assembly rule set — what "create one for me" writes.
///
/// <para><b>A new workspace deliberately contains no `.wasm` at all.</b> Most designs have no
/// wirebonds, and shipping a rule file into every workspace would put a document in the project tree
/// that most users would have to learn about only to ignore. The file is created the first time a
/// check actually needs one, when the user is already looking at the question — see
/// <c>WorkspaceViewModel.PromptForAssemblyRulesAsync</c>.</para>
///
/// <para><b>These are plausible starting values, not any real house's rules, and the file says so in
/// every rule's own description.</b> That matters: a rule set a user believes came from their
/// assembly house, but did not, is worse than no rule set — it would pass a design the house rejects.
/// The starter exists so someone has a working file to EDIT against their own house's document, and
/// the numbers are conventional first-pass gold-wire values chosen to be obviously placeholder.</para>
/// </summary>
public static class WasmDefaults
{
    /// <summary>The conventional file name a workspace's own default rule set is written to.</summary>
    public const string DefaultFileName = "default" + WasmPersistence.Extension;

    private static long Mil(double v) => WBondUnits.ToNm(v, WBondUnit.Mil);

    /// <summary>
    /// Builds a starter rule set covering the parts of §8's table this language can express.
    /// </summary>
    /// <param name="name">The house name shown wherever a check reports which rules ran.</param>
    public static WasmFile CreateStarter(string name = "Default assembly rules")
    {
        const string placeholder =
            "PLACEHOLDER — replace with your assembly house's own stated value.";

        return new WasmFile
        {
            Name = name,

            Machine =
            [
                new WasmRule
                {
                    Name        = "Machine: minimum wire pitch",
                    Expression  = "foot_pitch(all) >= 3mil",
                    Description = "Closest the bonder can place two feet. " + placeholder,
                },
                new WasmRule
                {
                    Name        = "Machine: minimum wire-to-wire clearance",
                    Expression  = "wire_spacing(all) >= 2mil",
                    Description = "Closest two wires may pass in space. " + placeholder,
                },
                new WasmRule
                {
                    Name        = "Machine: maximum wire angle change",
                    Expression  = "angle_change(all) <= 90deg",
                    Description = "Sharpest turn the loop former will produce. " + placeholder,
                },
            ],

            Process =
            [
                new WasmRule
                {
                    Name        = "Process: loop height vs span",
                    Expression  = "loop_height(all) <= envelope(max_loop_height, span(all))",
                    Description = "Loop height this house will run at a given span. " + placeholder,
                },
                new WasmRule
                {
                    Name        = "Process: minimum wire span",
                    Expression  = "span(all) >= 10mil",
                    Description = "Shortest wire this house will bond. " + placeholder,
                    Severity    = DrcSeverity.Warning,
                },
                new WasmRule
                {
                    Name        = "Process: maximum wire span",
                    Expression  = "span(all) <= 200mil",
                    Description = "Longest wire this house will bond unsupported. " + placeholder,
                },
            ],

            // The material section's own lists are checked structurally rather than through the
            // expression language — an allowed-value list is a set membership test.
            Material =
            [
                new WasmRule
                {
                    Name        = "Material: wire-to-pad-edge clearance",
                    Expression  = "wire_to_layer(all, 1/0) >= 1mil",
                    Description = "Clearance from a wire to artwork on layer 1/0. " +
                                  "Point this at your own bond-pad layer. " + placeholder,
                    Severity    = DrcSeverity.Warning,
                },
            ],

            AllowedDiametersNm = [Mil(0.7), Mil(1.0), Mil(1.25), Mil(2.0)],
            AllowedMetals      = ["Gold", "Aluminium"],

            Envelopes =
            [
                new WasmEnvelope
                {
                    Name = "max_loop_height",
                    Points =
                    [
                        new(Mil(10),  Mil(6)),
                        new(Mil(40),  Mil(12)),
                        new(Mil(100), Mil(22)),
                        new(Mil(200), Mil(35)),
                    ],
                },
            ],
        };
    }
}
