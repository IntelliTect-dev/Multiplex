using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace TripIt.FunctionalTests;

/// <summary>
/// Live checks for trips the user is not travelling on, participant management and trip
/// merge. Opt-in (TRIPIT_ALLOW_WRITES=1). Every test works only on private, dated throwaway
/// trips it creates and deletes. Invitations go to a reserved example.com address
/// (RFC 2606), which can never reach a real person.
/// </summary>
public sealed class SharingAndMergeTests : IClassFixture<TripItSessionFixture>
{
    private const string InviteeEmail = "tripitex-test@example.com";

    private readonly TripItSessionFixture _fx;
    private readonly ITestOutputHelper _out;

    public SharingAndMergeTests(TripItSessionFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
    }

    private static bool WritesAllowed => Environment.GetEnvironmentVariable("TRIPIT_ALLOW_WRITES") == "1";

    private Task<Models.Trip> CreateThrowaway(string label, string start, string end, bool ownerTravels = true)
        => _fx.Client.Trips.CreateAsync(new TripInput
        {
            DisplayName = $"ZZ TripItEx {label} {DateTime.UtcNow:yyyy-MM-dd HHmm} - safe to delete",
            PrimaryLocation = "Reykjavik, Iceland",
            StartDate = start,
            EndDate = end,
            IsPrivate = true,
            IsOwnerTraveler = ownerTravels,
        });

    [SkippableFact]
    public async Task Trip_I_am_not_travelling_on_can_be_listed_edited_planned_and_deleted()
    {
        Skip.IfNot(_fx.HasSession && WritesAllowed, "Set TRIPIT_ALLOW_WRITES=1 with a session to run live write tests.");
        var client = _fx.Client;
        var trip = await CreateThrowaway("non-traveler", "2027-04-05", "2027-04-07", ownerTravels: false);
        _out.WriteLine($"created {trip.Uuid}");
        try
        {
            var me = await client.GetMyProfileRefAsync();
            var detail = await client.Trips.GetAsync(trip.Uuid!);
            var mine = TripsClient.Participants(detail, me).Single(p => p.IsMe);
            Assert.True(mine.IsOwner);
            Assert.False(mine.IsTraveler);

            var others = await client.Trips.ListAsync(TripTimeframe.Upcoming, 100, 1, default, TripTravelerFilter.Others);
            Assert.Contains(others.Trips, t => t.Uuid == trip.Uuid);
            var minesOnly = await client.Trips.ListAsync(TripTimeframe.Upcoming, 100, 1, default, TripTravelerFilter.Mine);
            Assert.DoesNotContain(minesOnly.Trips, t => t.Uuid == trip.Uuid);
            var all = await client.Trips.ListAsync(TripTimeframe.Upcoming, 100, 1);
            Assert.Contains(all.Trips, t => t.Uuid == trip.Uuid);

            var edited = await client.Trips.UpdateAsync(trip.Uuid!, t => t with { Description = "edited while not travelling" });
            Assert.Equal("edited while not travelling", edited.Description);
            var stillNot = TripsClient.Participants(await client.Trips.GetAsync(trip.Uuid!), me).Single(p => p.IsMe);
            Assert.False(stillNot.IsTraveler);

            var note = await client.Objects.CreateAsync(TripItObjectType.Note,
                new JsonObject { ["display_name"] = "ZZ note on someone else's trip", ["DateTime"] = new JsonObject { ["date"] = "2027-04-06" } },
                trip.Uuid);
            Assert.Equal(trip.Uuid, note.TripUuid);

            // Switch myself to travelling and back.
            var travelling = await client.Trips.UpdateAsync(trip.Uuid!, t => t with { IsOwnerTraveler = true });
            Assert.True(TripsClient.Participants(await client.Trips.GetAsync(travelling.Uuid!), me).Single(p => p.IsMe).IsTraveler);
        }
        finally
        {
            await client.Trips.DeleteAsync(trip.Uuid!);
        }
    }

    [SkippableFact]
    public async Task Participants_can_be_invited_changed_and_removed()
    {
        Skip.IfNot(_fx.HasSession && WritesAllowed, "Set TRIPIT_ALLOW_WRITES=1 with a session to run live write tests.");
        var client = _fx.Client;
        var trip = await CreateThrowaway("sharing", "2027-04-12", "2027-04-14");
        _out.WriteLine($"created {trip.Uuid}");
        try
        {
            var me = await client.GetMyProfileRefAsync();
            await client.Trips.InviteAsync(trip.Uuid!, [InviteeEmail], TripRole.Viewer, "TripItEx automated test - please ignore");

            var invited = TripsClient.Participants(await client.Trips.GetAsync(trip.Uuid!), me);
            foreach (var p in invited) _out.WriteLine($"participant {p.ProfileRef} {p.Name} {p.Role} me={p.IsMe}");
            var guest = Assert.Single(invited, p => !p.IsMe);
            Assert.Equal(TripRole.Viewer, guest.Role);

            await client.Trips.ChangeRoleAsync(trip.Uuid!, guest.ProfileRef!, TripRole.Editor);
            var changed = TripsClient.Participants(await client.Trips.GetAsync(trip.Uuid!), me).Single(p => p.ProfileRef == guest.ProfileRef);
            Assert.Equal(TripRole.Editor, changed.Role);

            await client.Trips.RemoveParticipantAsync(trip.Uuid!, guest.ProfileRef!);
            var after = TripsClient.Participants(await client.Trips.GetAsync(trip.Uuid!), me);
            Assert.DoesNotContain(after, p => p.ProfileRef == guest.ProfileRef);
        }
        finally
        {
            await client.Trips.DeleteAsync(trip.Uuid!);
        }
    }

    [SkippableFact]
    public async Task Merge_moves_plans_and_removes_the_source_trip()
    {
        Skip.IfNot(_fx.HasSession && WritesAllowed, "Set TRIPIT_ALLOW_WRITES=1 with a session to run live write tests.");
        var client = _fx.Client;
        var into = await CreateThrowaway("merge-into", "2027-04-19", "2027-04-22");
        var from = await CreateThrowaway("merge-from", "2027-04-20", "2027-04-21");
        _out.WriteLine($"into {into.Uuid} from {from.Uuid}");
        var fromDeleted = false;
        try
        {
            await client.Objects.CreateAsync(TripItObjectType.Note,
                new JsonObject { ["display_name"] = "ZZ merge note", ["DateTime"] = new JsonObject { ["date"] = "2027-04-20" } },
                from.Uuid);

            var merged = await client.Trips.MergeAsync(from.Uuid!, into.Uuid!);

            Assert.True(merged.Objects.ContainsKey(TripItObjectType.Note));
            var gone = await Record.ExceptionAsync(() => client.Trips.GetAsync(from.Uuid!));
            _out.WriteLine($"source after merge: {gone?.Message ?? "still exists"}");
            fromDeleted = gone is TripItApiException;
            Assert.True(fromDeleted, "the source trip should be gone after a merge");
        }
        finally
        {
            if (!fromDeleted)
            {
                try { await client.Trips.DeleteAsync(from.Uuid!); } catch (TripItApiException) { }
            }
            await client.Trips.DeleteAsync(into.Uuid!);
        }
    }
}
