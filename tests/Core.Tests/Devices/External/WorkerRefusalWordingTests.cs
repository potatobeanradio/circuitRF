using System;
using System.IO;
using CircuitRF.Core.Devices.External;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.External;

/// <summary>
/// A worker's own account of why it refused must reach the user.
///
/// <para><b>The failure this pins, and it was live for every OSDI refusal.</b> The channel treated a
/// reply as a refusal only when it carried <c>"ok": false</c>. The compiled-model worker circuitRF
/// ships writes refusals as a bare <c>{"error": "…"}</c> — so every one of them was read as a
/// perfectly normal reply that simply carried no data, and the caller then reported whatever it made
/// of the ABSENCE. Measured: <i>"create: too many live instances"</i> reached the user as
/// <i>"created 'MDLA_VA' but did not say which instance it created"</i>, which names neither the
/// cause nor anything to act on, and sends the reader looking at the wrong half of the system.</para>
///
/// <para>There is no reply in this protocol where an <c>error</c> member means anything else, which
/// is what makes the member sufficient on its own.</para>
/// </summary>
public sealed class WorkerRefusalWordingTests
{
    /// <summary>A channel whose worker will answer with exactly this JSON, once.</summary>
    private static DeviceWorkerChannel ChannelAnswering(string json)
    {
        var replies = new MemoryStream();
        DeviceWorkerProtocol.WriteFrame(replies, new DeviceWorkerFrame(json));
        replies.Position = 0;

        return new DeviceWorkerChannel(
            new StreamDeviceWorkerTransport(new MemoryStream(), replies, "test worker"));
    }

    private static ExternalDeviceException Refusal(string json)
    {
        using var channel = ChannelAnswering(json);
        return Assert.Throws<ExternalDeviceException>(
            () => channel.Send(w => w.WriteString("cmd", "create")).Dispose());
    }

    [Fact]
    public void ABareErrorMember_IsARefusal_AndItsOwnWordsAreWhatSurfaces()
        => Assert.Contains("too many live instances",
                           Refusal("""{"error":"create: too many live instances"}""").Message,
                           StringComparison.Ordinal);

    /// <summary>The shape the other workers use still refuses, exactly as before.</summary>
    [Fact]
    public void TheOkFalseShape_StillRefuses()
        => Assert.Contains("no such device type",
                           Refusal("""{"ok":false,"error":"no such device type"}""").Message,
                           StringComparison.Ordinal);

    /// <summary>
    /// An ordinary reply is NOT a refusal — the guard has to distinguish them on the member's
    /// presence rather than on the reply being unfamiliar, or every successful command would throw.
    /// </summary>
    [Fact]
    public void AnOrdinaryReply_IsNotRefused()
    {
        using var channel = ChannelAnswering("""{"handle":0,"pinCount":4}""");
        using var reply   = channel.Send(w => w.WriteString("cmd", "create"));

        Assert.Equal(0, reply.Root.GetProperty("handle").GetInt32());
    }
}
