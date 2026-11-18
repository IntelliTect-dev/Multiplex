using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Multiplex.Web.SignIn;

namespace Multiplex.Tests.SignIn;

public sealed class PasscodeHasherTests
{
    [Fact]
    public void Hash_has_the_documented_format_and_verifies()
    {
        var hash = PasscodeHasher.Hash("correct horse battery");

        var parts = hash.Split('$');
        Assert.Equal("pbkdf2-sha256", parts[0]);
        Assert.True(int.Parse(parts[1]) >= 210_000);
        Assert.Equal(16, Convert.FromBase64String(parts[2]).Length);
        Assert.Equal(32, Convert.FromBase64String(parts[3]).Length);
        Assert.True(PasscodeHasher.Verify("correct horse battery", hash));
    }

    [Fact]
    public void Same_passcode_hashes_differently_each_time()
    {
        Assert.NotEqual(PasscodeHasher.Hash("same passcode"), PasscodeHasher.Hash("same passcode"));
    }

    [Theory]
    [InlineData("wrong passcode")]
    [InlineData("")]
    [InlineData(null)]
    public void Wrong_passcode_does_not_verify(string? attempt)
    {
        Assert.False(PasscodeHasher.Verify(attempt, PasscodeHasher.Hash("correct horse battery")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("plaintext")]
    [InlineData("pbkdf2-sha256$1000$c2FsdHNhbHRzYWx0$aGFzaGhhc2hoYXNoaGFzaA==")] // too few iterations
    [InlineData("pbkdf2-sha1$210000$c2FsdHNhbHRzYWx0$aGFzaGhhc2hoYXNoaGFzaA==")]
    [InlineData("pbkdf2-sha256$210000$not base64!$aGFzaA==")]
    public void Malformed_or_weak_stored_hashes_never_verify(string? stored)
    {
        Assert.False(PasscodeHasher.IsValidFormat(stored));
        Assert.False(PasscodeHasher.Verify("anything", stored));
    }

    [Fact]
    public void Cli_reads_stdin_and_prints_only_a_verifiable_hash()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = PasscodeCli.Run(new StringReader("my long passcode\n"), output, error);

        Assert.Equal(0, exit);
        var hash = output.ToString().Trim();
        Assert.True(PasscodeHasher.Verify("my long passcode", hash));
        Assert.DoesNotContain("my long passcode", output.ToString() + error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    public void Cli_refuses_a_missing_or_short_passcode(string input)
    {
        var output = new StringWriter();

        var exit = PasscodeCli.Run(new StringReader(input), output, new StringWriter());

        Assert.NotEqual(0, exit);
        Assert.Equal("", output.ToString());
    }
}

public sealed class PasscodeGateTests
{
    private readonly ManualTime _time = new(DateTimeOffset.Parse("2026-11-17T10:00:00Z"));

    private PasscodeGate Gate(string? hash) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [PasscodeGate.HashKey] = hash }).Build(),
        _time, NullLogger<PasscodeGate>.Instance);

    [Fact]
    public void Lock_lifts_after_fifteen_minutes()
    {
        var gate = Gate(PasscodeHasher.Hash("right passcode"));
        for (var i = 0; i < 5; i++) gate.Check("p1", "10.0.0.1", "wrong");
        Assert.Equal(GateResult.LockedOut, gate.Check("p1", "10.0.0.1", "right passcode"));

        _time.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));

        Assert.Equal(GateResult.Passed, gate.Check("p1", "10.0.0.1", "right passcode"));
    }

    [Fact]
    public void Missing_hash_fails_closed()
    {
        Assert.Equal(GateResult.NotConfigured, Gate(null).Check("p1", "10.0.0.1", "anything"));
        Assert.Equal(GateResult.NotConfigured, Gate("plaintext-passcode").Check("p1", "10.0.0.1", "plaintext-passcode"));
    }

    /// <summary>
    /// A burst of guesses sent at once must not all slip past the lock check while the first
    /// ones are still being hashed: at most <see cref="PasscodeGate.MaxFailures"/> are ever checked.
    /// </summary>
    [Fact]
    public async Task Concurrent_wrong_guesses_never_get_more_checks_than_the_limit()
    {
        const int burst = 20;
        var checks = 0;
        using var release = new ManualResetEventSlim(false);
        var gate = new PasscodeGate(Config(PasscodeHasher.Hash("right passcode")), _time, NullLogger<PasscodeGate>.Instance, (passcode, _) =>
        {
            Interlocked.Increment(ref checks);
            release.Wait(TimeSpan.FromSeconds(60)); // hold every checked guess "mid-hash"
            return passcode == "right passcode";
        });

        // Real threads, so every guess is in flight at the same time.
        var guesses = Enumerable.Range(0, burst)
            .Select(i => Task.Factory.StartNew(() => gate.Check("p1", "10.0.0.1", "wrong-" + i),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default))
            .ToArray();
        try
        {
            // While the checked ones are held, every other guess is already refused, unchecked.
            await WaitUntilAsync(() => guesses.Count(g => g.IsCompleted) == burst - PasscodeGate.MaxFailures
                                       && Volatile.Read(ref checks) == PasscodeGate.MaxFailures);
            Assert.All(guesses.Where(g => g.IsCompleted), g => Assert.Equal(GateResult.LockedOut, g.Result));
        }
        finally
        {
            release.Set();
        }

        var results = await Task.WhenAll(guesses);

        Assert.Equal(PasscodeGate.MaxFailures, Volatile.Read(ref checks));
        Assert.Equal(PasscodeGate.MaxFailures - 1, results.Count(r => r == GateResult.Wrong));
        // Now locked: even the right passcode is refused without being checked.
        Assert.Equal(GateResult.LockedOut, gate.Check("p1", "10.0.0.1", "right passcode"));
        Assert.Equal(GateResult.LockedOut, gate.Check("p2", "10.0.0.1", "right passcode"));
        Assert.Equal(PasscodeGate.MaxFailures, Volatile.Read(ref checks));
    }

    [Fact]
    public void Right_passcode_clears_the_callers_earlier_wrong_tries()
    {
        var gate = Gate(PasscodeHasher.Hash("right passcode"));
        for (var i = 0; i < PasscodeGate.MaxFailures - 1; i++)
            Assert.Equal(GateResult.Wrong, gate.Check("p" + i, "10.0.0.1", "wrong"));

        Assert.Equal(GateResult.Passed, gate.Check("owner", "10.0.0.1", "right passcode"));

        // A later typo from the same address does not lock the owner out on the strength of the old tries.
        for (var i = 0; i < PasscodeGate.MaxFailures - 1; i++)
            Assert.Equal(GateResult.Wrong, gate.Check("q" + i, "10.0.0.1", "wrong"));
    }

    [Fact]
    public void A_check_that_throws_does_not_use_up_a_try()
    {
        var calls = 0;
        var gate = new PasscodeGate(Config(PasscodeHasher.Hash("right passcode")), _time, NullLogger<PasscodeGate>.Instance, (passcode, stored) =>
            ++calls <= PasscodeGate.MaxFailures ? throw new InvalidOperationException("boom") : PasscodeHasher.Verify(passcode, stored));

        for (var i = 0; i < PasscodeGate.MaxFailures; i++)
            Assert.Throws<InvalidOperationException>(() => gate.Check("p1", "10.0.0.1", "right passcode"));

        Assert.Equal(GateResult.Passed, gate.Check("p1", "10.0.0.1", "right passcode"));
    }

    /// <summary>
    /// A pending id is a capability (whoever holds it can cancel that authorization), so the
    /// gate's warnings name it by a short hash only: enough to match log lines, useless to replay.
    /// </summary>
    [Fact]
    public void Gate_logs_name_a_pending_authorization_by_a_short_hash_not_by_its_id()
    {
        const string pendingId = "pending-capability-0123456789abcdef";
        var logs = new LogCapture();
        var gate = new PasscodeGate(Config(PasscodeHasher.Hash("right passcode")), _time, logs.For<PasscodeGate>(), (_, _) => false);

        for (var i = 0; i <= PasscodeGate.MaxFailures; i++) gate.Check(pendingId, "10.0.0.1", "wrong");

        Assert.Contains(logs.Entries, e => e.Message.Contains("Wrong connect passcode"));
        Assert.Contains(logs.Entries, e => e.Message.Contains("locked out"));
        Assert.DoesNotContain(pendingId, logs.AllText);
        Assert.All(logs.Entries, e => Assert.Contains(SignInPaths.HashName(pendingId)[..8], e.Message));
    }

    private static IConfiguration Config(string? hash) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [PasscodeGate.HashKey] = hash }).Build();

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met in time.");
            await Task.Delay(50);
        }
    }
}

/// <summary>A clock the test moves by hand.</summary>
public sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
