using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Multiplex.Tests.SignIn;

/// <summary>
/// A tiny login site on a random loopback port, standing in for TripIt: a login form, a
/// persistent session cookie, a signed-in app page that calls its own API, and a JSON
/// profile endpoint. Its two users and their passwords are fake and exist only in these tests.
/// Reachable as <c>127.0.0.1</c> (allowed) and as <c>localhost</c> (a "foreign" host).
/// </summary>
public sealed class StubLoginSite : IAsyncDisposable
{
    public const string Email = "tester@example.test";
    public const string Password = "stub-pass-123";
    public const string AccountId = "stub-account-1";
    public const string DisplayName = "Stub Tester";

    /// <summary>A second account on the same site, for a second connection signed in as someone else.</summary>
    public const string OtherEmail = "other-tester@example.test";
    public const string OtherPassword = "other-pass-456";
    public const string OtherAccountId = "stub-account-2";
    public const string OtherDisplayName = "Other Tester";

    public const string CookieName = "stub_session";

    private static readonly User[] s_users =
    [
        new(Email, Password, AccountId, DisplayName),
        new(OtherEmail, OtherPassword, OtherAccountId, OtherDisplayName),
    ];

    private readonly WebApplication _app;

    /// <summary>Session token → the user it signs in.</summary>
    private readonly ConcurrentDictionary<string, User> _sessions = new();

    public ConcurrentQueue<string> Requests { get; } = new();

    public Uri BaseUrl { get; private set; } = null!;
    public int Port => BaseUrl.Port;

    private StubLoginSite(WebApplication app) => _app = app;

    public static async Task<StubLoginSite> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        var site = new StubLoginSite(app);
        site.Map(app);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        site.BaseUrl = new Uri(new Uri(address).GetLeftPart(UriPartial.Authority).Replace("localhost", "127.0.0.1") + "/");
        return site;
    }

    private string IssueSession(User user)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _sessions[token] = user;
        return token;
    }

    private User? SignedIn(HttpContext c) =>
        c.Request.Cookies.TryGetValue(CookieName, out var t) && t is not null && _sessions.TryGetValue(t, out var user) ? user : null;

    private void Map(WebApplication app)
    {
        app.Use(async (c, next) =>
        {
            Requests.Enqueue(c.Request.Path + c.Request.QueryString);
            await next();
        });

        app.MapGet("/login", (HttpContext c) => Results.Content(LoginPage(c, error: null), "text/html"));
        app.MapPost("/login", async (HttpContext c) =>
        {
            var form = await c.Request.ReadFormAsync();
            if (s_users.FirstOrDefault(u => form["email"] == u.Email && form["password"] == u.Password) is { } user)
            {
                var token = IssueSession(user);
                // Persistent, like "Keep me signed in": it must survive the browser closing.
                c.Response.Cookies.Append(CookieName, token, new CookieOptions { HttpOnly = true, Path = "/", Expires = DateTimeOffset.UtcNow.AddDays(1) });
                return Results.Redirect("/app");
            }
            return Results.Content(LoginPage(c, "Wrong email or password."), "text/html");
        });
        app.MapGet("/app", (HttpContext c) => SignedIn(c) is not null
            ? Results.Content("""
                <!doctype html><html><head><meta name="viewport" content="width=device-width"><title>Stub app</title></head>
                <body><h1 id="hello">Your trips</h1>
                <script>fetch('/api/profile', { headers: { 'X-Stub-App': 'web' } }).then(r => r.json()).then(p => document.getElementById('hello').textContent = 'Hello ' + p.name);</script>
                </body></html>
                """, "text/html")
            : Results.Redirect("/login"));
        app.MapGet("/api/profile", (HttpContext c) => SignedIn(c) is { } user
            ? Results.Json(new { id = user.AccountId, emails = new[] { user.Email }, name = user.DisplayName })
            : Results.StatusCode(401));
        app.MapGet("/foreign", () => Results.Content("<h1 id='foreign'>Somewhere else</h1>", "text/html"));
        app.MapGet("/foreign-tab", () => Results.Content("<h1>Somewhere else, new tab</h1>", "text/html"));
        app.MapGet("/help", () => Results.Content("<!doctype html><title>Help</title><h1 id='help'>Help</h1>", "text/html"));
    }

    private string LoginPage(HttpContext c, string? error) => $$"""
        <!doctype html>
        <html><head><meta name="viewport" content="width=device-width, initial-scale=1"><title>Stub sign in</title>
        <style>body{font:18px sans-serif;margin:0;padding:16px} input,button,a{display:block;width:100%;height:56px;font-size:20px;margin:0 0 16px;box-sizing:border-box}</style>
        </head><body>
        <form method="post" action="/login">
          <input id="email" name="email" type="email" placeholder="Email">
          <input id="password" name="password" type="password" placeholder="Password">
          <button id="submit" type="submit">Sign in</button>
        </form>
        <a id="foreign" href="http://localhost:{{c.Request.Host.Port}}/foreign">Somewhere else</a>
        <a id="foreign-tab" href="http://localhost:{{c.Request.Host.Port}}/foreign-tab" target="_blank">Somewhere else, new tab</a>
        <a id="help-tab" href="/help" target="_blank">Help, new tab</a>
        <p id="error">{{error}}</p>
        </body></html>
        """;

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private sealed record User(string Email, string Password, string AccountId, string DisplayName);
}
