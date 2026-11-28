using System.Net;
using Microsoft.AspNetCore.WebUtilities;
using Multiplex.Tests.Auth;
using Multiplex.Tests.SignIn;

namespace Multiplex.Tests.Identity;

/// <summary>Tests that change the process's environment variables, so no other test runs beside them.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "Process environment";
}

/// <summary>
/// A PC that runs Multiplex for real keeps its Microsoft app registration in environment variables,
/// which every host on it reads (#159). The test hosts start without one all the same: the tests of
/// what happens when sign-in is not set up must not turn red on the owner's own PC.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class MachineSignInIsolationTests : IDisposable
{
    private static readonly string[] s_variables =
        ["Multiplex__Identity__Microsoft__ClientId", "Multiplex__Identity__Microsoft__ClientSecret"];

    private readonly Dictionary<string, string?> _saved = s_variables.ToDictionary(v => v, Environment.GetEnvironmentVariable);

    public MachineSignInIsolationTests()
    {
        Environment.SetEnvironmentVariable(s_variables[0], "machine-client-id");
        Environment.SetEnvironmentVariable(s_variables[1], "machine-client-secret");
    }

    public void Dispose()
    {
        foreach (var (variable, value) in _saved) Environment.SetEnvironmentVariable(variable, value);
    }

    [Fact]
    public async Task The_auth_test_app_has_no_Microsoft_sign_in_even_when_the_PC_has_one()
    {
        using var app = new AuthTestApp();
        var clientId = await new OAuthDriver(app).RegisterAsync();

        var response = await app.Client().GetAsync(QueryHelpers.AddQueryString("/oauth/authorize",
            OAuthDriver.AuthorizeParameters(clientId, OAuthDriver.NewPkce()).Where(p => p.Value is not null)));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("Sign-in is not set up", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_sign_in_test_host_has_no_Microsoft_sign_in_even_when_the_PC_has_one()
    {
        await using var host = new SignInTestHost(new SignInTestOptions { ExecutablePath = Path.Combine(Path.GetTempPath(), "no-such-chrome.exe") });
        host.Completer.Add("p1", "stub", clientName: "Claude");

        var (response, html) = await host.NewClient().GetAsync("/connect/stub?p=p1");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("Sign-in is not set up", html);
    }
}
