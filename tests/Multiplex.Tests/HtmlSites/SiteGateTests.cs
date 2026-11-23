using Microsoft.Extensions.Time.Testing;
using Multiplex.Web.HtmlSites;

namespace Multiplex.Tests.HtmlSites;

public class SiteGateTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero));
    private readonly List<TimeSpan> _waits = [];

    /// <summary>A gate whose waits advance the fake clock at once, with jitter fixed at <paramref name="random"/>.</summary>
    private SiteGate Gate(double random = 0, SiteGateOptions? options = null) =>
        new(options ?? new SiteGateOptions { Site = "Amazon" }, _time, () => random, (wait, _) =>
        {
            _waits.Add(wait);
            _time.Advance(wait);
            return Task.CompletedTask;
        });

    private Task<DateTimeOffset> Send(SiteGate gate, SiteRequestKind kind = SiteRequestKind.Page, string? sequence = null,
        DateTimeOffset? deadline = null, Action? beforeSend = null) =>
        gate.RunAsync(kind, sequence, deadline, beforeSend, _ => Task.FromResult(_time.GetUtcNow()), CancellationToken.None);

    [Fact]
    public async Task PagesFromSeparateCalls_AreAtLeast3SecondsApart_PlusJitter()
    {
        var gate = Gate(random: 0.5);

        var first = await Send(gate);
        var second = await Send(gate);

        Assert.Equal(TimeSpan.FromSeconds(4), second - first); // 3 s + half of the 2 s jitter
    }

    [Fact]
    public async Task ConsecutivePagesOfOneSearch_Are2To3SecondsApart_NeverUnder2()
    {
        var none = Gate(random: 0);
        var a = await Send(none, sequence: "search-1");
        var b = await Send(none, sequence: "search-1");
        Assert.Equal(TimeSpan.FromSeconds(2), b - a);

        var most = Gate(random: 0.999);
        var c = await Send(most, sequence: "search-1");
        var d = await Send(most, sequence: "search-1");
        Assert.InRange(d - c, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task AnotherCallsRequestBetweenTwoPagesOfASearch_GetsTheFullGap_OnBothSides()
    {
        var gate = Gate();

        var page1 = await Send(gate, sequence: "search-1");
        var other = await Send(gate);
        var page2 = await Send(gate, sequence: "search-1");

        Assert.Equal(TimeSpan.FromSeconds(3), other - page1);
        Assert.Equal(TimeSpan.FromSeconds(3), page2 - other);
    }

    [Fact]
    public async Task AnImage_Follows1SecondAfterItsPage_AndTheNextPageStillWaitsItsGap()
    {
        var gate = Gate();

        var page = await Send(gate);
        var image = await Send(gate, SiteRequestKind.Image);
        var next = await Send(gate);

        Assert.Equal(TimeSpan.FromSeconds(1), image - page);
        Assert.Equal(TimeSpan.FromSeconds(3), next - page);
    }

    [Fact]
    public async Task OnlyOneRequestIsInFlight()
    {
        var gate = Gate();
        var release = new TaskCompletionSource();
        var inFlight = 0;
        var most = 0;
        async Task<int> Slow(CancellationToken _)
        {
            most = Math.Max(most, Interlocked.Increment(ref inFlight));
            await release.Task;
            Interlocked.Decrement(ref inFlight);
            return 0;
        }

        var first = gate.RunAsync(SiteRequestKind.Page, null, null, null, Slow, CancellationToken.None);
        var second = gate.RunAsync(SiteRequestKind.Page, null, null, null, Slow, CancellationToken.None);
        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, most);
    }

    [Fact]
    public async Task ACallThatWaitsOver30SecondsForItsTurn_Fails_WithoutSending()
    {
        var gate = Gate();
        var release = new TaskCompletionSource();
        var holder = gate.RunAsync(SiteRequestKind.Page, null, null, null, async _ => { await release.Task; return 0; }, CancellationToken.None);
        var sent = false;

        var queued = gate.RunAsync(SiteRequestKind.Page, null, null, null, _ => { sent = true; return Task.FromResult(0); }, CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(31));

        var busy = await Assert.ThrowsAsync<SiteBusyException>(() => queued);
        Assert.Contains("queued", busy.Message);
        release.SetResult();
        await holder;
        Assert.False(sent);
        // The abandoned turn was not leaked: the gate still serves the next call.
        await Send(gate);
    }

    [Fact]
    public async Task ACallCancelledWhileItWaitsForItsTurn_LeavesTheGateUsable()
    {
        var gate = Gate();
        var release = new TaskCompletionSource();
        var holder = gate.RunAsync(SiteRequestKind.Page, null, null, null, async _ => { await release.Task; return 0; }, CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        var sent = false;

        var queued = gate.RunAsync(SiteRequestKind.Page, null, null, null, _ => { sent = true; return Task.FromResult(0); }, cancel.Token);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        release.SetResult();
        await holder;
        Assert.False(sent);
        // Its place in the queue was given up, not kept: the next call gets its turn.
        await Send(gate);
        await Send(gate);
    }

    [Fact]
    public async Task AWaitForATurn_IsCutToTheTimeLeftBeforeTheDeadline()
    {
        var gate = Gate();
        var release = new TaskCompletionSource();
        var holder = gate.RunAsync(SiteRequestKind.Page, null, null, null, async _ => { await release.Task; return 0; }, CancellationToken.None);

        var queued = Send(gate, deadline: _time.GetUtcNow() + TimeSpan.FromSeconds(5));
        _time.Advance(TimeSpan.FromSeconds(6));

        var busy = await Assert.ThrowsAsync<SiteBusyException>(() => queued);
        Assert.Contains("ran out of time", busy.Message);
        release.SetResult();
        await holder;
    }

    [Fact]
    public async Task ARequestWhoseSpacingWaitWouldPassTheDeadline_IsNotSent()
    {
        var gate = Gate();
        await Send(gate);

        await Assert.ThrowsAsync<SiteBusyException>(() => Send(gate, deadline: _time.GetUtcNow() + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task BeforeSend_RunsAtTheFrontOfTheQueue_AndCanGiveTheTurnUp()
    {
        var gate = Gate();

        await Assert.ThrowsAsync<SiteBlockedException>(() =>
            Send(gate, beforeSend: () => throw new SiteBlockedException("paused")));

        Assert.Empty(_waits);
        await Send(gate); // the turn was released
    }

    [Fact]
    public async Task TheHourlyCap_RefusesRequests_UntilTheWindowFreesUp()
    {
        var gate = Gate(options: new SiteGateOptions { Site = "Amazon", HourlyCap = 3, DailyCap = 100 });
        for (var i = 0; i < 3; i++) await Send(gate);

        var busy = await Assert.ThrowsAsync<SiteBusyException>(() => Send(gate));
        Assert.Contains("this hour's limit of 3", busy.Message);

        _time.Advance(TimeSpan.FromHours(1));
        await Send(gate);
    }

    [Fact]
    public async Task TheDailyCap_RefusesRequests_UntilTheWindowFreesUp()
    {
        var gate = Gate(options: new SiteGateOptions { Site = "Amazon", HourlyCap = 100, DailyCap = 2 });
        await Send(gate);
        await Send(gate);

        var busy = await Assert.ThrowsAsync<SiteBusyException>(() => Send(gate));
        Assert.Contains("today's limit of 2", busy.Message);

        _time.Advance(TimeSpan.FromDays(1));
        await Send(gate);
    }

    [Fact]
    public async Task ImagesCountTowardTheCaps()
    {
        var gate = Gate(options: new SiteGateOptions { Site = "Amazon", HourlyCap = 2, DailyCap = 100 });
        await Send(gate);
        await Send(gate, SiteRequestKind.Image);

        await Assert.ThrowsAsync<SiteBusyException>(() => Send(gate));
    }
}
