using FeatherQuilld.Controllers;

namespace FeatherQuilld.Tests.WebSpaces;

public class ConsoleWsCommandTests
{
    [Theory]
    [InlineData("""{"event":"send command","args":["hello"]}""", true, "hello")]
    [InlineData("""{"event":"send_command","args":["x"]}""", true, "x")]
    [InlineData("""{"event":"auth","args":["tok"]}""", false, "")]
    [InlineData("""{"event":"send command","args":[]}""", false, "")]
    [InlineData("""{"event":"send stats","args":[]}""", false, "")]
    [InlineData("not-json", false, "")]
    public void TryParseSendCommand(string json, bool expected, string command)
    {
        var ok = WebSpacesController.TryParseSendCommand(json, out var parsed);
        Assert.Equal(expected, ok);
        if (expected)
            Assert.Equal(command, parsed);
    }

    [Theory]
    [InlineData("""{"event":"send stats","args":[]}""", true)]
    [InlineData("""{"event":"send_stats","args":[]}""", true)]
    [InlineData("""{"event":"send command","args":["x"]}""", false)]
    public void TryParseSendStats(string json, bool expected)
    {
        Assert.Equal(expected, WebSpacesController.TryParseSendStats(json));
    }
}
