using FeatherQuilld.Utils.WebSpaces.Schedules;

namespace FeatherQuilld.Tests.WebSpaces;

public class ScheduleCommandPayloadTests
{
    [Theory]
    [InlineData("wp cron event run --due-now", "wp cron event run --due-now")]
    [InlineData("  php -q crons/cron.php  ", "php -q crons/cron.php")]
    [InlineData("""{"command":"wp cron event run --due-now"}""", "wp cron event run --due-now")]
    [InlineData("""{"command":" php artisan schedule:run "}""", "php artisan schedule:run")]
    public void Parse_AcceptsRawAndJson(string payload, string expected) =>
        Assert.Equal(expected, ScheduleCommandPayload.Parse(payload));

    [Fact]
    public void Parse_InvalidJsonObject_TreatedAsLiteral()
    {
        var literal = "{not-json";
        Assert.Equal(literal, ScheduleCommandPayload.Parse(literal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    [InlineData("""{"command":""}""")]
    [InlineData("""{"command":"   "}""")]
    public void Parse_Empty_Throws(string? payload) =>
        Assert.Throws<ArgumentException>(() => ScheduleCommandPayload.Parse(payload));
}
