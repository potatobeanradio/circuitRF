// ================================================================
//  Program.cs — the process entry point, and nothing else.
//
//  Everything that WAS here is in CliEntry.cs, unchanged. It moved because a local function of a
//  top-level program is private to `<Main>$` and callable by nobody, and `circuitrf serve` has to
//  call the verbs rather than re-implement them (R-aut-13). CliEntry.cs's header records the rest.
// ================================================================

// A link-driver launcher first (brief vendorlib-18): see src/Ui/Program.cs, which does the same.
if (CircuitRF.Core.Devices.External.VerilogALinkDriver.IsInvoked)
    return CircuitRF.Core.Devices.External.VerilogALinkDriver.Main(args);
CircuitRF.Core.Devices.External.VerilogALinkDriver.Host =
    CircuitRF.Core.Devices.External.VerilogALinkDriver.HostOf(Environment.ProcessPath, AppContext.BaseDirectory, "CircuitRF.Cli");

return CircuitRF.Cli.CliEntry.Run(args);
