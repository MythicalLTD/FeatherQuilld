using FeatherQuilld.Controllers;

namespace FeatherQuilld.Tests.WebSpaces;

public class ConsoleWsCommandTests
{
    [Theory]
    [InlineData("""{"event":"send command","args":["hello"]}""", true, "hello")]
    [InlineData("""{"event":"send_command","args":["x"]}""", true, "x")]
    [InlineData("""{"event":"auth","args":["tok"]}""", false, "")]
    [InlineData("""{"event":"send command","args":[]}""", false, "")]
    [InlineData("not-json", false, "")]
    public void TryParseSendCommand(string json, bool expected, string command)
    {
        var ok = WebSpacesController.TryParseSendCommand(json, out var parsed);
        Assert.Equal(expected, ok);
        if (expected)
            Assert.Equal(command, parsed);
    }
}
