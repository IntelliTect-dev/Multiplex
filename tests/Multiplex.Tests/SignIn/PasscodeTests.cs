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
}

/// <summary>A clock the test moves by hand.</summary>
public sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
