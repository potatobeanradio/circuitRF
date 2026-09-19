using CircuitRF.Ui.Commands;

namespace CircuitRF.Ui.Smith;

/// <summary>
/// One coarse-grained undo entry for a <c>.csmith</c>: the whole <see cref="CircuitRF.Design.Smith.SmithDesign"/>,
/// before and after one committed edit, each captured through
/// <see cref="CircuitRF.Design.Smith.SmithDesignIo.SerializeUnvalidated"/>.
/// </summary>
/// <remarks>
/// <b>Deliberately the same shape as <c>EmSetupSnapshotCommand</c> and <c>TechSnapshotCommand</c>, and
/// for the same reason they exist.</b> A Smith design is a generator table and a short cascade, so its
/// own serializer doubles as an exact deep clone and there is nothing to gain from a per-field command
/// per settable value — of which this window has, by <c>R-smith4-5</c>, rather a lot.
///
/// <para><b>What it buys that a per-field command would not.</b> <c>R-smith4-7</c>'s Conjugate negates
/// every row's reactance and must be <i>one</i> entry a single Undo unwinds completely; an import
/// REPLACES the whole table and records a path. Both are one snapshot pair here, with no composite to
/// build and nothing to get wrong about grouping — which is the standing rule the series states as "one
/// gesture is one undo entry".</para>
///
/// <para><b>The unvalidated serializer is the right one.</b> A half-built design is an ordinary state of
/// a live editor: a generator table being typed into, an element not yet named. A snapshot that refused
/// to capture it would make Undo unavailable exactly while the user is most likely to want it.</para>
/// </remarks>
internal sealed class SmithSnapshotCommand : IUiCommand
{
    private readonly SmithChartViewModel _owner;
    private readonly string _beforeJson;
    private readonly string _afterJson;

    public string Description { get; }

    public SmithSnapshotCommand(SmithChartViewModel owner, string beforeJson, string afterJson,
                                string description)
    {
        _owner      = owner;
        _beforeJson = beforeJson;
        _afterJson  = afterJson;
        Description = description;
    }

    public void Execute() => _owner.ApplySnapshot(_afterJson);
    public void Undo()    => _owner.ApplySnapshot(_beforeJson);
}
