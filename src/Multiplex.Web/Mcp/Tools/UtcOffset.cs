using System.Globalization;

namespace Multiplex.Web.Mcp.Tools;

internal static class UtcOffset
{
    private const string DateFormat = "dddd, MMMM d, yyyy";
    private const string TimeFormat = @"hh\:mm\:ss";

    /// <summary>
    /// Derives the current offset (e.g. <c>UTC+13:00</c>) from a location's displayed local date and 24-hour time
    /// against real UTC. time.now's static UTC links show the standard offset even while DST is active, so the
    /// live clock is the reliable source. Rounds to 15 minutes to absorb the seconds between page read and now.
    /// </summary>
    public static string From(string date, string time, DateTimeOffset utcNow)
    {
        var local = DateTime.ParseExact(date, DateFormat, CultureInfo.InvariantCulture)
                    + TimeSpan.ParseExact(time, TimeFormat, CultureInfo.InvariantCulture);
        var minutes = (int)(Math.Round((local - utcNow.UtcDateTime).TotalMinutes / 15) * 15);

        var sign = minutes < 0 ? '-' : '+';
        var abs = Math.Abs(minutes);
        return $"UTC{sign}{abs / 60:00}:{abs % 60:00}";
    }
}
