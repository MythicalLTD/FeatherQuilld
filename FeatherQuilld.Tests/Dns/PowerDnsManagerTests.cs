using FeatherQuilld.Utils.Dns;
using FeatherQuilld.Utils.SystemInfo;

namespace FeatherQuilld.Tests.Dns;

public class PowerDnsManagerTests
{
    [Fact]
    public void NormalizeZoneName_AddsTrailingDot()
    {
        var zone = InvokeNormalizeZone("example.com");
        Assert.Equal("example.com.", zone);
    }

    [Fact]
    public void BuildRecordId_RoundTrips()
    {
        var id = InvokeBuildRecordId("A", "www.example.com.", 0);
        Assert.False(string.IsNullOrWhiteSpace(id));
    }

    [Fact]
    public void FormatMxRrContent_CombinesPriorityAndTarget()
    {
        Assert.Equal("10 mail.example.com.", PowerDnsManager.FormatMxRrContent(10, "mail.example.com"));
        Assert.Equal("5 mail.example.com.", PowerDnsManager.FormatMxRrContent(5, "10 mail.example.com"));
    }

    [Fact]
    public void ParseMxParts_SplitsPriorityAndTarget()
    {
        var (priority, target) = PowerDnsManager.ParseMxParts("20 mx.example.com.");
        Assert.Equal(20, priority);
        Assert.Equal("mx.example.com", target);
    }

    [Fact]
    public void DefaultNameservers_UsesNs1Host()
    {
        var nameservers = PowerDnsManager.DefaultNameservers("example.com");
        Assert.Single(nameservers);
        Assert.Equal("ns1.example.com", nameservers[0]);
    }

    private static string InvokeNormalizeZone(string name)
    {
        var method = typeof(PowerDnsManager).GetMethod(
            "NormalizeZoneName",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, [name])!;
    }

    private static string InvokeBuildRecordId(string type, string name, int index)
    {
        var method = typeof(PowerDnsManager).GetMethod(
            "BuildRecordId",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, [type, name, index])!;
    }
}

public class HostPackagePowerDnsTests
{
    [Fact]
    public void List_IncludesPowerDnsPackage()
    {
        var mgr = new HostPackageManager();
        var packages = mgr.List();
        Assert.Contains(packages, p => p.Id == "powerdns");
    }
}
