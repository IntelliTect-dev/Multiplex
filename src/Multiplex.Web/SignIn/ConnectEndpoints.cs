using Microsoft.AspNetCore.Antiforgery;
using Multiplex.Web.Sites;

namespace Multiplex.Web.SignIn;

/// <summary>
/// The connect pages, reached from <c>/oauth/authorize</c> as <c>/connect/{slug}?p={pendingId}</c>:
/// passcode gate → live sign-in → back to the client with a code or an error.
/// Each pending authorization is one connection being added, bound to the account signed in to
/// here. The passcode only lets a person reach the sign-in: every connection proves its account by
/// signing in, and no account the server holds a session for is ever offered or selectable (#52).
/// </summary>
public static class ConnectEndpoints
{
    public static IEndpointRouteBuilder MapConnectPages(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/connect/{slug}");
        group.MapGet("", ShowAsync);
        group.MapPost("", PasscodeAsync);
        group.MapPost("/start", StartAsync);
        group.MapGet("/live", LiveAsync);
        group.MapGet("/done", DoneAsync);
        group.MapPost("/cancel", CancelAsync);
        return endpoints;
    }

    // ---------------------------------------------------------------- GET /connect/{slug}?p=

    private static async Task ShowAsync(HttpContext context, string slug, string? p)
    {
        if (await ResolveAsync(context, slug, p) is not ({ } site, { } pending)) return;
        var services = context.RequestServices;
        var gate = services.GetRequiredService<PasscodeGate>();

        if (!gate.IsConfigured) { await NotConfiguredAsync(context); return; }

        if (services.GetRequiredService<GateCookie>().HasPassed(context, site.Slug, pending.Id))
        {
            await WriteHtmlAsync(context, 200, nonce => ConnectHtml.SignIn(nonce, site, pending, Token(context)));
            return;
        }

        await WriteHtmlAsync(context, 200, nonce => ConnectHtml.Gate(nonce, site, pending, Token(context), null));
    }

    // ---------------------------------------------------------------- POST /connect/{slug}?p=  (passcode)

    private static async Task PasscodeAsync(HttpContext context, string slug, string? p)
    {
        if (await ResolveAsync(context, slug, p) is not ({ } site, { } pending)) return;
        var services = context.RequestServices;
        var gate = services.GetRequiredService<PasscodeGate>();
        if (!gate.IsConfigured) { await NotConfiguredAsync(context); return; }
        if (!await ValidAntiforgeryAsync(context)) return;

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var result = gate.Check(pending.Id, ClientIp(context), form["passcode"]);
        switch (result)
        {
            case GateResult.NotConfigured:
                await NotConfiguredAsync(context);
                return;
            case GateResult.LockedOut:
                await LockedOutAsync(context);
                return;
            case GateResult.Wrong:
                await WriteHtmlAsync(context, 401, nonce => ConnectHtml.Gate(nonce, site, pending, Token(context), "That passcode is not right. Try again."));
                return;
        }

        services.GetRequiredService<GateCookie>().Issue(context, site.Slug, pending.Id);
        await StartLiveAsync(context, site, pending.Id);
    }

    // ---------------------------------------------------------------- POST /connect/{slug}/start?p=

    private static async Task StartAsync(HttpContext context, string slug, string? p)
    {
        if (await ResolveAsync(context, slug, p) is not ({ } site, { } pending)) return;
        if (!await ValidAntiforgeryAsync(context) || !await RequireGateAsync(context, site, pending.Id)) return;
        await StartLiveAsync(context, site, pending.Id);
    }

    // ---------------------------------------------------------------- GET /connect/{slug}/live?a=  (page or WebSocket)

    private static async Task LiveAsync(HttpContext context, string slug, string? a)
    {
        var services = context.RequestServices;
        var attempt = services.GetRequiredService<LiveSignInRegistry>().Find(a);
        if (attempt is null || !string.Equals(attempt.Slug, slug, StringComparison.OrdinalIgnoreCase))
        {
            await WriteHtmlAsync(context, 404, nonce => ConnectHtml.Message(nonce, "Sign-in ended",
                "This sign-in has ended. Go back to the app and connect again."));
            return;
        }
        if (!services.GetRequiredService<GateCookie>().HasPassed(context, attempt.Slug, attempt.PendingId))
        {
            await WriteHtmlAsync(context, 403, nonce => ConnectHtml.Message(nonce, "Not allowed",
                "Open this sign-in from the link the app gave you and enter the passcode first."));
            return;
        }

        if (context.WebSockets.IsWebSocketRequest)
        {
            if (!SameOrigin(context))
            {
                // Logged loudly: behind a tunnel that rewrites Host this would otherwise look like an endless "Reconnecting…".
                Logger(context).LogWarning(
                    "Refused the live sign-in WebSocket: Origin {Origin} matches neither Host {Host}, X-Forwarded-Host nor Multiplex:PublicBaseUrl.",
                    context.Request.Headers.Origin.ToString(), context.Request.Host.Value);
                context.Response.StatusCode = 403;
                return;
            }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await attempt.ServeClientAsync(socket, context.RequestAborted);
            return;
        }

        await WriteHtmlAsync(context, 200, nonce => ConnectHtml.Live(nonce, attempt, Token(context)));
    }

    // ---------------------------------------------------------------- GET /connect/{slug}/done?a=

    private static async Task DoneAsync(HttpContext context, string slug, string? a)
    {
        var services = context.RequestServices;
        var attempt = services.GetRequiredService<LiveSignInRegistry>().Find(a);
        if (attempt is null || !string.Equals(attempt.Slug, slug, StringComparison.OrdinalIgnoreCase)
            || !services.GetRequiredService<GateCookie>().HasPassed(context, attempt.Slug, attempt.PendingId))
        {
            await WriteHtmlAsync(context, 404, nonce => ConnectHtml.Message(nonce, "Sign-in ended",
                "This sign-in has ended. Go back to the app and connect again."));
            return;
        }
        if (services.GetService<IAuthorizationCompleter>() is not { } completer)
        {
            await NoAuthorizationServerAsync(context);
            return;
        }

        var redirect = await attempt.CompleteAsync(completer, context.RequestAborted) ?? attempt.ReturnUrl;
        if (redirect is null)
        {
            context.Response.Redirect($"/connect/{attempt.Slug}/live?a={Uri.EscapeDataString(attempt.Id)}");
            return;
        }
        Logger(context).LogInformation("Connect for {Site} completed after a live sign-in.", attempt.Slug);
        context.Response.Redirect(redirect.AbsoluteUri);
    }

    // ---------------------------------------------------------------- POST /connect/{slug}/cancel?a= | ?p=

    private static async Task CancelAsync(HttpContext context, string slug, string? a, string? p)
    {
        if (!await ValidAntiforgeryAsync(context)) return;
        var services = context.RequestServices;
        var completer = services.GetService<IAuthorizationCompleter>();
        Uri? redirect = null;

        if (services.GetRequiredService<LiveSignInRegistry>().Find(a) is { } attempt
            && string.Equals(attempt.Slug, slug, StringComparison.OrdinalIgnoreCase))
        {
            if (!await RequireGateAsync(context, attempt.Definition, attempt.PendingId)) return;
            redirect = await attempt.CancelAsync(completer);
        }
        else if (!string.IsNullOrEmpty(p) && completer is not null)
        {
            // Cancel before or instead of a live sign-in: the pending id itself is the capability.
            var pending = await completer.FindAsync(p, context.RequestAborted);
            if (pending is not null && string.Equals(pending.Site, slug, StringComparison.OrdinalIgnoreCase))
            {
                if (services.GetRequiredService<LiveSignInRegistry>().FindForPending(p) is { } running)
                    await running.CancelAsync(null);
                redirect = await completer.FailAsync(p, "access_denied", "Sign-in cancelled", context.RequestAborted);
            }
        }

        if (redirect is not null)
        {
            SeeOther(context, redirect.AbsoluteUri);
            return;
        }
        await WriteHtmlAsync(context, 200, nonce => ConnectHtml.Message(nonce, "Cancelled", "Sign-in cancelled. You can close this page."));
    }

    // ---------------------------------------------------------------- helpers

    private static async Task StartLiveAsync(HttpContext context, ISiteSignInDefinition site, string pendingId)
    {
        var attempt = context.RequestServices.GetRequiredService<LiveSignInRegistry>().StartOrResume(site, pendingId);
        if (attempt is null)
        {
            await WriteHtmlAsync(context, 503, nonce => ConnectHtml.Busy(nonce, site, pendingId, Token(context)));
            return;
        }
        SeeOther(context, $"/connect/{site.Slug}/live?a={Uri.EscapeDataString(attempt.Id)}");
    }

    /// <summary>Finds the site and the pending authorization; on failure writes the friendly error page and returns null.</summary>
    private static async Task<(ISiteSignInDefinition Site, PendingAuthorization Pending)?> ResolveAsync(
        HttpContext context, string slug, string? pendingId)
    {
        var services = context.RequestServices;
        var site = services.GetServices<ISiteSignInDefinition>()
            .FirstOrDefault(d => string.Equals(d.Slug, slug, StringComparison.OrdinalIgnoreCase));
        if (site is null)
        {
            await WriteHtmlAsync(context, 404, nonce => ConnectHtml.Message(nonce, "Not found", "There is no such sign-in on this server."));
            return null;
        }

        if (services.GetService<IAuthorizationCompleter>() is not { } completer)
        {
            await NoAuthorizationServerAsync(context);
            return null;
        }

        if (string.IsNullOrEmpty(pendingId))
        {
            await WriteHtmlAsync(context, 400, nonce => ConnectHtml.Message(nonce, "Link incomplete",
                "This sign-in link is incomplete. Go back to the app and try connecting again."));
            return null;
        }

        var pending = await completer.FindAsync(pendingId, context.RequestAborted);
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        if (pending is null || pending.ExpiresAt <= now || !string.Equals(pending.Site, site.Slug, StringComparison.OrdinalIgnoreCase))
        {
            await WriteHtmlAsync(context, 404, nonce => ConnectHtml.Message(nonce, "Link expired",
                "This sign-in link has expired or is not valid. Go back to the app and try connecting again."));
            return null;
        }

        return (site, pending);
    }

    private static async Task<bool> RequireGateAsync(HttpContext context, ISiteSignInDefinition site, string pendingId)
    {
        if (context.RequestServices.GetRequiredService<GateCookie>().HasPassed(context, site.Slug, pendingId)) return true;
        await WriteHtmlAsync(context, 403, nonce => ConnectHtml.Message(nonce, "Passcode needed",
            "Enter the passcode first.", $"/connect/{site.Slug}?p={Uri.EscapeDataString(pendingId)}", "Enter passcode"));
        return false;
    }

    private static async Task<bool> ValidAntiforgeryAsync(HttpContext context)
    {
        try
        {
            await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            await WriteHtmlAsync(context, 400, nonce => ConnectHtml.Message(nonce, "Page expired",
                "This page expired. Go back, reload it and try again."));
            return false;
        }
    }

    private static FormToken Token(HttpContext context)
    {
        var tokens = context.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(context);
        return new FormToken(tokens.FormFieldName, tokens.RequestToken ?? "");
    }

    private static Task NotConfiguredAsync(HttpContext context) =>
        WriteHtmlAsync(context, 503, nonce => ConnectHtml.Message(nonce, "Not set up yet",
            "This server is not ready for connections: its owner has not set a connect passcode yet. " +
            "The owner needs to run the install script, which sets Multiplex:Gate:PasscodeHash."));

    private static Task LockedOutAsync(HttpContext context) =>
        WriteHtmlAsync(context, 429, nonce => ConnectHtml.Message(nonce, "Too many tries",
            "Too many wrong passcodes. Wait 15 minutes, then try again."));

    private static Task NoAuthorizationServerAsync(HttpContext context) =>
        WriteHtmlAsync(context, 503, nonce => ConnectHtml.Message(nonce, "Not available",
            "Connecting is not available on this server yet (its authorization server is not configured)."));

    /// <summary>The WebSocket must come from a page on this server (no cross-site WebSocket hijacking).</summary>
    private static bool SameOrigin(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin)) return true; // not a browser; the gate cookie still applies
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)) return false;
        var originAuthority = originUri.Authority;

        var config = context.RequestServices.GetRequiredService<IConfiguration>();
        IEnumerable<string?> candidates =
        [
            context.Request.Host.Value,
            context.Request.Headers["X-Forwarded-Host"].ToString(),
            AuthorityOf(config["Multiplex:BaseUrl"]),
            AuthorityOf(config["Multiplex:PublicBaseUrl"]),
        ];
        return candidates.Any(c => !string.IsNullOrEmpty(c) && string.Equals(c, originAuthority, StringComparison.OrdinalIgnoreCase));
    }

    private static string? AuthorityOf(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Authority : null;

    /// <summary>
    /// The connecting address. Behind a tunnel this is the tunnel's local end (usually loopback),
    /// so the per-address lockout then covers everyone; forwarded headers are not trusted for it.
    /// </summary>
    private static string ClientIp(HttpContext context) => RequestOrigin.ClientAddress(context);

    private static ILogger Logger(HttpContext context) =>
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Multiplex.Web.SignIn.Connect");

    private static void SeeOther(HttpContext context, string location)
    {
        context.Response.StatusCode = StatusCodes.Status303SeeOther;
        context.Response.Headers.Location = location;
        context.Response.Headers.CacheControl = "no-store";
    }

    private static async Task WriteHtmlAsync(HttpContext context, int status, Func<string, string> render)
    {
        var nonce = ConnectHtml.NewNonce();
        var headers = context.Response.Headers;
        // Set before the antiforgery token is generated, so it does not add its own SAMEORIGIN.
        headers.XFrameOptions = "DENY";
        headers.ContentSecurityPolicy =
            $"default-src 'none'; script-src 'nonce-{nonce}'; style-src 'nonce-{nonce}'; img-src 'self' blob: data:; " +
            "connect-src 'self' wss: ws:; base-uri 'none'; frame-ancestors 'none'";
        headers["Referrer-Policy"] = "no-referrer";
        headers.XContentTypeOptions = "nosniff";
        var html = render(nonce);
        headers.CacheControl = "no-store";
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html, context.RequestAborted);
    }
}
