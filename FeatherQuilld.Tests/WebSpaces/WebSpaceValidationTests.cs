using FeatherQuilld.Utils.WebSpaces;

namespace FeatherQuilld.Tests.WebSpaces;

public class WebSpaceValidationTests
{
    [Theory]
    [InlineData("example.com", true)]
    [InlineData("sub.example.com", true)]
    [InlineData("soak.local.test", true)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("-bad.com", false)]
    [InlineData("bad..com", false)]
    [InlineData("no spaces.com", false)]
    public void IsValidDomain(string domain, bool expected) =>
        Assert.Equal(expected, WebSpaceValidation.IsValidDomain(domain));
}
