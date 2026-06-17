using System.IO;
using Xunit;

namespace RfCore.Tests;

public class TryGetPortCountTests
{
    private static string TestData => Path.Combine(
        Path.GetDirectoryName(typeof(TryGetPortCountTests).Assembly.Location)!,
        "testdata");

    [Fact]
    public void S2p_FromExtension_Returns2()
    {
        string path = Path.Combine(TestData, "2SC5226A.s2p");
        bool ok = TouchstoneIO.TryGetPortCount(path, out int ports, out _);
        Assert.True(ok);
        Assert.Equal(2, ports);
    }

    [Fact]
    public void S5p_FromExtension_Returns5()
    {
        // bad_file_extension.s5p has a bad header but extension says 5 — extension wins.
        string path = Path.Combine(TestData, "bad_file_extension.s5p");
        bool ok = TouchstoneIO.TryGetPortCount(path, out int ports, out _);
        Assert.True(ok);
        Assert.Equal(5, ports);
    }

    [Fact]
    public void MissingFile_ReturnsFalseWithError()
    {
        bool ok = TouchstoneIO.TryGetPortCount("/nonexistent/path.s2p", out int ports, out string? error);
        Assert.False(ok);
        Assert.Equal(0, ports);
        Assert.NotNull(error);
    }

    [Fact]
    public void Test5Port_ReturnsCorrectPortCount()
    {
        string path = Path.Combine(TestData, "Test_5Port.s5p");
        bool ok = TouchstoneIO.TryGetPortCount(path, out int ports, out string? error);
        Assert.True(ok, error);
        Assert.Equal(5, ports);
    }
}
