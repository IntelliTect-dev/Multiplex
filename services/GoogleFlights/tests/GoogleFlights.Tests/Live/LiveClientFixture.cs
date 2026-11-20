using Multiplex.Sites.GoogleFlights.Http;

namespace Multiplex.Sites.GoogleFlights.Tests.Live;

/// <summary>
/// One real client for the whole live suite, so its pacing and refusal cool-down cover every test: after a
/// refusal the remaining tests fail at once without reaching Google. Every exchange is recorded.
/// </summary>
public sealed class LiveClientFixture : IDisposable
{
    public LiveClientFixture()
    {
        if (Environment.GetEnvironmentVariable("MULTIPLEX_LIVE_TESTS") != "1")
            return;
        Recorder = new RecordingHandler(GoogleFlightsTransport.CreateNetworkHandler(), "the client's live test suite");
        // The client's own default pacing, unchanged: the live suite runs at the speed real use would.
        Client = new GoogleFlightsClient(new GoogleFlightsClientOptions(), TimeProvider.System, Recorder);
    }

    public GoogleFlightsClient Client { get; } = null!;

    internal RecordingHandler Recorder { get; } = null!;

    /// <summary>A departure about six weeks out, so every live search asks about real, bookable flights.</summary>
    public static DateOnly Departure { get; } = DateOnly.FromDateTime(DateTime.Today.AddDays(42));

    public void Dispose() => Client?.Dispose();
}

[CollectionDefinition(Name)]
public sealed class LiveCollection : ICollectionFixture<LiveClientFixture>
{
    public const string Name = "Google Flights live";
}
