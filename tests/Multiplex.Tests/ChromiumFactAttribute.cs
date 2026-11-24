namespace Multiplex.Tests;

/// <summary>Resolves the Chromium binary used by browser-driven tests: MULTIPLEX_CHROMIUM, else /usr/bin/chromium.</summary>
public static class ChromiumLocator
{
    public static string? Path { get; } = Resolve();

    private static string? Resolve()
    {
        var path = Environment.GetEnvironmentVariable("MULTIPLEX_CHROMIUM") ?? "/usr/bin/chromium";
        return File.Exists(path) ? path : null;
    }
}

/// <summary>A fact that needs a local Chromium; skipped when none is found.</summary>
public sealed class ChromiumFactAttribute : FactAttribute
{
    public ChromiumFactAttribute()
    {
        if (ChromiumLocator.Path is null)
            Skip = "No Chromium found. Set MULTIPLEX_CHROMIUM to its path.";
    }
}

/// <summary>
/// A fact that hits a real website over plain HTTP, with no browser; opt in with MULTIPLEX_LIVE_TESTS=1.
/// </summary>
public sealed class LiveHttpFactAttribute : FactAttribute
{
    public LiveHttpFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MULTIPLEX_LIVE_TESTS") != "1")
            Skip = "Live test. Set MULTIPLEX_LIVE_TESTS=1 to run against the real website.";
    }
}
