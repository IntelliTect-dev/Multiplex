using Multiplex.Web.Servers.Time;

namespace Multiplex.Tests.Servers.Time;

public class UtcOffsetTests
{
    // Sunday 2026-11-15 23:11:30 UTC.
    private static readonly DateTimeOffset UtcNow = new(2026, 11, 15, 23, 11, 30, TimeSpan.Zero);

    [Theory]
    [InlineData("Monday, November 16, 2026", "08:11:27", "UTC+09:00")] // Japan
    [InlineData("Monday, November 16, 2026", "12:11:28", "UTC+13:00")] // New Zealand, DST active
    [InlineData("Monday, November 16, 2026", "04:41:28", "UTC+05:30")] // India, half-hour zone
    [InlineData("Monday, November 16, 2026", "04:56:28", "UTC+05:45")] // Nepal, 45-minute zone
    [InlineData("Monday, November 16, 2026", "13:11:30", "UTC+14:00")] // Kiribati, across the date line
    [InlineData("Sunday, November 15, 2026", "23:11:30", "UTC+00:00")] // UTC itself
    [InlineData("Sunday, November 15, 2026", "18:11:29", "UTC-05:00")] // Peru
    [InlineData("Sunday, November 15, 2026", "13:11:30", "UTC-10:00")] // Hawaii
    [InlineData("Sunday, November 15, 2026", "11:11:30", "UTC-12:00")] // Baker Island, far west
    public void From_ReturnsOffsetOfLocalClockAgainstUtc(string date, string time, string expected)
    {
        Assert.Equal(expected, UtcOffset.From(date, time, UtcNow));
    }

    [Theory]
    [InlineData("November 16, 2026", "08:11:27")]       // missing weekday
    [InlineData("Monday, November 16, 2026", "8:11 AM")] // 12-hour clock
    public void From_RejectsUnexpectedFormats(string date, string time)
    {
        Assert.Throws<FormatException>(() => UtcOffset.From(date, time, UtcNow));
    }
}
