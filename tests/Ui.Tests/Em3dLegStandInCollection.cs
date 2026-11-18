using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The classes that set <c>Em3dRunService.LegStandIn</c>, a process-wide static: one class's stand-in replacing another's
/// between its set and its run would hand that run a real solver. They run one after another; each filters on its own root.
/// </summary>
[CollectionDefinition(Name)]
public sealed class Em3dLegStandInCollection
{
    public const string Name = "Em3d leg stand-in";
}
