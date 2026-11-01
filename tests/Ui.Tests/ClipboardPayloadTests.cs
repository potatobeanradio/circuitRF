using System.Text;
using Avalonia.Input;
using CircuitRF.Ui.Clipboard;
using CircuitRF.Ui.DataDisplay;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// A circuitRF copy carries its own document in <see cref="ClipboardPayload"/>, never as plain text: a chat
/// client takes text whenever it is offered, so a plot pasted into one arrived as its JSON instead of as the
/// picture beside it. circuitRF reads the payload first and plain text after (<see cref="ClipboardRead.TextAsync"/>).
/// </summary>
public class ClipboardPayloadTests
{
    private const string Json = "{\"plots\":[{\"title\":\"S21 — gain\"}]}";

    [Fact]
    public void APlotCopy_OffersNoPlainText_ButCarriesItsJsonAsThePayload()
    {
        using var transfer = PlotExporter.BuildClipboardTransferCore([1, 2, 3], "<svg/>", Json);

        Assert.DoesNotContain(DataFormat.Text, transfer.Formats);
        Assert.Equal(Json, ClipboardPayload.Decode(transfer.TryGetValue(ClipboardPayload.Format)));
    }

    [Fact]
    public void Decode_StopsAtTheTerminator_IgnoringAnAllocationsPadding()
    {
        // Windows returns an HGLOBAL's whole allocation, which can be rounded up past what was written.
        byte[] written = ClipboardPayload.Encode(Json);
        byte[] padded  = new byte[written.Length + 13];
        written.CopyTo(padded, 0);
        Encoding.ASCII.GetBytes("garbage").CopyTo(padded, written.Length);

        Assert.Equal(Json, ClipboardPayload.Decode(padded));
    }
}
