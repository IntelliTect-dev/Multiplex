namespace Multiplex.Sites.GoogleFlights.Tests.Live;

/// <summary>
/// A test against the real Google Flights. Opt in with <c>MULTIPLEX_LIVE_TESTS=1</c>, the variable every
/// Multiplex live test uses. Live tests spend a small, fixed request budget: never run them in a loop.
/// </summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MULTIPLEX_LIVE_TESTS") != "1")
            Skip = "Live test against Google Flights. Set MULTIPLEX_LIVE_TESTS=1 to run it (a small, paced request budget).";
    }
}
