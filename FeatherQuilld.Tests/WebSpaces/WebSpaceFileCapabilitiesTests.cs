using FeatherQuilld.Utils.WebSpaces;

namespace FeatherQuilld.Tests.WebSpaces;

public class WebSpaceFileCapabilitiesTests
{
    [Fact]
    public void All_IncludesExpectedFlags()
    {
        Assert.True(WebSpaceFileCapabilities.All["compress_7z"]);
        Assert.True(WebSpaceFileCapabilities.All["paginated_list"]);
        Assert.True(WebSpaceFileCapabilities.All["trash"]);
    }

    [Fact]
    public void ToResponse_ReturnsDictionary()
    {
        var response = WebSpaceFileCapabilities.ToResponse();
        Assert.IsType<Dictionary<string, bool>>(response);
        Assert.True(((Dictionary<string, bool>)response)["share"]);
    }
}
