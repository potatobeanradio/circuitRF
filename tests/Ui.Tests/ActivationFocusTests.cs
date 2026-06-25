// ================================================================
//  ActivationFocusTests.cs — IActivatableDocument focus-request contract
//
//  The workspace calls RequestActivationFocus on tab activation; the editor
//  view focuses its canvas via the event (already-bound) or by consuming the
//  pending flag (binds later, on first open). The focus itself can't be tested
//  headlessly, but the request/consume contract can.
// ================================================================

using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class ActivationFocusTests
{
    [Fact]
    public void RequestActivationFocus_RaisesEvent_AndConsumeClearsPending()
    {
        IActivatableDocument doc =
            new SchematicDocument("TB", new SchematicViewModel(new SchematicEditModel()));

        bool raised = false;
        doc.ActivationFocusRequested += () => raised = true;

        Assert.False(doc.ConsumeActivationFocus());   // nothing pending initially

        doc.RequestActivationFocus();
        Assert.True(raised);                          // event fired → an already-bound view focuses
        Assert.True(doc.ConsumeActivationFocus());    // flag set → a view binding later (first open) focuses
        Assert.False(doc.ConsumeActivationFocus());   // consumed → cleared (no stale re-focus)
    }
}
