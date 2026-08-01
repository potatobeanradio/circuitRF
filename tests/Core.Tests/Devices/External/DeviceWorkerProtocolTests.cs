using System;
using System.IO;
using System.Linq;
using CircuitRF.Core.Devices.External;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.External;

/// <summary>
/// Frame codec for the out-of-process device-model worker. The failures worth testing here are the
/// ones that only appear under load or on a corrupt stream: partial pipe reads, and a desynchronised
/// length being believed.
/// </summary>
public sealed class DeviceWorkerProtocolTests
{
    private static DeviceWorkerFrame RoundTrip(DeviceWorkerFrame frame)
    {
        var ms = new MemoryStream();
        DeviceWorkerProtocol.WriteFrame(ms, frame);
        ms.Position = 0;
        return DeviceWorkerProtocol.ReadFrame(ms);
    }

    [Fact]
    public void AControlOnlyFrame_RoundTrips()
    {
        var back = RoundTrip(new DeviceWorkerFrame("""{"cmd":"describe"}"""));

        Assert.Equal("""{"cmd":"describe"}""", back.Json);
        Assert.Equal(0, back.Payload.Length);
    }

    [Fact]
    public void AFrameCarryingNumerics_RoundTripsBitExactly()
    {
        // Bias vectors and Jacobians go over this path; a lossy transport would be a silent
        // accuracy bug rather than a failure.
        double[] values = [0.0, -1.0, 2.9829, 1e-15, -166.6, double.Epsilon, 7.15e-12];

        var back = RoundTrip(new DeviceWorkerFrame("""{"cmd":"eval","count":1}""", values));

        Assert.Equal(values, back.Payload.ToArray());
    }

    [Fact]
    public void ALargeBatch_SurvivesIntact()
    {
        // Batching is the whole reason the payload is raw doubles — ~24x at 2000 points.
        double[] values = Enumerable.Range(0, 2000 * 6).Select(i => i * 0.5).ToArray();

        var back = RoundTrip(new DeviceWorkerFrame("""{"cmd":"eval","count":2000}""", values));

        Assert.Equal(values.Length, back.Payload.Length);
        Assert.Equal(values[^1], back.Payload.Span[^1], 12);
    }

    [Fact]
    public void NonAsciiControlText_SurvivesAsUtf8()
    {
        var back = RoundTrip(new DeviceWorkerFrame("""{"note":"Ω µm ±"}"""));

        Assert.Equal("""{"note":"Ω µm ±"}""", back.Json);
    }

    [Fact]
    public void SeveralFramesOnOneStream_ReadBackInOrder()
    {
        var ms = new MemoryStream();
        DeviceWorkerProtocol.WriteFrame(ms, new DeviceWorkerFrame("""{"cmd":"create"}"""));
        DeviceWorkerProtocol.WriteFrame(ms, new DeviceWorkerFrame("""{"cmd":"eval"}""", new double[] { 1, 2 }));
        DeviceWorkerProtocol.WriteFrame(ms, new DeviceWorkerFrame("""{"cmd":"shutdown"}"""));
        ms.Position = 0;

        Assert.Equal("""{"cmd":"create"}""",   DeviceWorkerProtocol.ReadFrame(ms).Json);
        Assert.Equal(2,                         DeviceWorkerProtocol.ReadFrame(ms).Payload.Length);
        Assert.Equal("""{"cmd":"shutdown"}""", DeviceWorkerProtocol.ReadFrame(ms).Json);
    }

    // ── Partial reads ─────────────────────────────────────────────────────────

    /// <summary>A pipe that hands back one byte at a time — the shape a real pipe takes under load.</summary>
    private sealed class DribbleStream(byte[] data) : Stream
    {
        private int _pos;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= data.Length || count == 0) return 0;
            buffer[offset] = data[_pos++];
            return 1;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    [Fact]
    public void APartialRead_IsLoopedRatherThanTreatedAsTheEnd()
    {
        // A short read is normal on a pipe. Treating one as end-of-stream produces frames that
        // decode as garbage only under load — the worst kind of bug to chase.
        double[] values = [1.5, 2.5, 3.5];
        var ms = new MemoryStream();
        DeviceWorkerProtocol.WriteFrame(ms, new DeviceWorkerFrame("""{"cmd":"eval"}""", values));

        var back = DeviceWorkerProtocol.ReadFrame(new DribbleStream(ms.ToArray()));

        Assert.Equal("""{"cmd":"eval"}""", back.Json);
        Assert.Equal(values, back.Payload.ToArray());
    }

    // ── Desynchronisation and truncation ──────────────────────────────────────

    [Fact]
    public void AClosedStream_SaysTheWorkerStoppedAnswering()
    {
        var ex = Assert.Throws<ExternalDeviceException>(
            () => DeviceWorkerProtocol.ReadFrame(new MemoryStream()));

        Assert.Contains("closed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ATruncatedFrame_IsReportedRatherThanReturnedShort()
    {
        var ms = new MemoryStream();
        DeviceWorkerProtocol.WriteFrame(ms, new DeviceWorkerFrame("""{"cmd":"eval"}""", new double[] { 1, 2, 3 }));
        var bytes = ms.ToArray()[..^8];          // lose the last value

        var ex = Assert.Throws<ExternalDeviceException>(
            () => DeviceWorkerProtocol.ReadFrame(new MemoryStream(bytes)));

        Assert.Contains("truncated", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnImplausibleLength_IsCalledADesync_NotHonouredWithAnAllocation()
    {
        // Believing a corrupt length means allocating gigabytes instead of reporting the desync.
        byte[] header = [0x10, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF];

        var ex = Assert.Throws<ExternalDeviceException>(
            () => DeviceWorkerProtocol.ReadFrame(new MemoryStream(header)));

        Assert.Contains("out of step", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void APayloadLengthThatIsNotAWholeNumberOfValues_IsRejected()
    {
        byte[] header = [0x02, 0, 0, 0, 0x05, 0, 0, 0];   // 5 bytes is not a whole double

        var ex = Assert.Throws<ExternalDeviceException>(
            () => DeviceWorkerProtocol.ReadFrame(new MemoryStream(header)));

        Assert.Contains("whole number", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AFrameWithNoControlObject_IsRejectedInBothDirections()
    {
        Assert.Throws<ArgumentException>(
            () => DeviceWorkerProtocol.WriteFrame(new MemoryStream(), new DeviceWorkerFrame("")));

        byte[] header = [0, 0, 0, 0, 0, 0, 0, 0];
        Assert.Throws<ExternalDeviceException>(
            () => DeviceWorkerProtocol.ReadFrame(new MemoryStream(header)));
    }
}
