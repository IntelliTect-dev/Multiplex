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

/// <summary>A fact that hits the real time.now site; opt in with MULTIPLEX_LIVE_TESTS=1 (also needs Chromium).</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute() => Skip = LiveSkipReason.Get();
}

/// <summary>A theory that hits the real time.now site; opt in with MULTIPLEX_LIVE_TESTS=1 (also needs Chromium).</summary>
public sealed class LiveTheoryAttribute : TheoryAttribute
{
    public LiveTheoryAttribute() => Skip = LiveSkipReason.Get();
}

internal static class LiveSkipReason
{
    public static string? Get() =>
        Environment.GetEnvironmentVariable("MULTIPLEX_LIVE_TESTS") != "1"
            ? "Live test. Set MULTIPLEX_LIVE_TESTS=1 to run against time.now."
            : ChromiumLocator.Path is null
                ? "No Chromium found. Set MULTIPLEX_CHROMIUM to its path."
                : null;
}
