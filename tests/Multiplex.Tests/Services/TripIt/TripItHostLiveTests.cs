using System.Text.Json;
using Xunit.Abstractions;

namespace Multiplex.Tests.Services.TripIt;

/// <summary>
/// The hosted TripIt service against the real TripIt, signed in as the dedicated TEST account.
/// Opt-in, same guard as the library's live tests: <c>TRIPIT_TEST_SESSION_FILE</c> (a session
/// snapshot of the test account) and <c>TRIPIT_TEST_ACCOUNT</c> (its email; required with the
/// file). The first call checks the profile's email addresses and fails before anything else
/// when the session belongs to any other account. Writes also need <c>TRIPIT_ALLOW_WRITES=1</c>
/// and stay inside one dated, private throwaway trip.
/// </summary>
public sealed class TripItHostLiveTests(ITestOutputHelper output)
{
    private const string Account = "live-test-account";

    [TripItLiveFact]
    public async Task Read_tools_answer_for_the_test_account_through_the_real_HTTP_stack()
    {
        using var host = SignedInHost();
        var mcp = host.McpFor(Account);
        await RequireTestAccountAsync(mcp);

        var status = await mcp.CallAsync("tripit_session_status");
        Assert.False(status.IsError, status.Text);
        Assert.Equal("valid", JsonDocument.Parse(status.Text).RootElement.GetProperty("state").GetString());

        var trips = await mcp.CallAsync("tripit_list_trips", new { timeframe = "upcoming", pageSize = 5 });
        Assert.False(trips.IsError, trips.Text);
        var lookup = await mcp.CallAsync("tripit_lookup", new { kind = "airport", query = "SEA" });
        Assert.False(lookup.IsError, lookup.Text);
        Assert.Contains("\"SEA\"", lookup.Text, StringComparison.Ordinal);
    }

    [TripItLiveFact(Writes = true)]
    public async Task Create_then_delete_a_throwaway_trip_through_the_confirm_step()
    {
        using var host = SignedInHost();
        var mcp = host.McpFor(Account);
        await RequireTestAccountAsync(mcp);

        var name = $"ZZ Multiplex host test {DateTime.UtcNow:yyyy-MM-dd HHmm} - safe to delete";
        var created = await mcp.CallAsync("tripit_create_trip", new
        {
            destination = "Reykjavik, Iceland", startDate = "2027-05-10", endDate = "2027-05-12", name,
        });
        Assert.False(created.IsError, created.Text);
        var uuid = JsonDocument.Parse(created.Text).RootElement.GetProperty("created").GetProperty("uuid").GetString()!;
        output.WriteLine($"created trip {uuid}");
        var deleted = false;
        try
        {
            var note = await mcp.CallAsync("tripit_add_note", new { tripUuid = uuid, title = "ZZ host test note", date = "2027-05-11" });
            Assert.False(note.IsError, note.Text);

            var preview = await mcp.CallAsync("tripit_delete_trip", new { tripUuid = uuid, confirmName = name });
            Assert.False(preview.IsError, preview.Text);
            var p = JsonDocument.Parse(preview.Text).RootElement;
            output.WriteLine(p.GetProperty("preview").GetString());
            Assert.Contains("and its 1 plan.", p.GetProperty("preview").GetString(), StringComparison.Ordinal);

            var stillThere = await mcp.CallAsync("tripit_get_trip", new { tripUuid = uuid });
            Assert.False(stillThere.IsError, "the preview must not delete anything: " + stillThere.Text);

            var delete = await mcp.CallAsync("tripit_delete_trip",
                new { tripUuid = uuid, confirmName = name, confirm_token = p.GetProperty("confirm_token").GetString() });
            Assert.False(delete.IsError, delete.Text);
            deleted = true;

            var gone = await mcp.CallAsync("tripit_get_trip", new { tripUuid = uuid });
            Assert.True(gone.IsError);
        }
        finally
        {
            if (!deleted)
            {
                // Best effort: take the confirm step again to remove the throwaway trip.
                var again = await mcp.CallAsync("tripit_delete_trip", new { tripUuid = uuid, confirmName = name });
                if (!again.IsError)
                {
                    var token = JsonDocument.Parse(again.Text).RootElement.GetProperty("confirm_token").GetString();
                    await mcp.CallAsync("tripit_delete_trip", new { tripUuid = uuid, confirmName = name, confirm_token = token });
                }
            }
        }
    }

    private static TripItTestHost SignedInHost()
    {
        var host = new TripItTestHost(fakeTripIt: false);
        host.Store.Data[Account] = File.ReadAllText(Environment.GetEnvironmentVariable(TripItLiveFactAttribute.SessionFileVariable)!);
        return host;
    }

    /// <summary>Fails the test unless the session is signed in as TRIPIT_TEST_ACCOUNT. Never prints the addresses found.</summary>
    private static async Task RequireTestAccountAsync(McpTestClient mcp)
    {
        var expected = Environment.GetEnvironmentVariable(TripItLiveFactAttribute.AccountVariable);
        if (string.IsNullOrWhiteSpace(expected))
        {
            throw new InvalidOperationException(
                $"{TripItLiveFactAttribute.SessionFileVariable} is set but {TripItLiveFactAttribute.AccountVariable} is not.");
        }
        var (isError, text) = await mcp.CallAsync("tripit_get_profile");
        Assert.False(isError, "tripit_get_profile failed: " + text);
        var profile = JsonDocument.Parse(text).RootElement;
        var emails = profile.TryGetProperty("ProfileEmailAddresses", out var list) && list.TryGetProperty("ProfileEmailAddress", out var entries)
            ? (entries.ValueKind == JsonValueKind.Array ? entries.EnumerateArray().ToList() : [entries])
                .Select(e => e.TryGetProperty("address", out var a) ? a.GetString() : null).OfType<string>().ToList()
            : [];
        if (!emails.Any(e => string.Equals(e, expected, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"The session in {TripItLiveFactAttribute.SessionFileVariable} is not signed in as {TripItLiveFactAttribute.AccountVariable}. " +
                "Refusing to run live tests against it.");
        }
    }
}

/// <summary>
/// A live TripIt test: skipped unless <c>TRIPIT_TEST_SESSION_FILE</c> is set. When it is set,
/// <c>TRIPIT_TEST_ACCOUNT</c> must be too (the test fails otherwise). <see cref="Writes"/> also
/// requires <c>TRIPIT_ALLOW_WRITES=1</c>.
/// </summary>
public sealed class TripItLiveFactAttribute : FactAttribute
{
    public const string SessionFileVariable = "TRIPIT_TEST_SESSION_FILE";
    public const string AccountVariable = "TRIPIT_TEST_ACCOUNT";

    public TripItLiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SessionFileVariable)))
            Skip = $"Live TripIt test. Set {SessionFileVariable} and {AccountVariable} to run it against the test account.";
    }

    public bool Writes
    {
        get => Skip is not null && Skip.Contains("TRIPIT_ALLOW_WRITES", StringComparison.Ordinal);
        set
        {
            if (value && Skip is null && Environment.GetEnvironmentVariable("TRIPIT_ALLOW_WRITES") != "1")
                Skip = "Set TRIPIT_ALLOW_WRITES=1 to run live write tests.";
        }
    }
}
