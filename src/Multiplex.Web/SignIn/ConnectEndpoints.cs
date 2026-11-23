using Microsoft.AspNetCore.Antiforgery;
using Multiplex.Web.Sites;

namespace Multiplex.Web.SignIn;

/// <summary>
/// The connect pages, reached from <c>/oauth/authorize</c> as <c>/connect/{slug}?p={pendingId}</c>, after the
/// person signed in to Multiplex there. Each pending authorization is one connection being added by that
/// person, and only they may go on with it. For a service that acts on a site account: live sign-in to the
/// site, then back to the client with a code bound to the account signed in to here; no account the server
/// holds a session for is ever offered or selectable (#52). For a service that uses no site account: one
/// "Allow" and back to the client.
/// </summary>
public static class ConnectEndpoints
{
    public static IEndpointRouteBuilder MapConnectPages(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/connect/{slug}");
        group.MapGet("", ShowAsync);
        group.MapPost("/start", StartAsync);
        group.MapPost("/allow", AllowAsync);
        group.MapGet("/live", LiveAsync);
        group.MapGet("/done", DoneAsync);
        group.MapPost("/cancel", CancelAsync);
        return endpoints;
    }

    // ---------------------------------------------------------------- GET /connect/{slug}?p=

    private static async Task ShowAsync(HttpContext context, string slug, string? p)
    {
        if (await ResolveAsync(context, slug, p) is not ({ } pending, var site, { } person)) return;
        await WriteHtmlAsync(context, 200, nonce => site is null
            ? ConnectHtml.Consent(nonce, pending, person.Email, Token(context))
            : ConnectHtml.SignIn(nonce, site, pending, person.Email, Token(context)));
    }

    // ---------------------------------------------------------------- POST /connect/{slug}/start?p=

    private static async Task StartAsync(HttpContext context, string slug, string? p)
    {
        if (await ResolveAsync(context, slug, p) is not ({ } pending, var site, { } person)) return;
        if (!await ValidAntiforgeryAsync(context)) return;
        if (site is null)
        {
            await WriteHtmlAsync(context, 404, nonce => ConnectHtml.Message(nonce, "Not found", "This service has no sign-in to start."));
            return;
        }
        await StartLiveAsync(context, site, pending.Id, person.Owner);
    }

    // ---------------------------------------------------------------- POST /connect/{slug}/allow?p=

    private static async Task AllowAsync(HttpContext context, string slug, string? p)
    {
        if (await ResolveAsync(context, slug, p) is not ({ } pending, var site, { } person)) return;
        if (!await ValidAntiforgeryAsync(context)) return;
        if (site is not null)
        {
            // A service that acts on a site account is connected only by signing in to the site.
            await WriteHtmlAsync(context, 400, nonce => ConnectHtml.Message(nonce, "Sign in first",
                $"Connecting {site.DisplayName} needs a sign-in to {site.DisplayName}.", $"/connect/{site.Slug}?p={Uri.EscapeDataString(pending.Id)}", "Back"));
            return;
        }

        Uri redirect;
        try
        {
            redirect = await context.RequestServices.GetRequiredService<IAuthorizationCompleter>()
                .CompleteAsync(pending.Id, person.Owner, account: null, context.RequestAborted);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            Logger(context).LogWarning("Could not complete the connection to {Site}: {Error}", slug, ex.GetType().Name);
            await LinkExpiredAsync(context);
            return;
        }
        Logger(context).LogInformation("Connect for {Site} completed after consent.", pending.Site);
        SeeOther(context, redirect.AbsoluteUri);
    }

    // ---------------------------------------------------------------- GET /connect/{slug}/live?a=  (page or WebSocket)

    private static async Task LiveAsync(HttpContext context, string slug, string? a)
    {
        if (await FindAttemptAsync(context, slug, a) is not { } attempt) return;

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
        if (await FindAttemptAsync(context, slug, a) is not { } attempt) return;
        if (context.RequestServices.GetService<IAuthorizationCompleter>() is not { } completer)
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
        var person = PersonOf(context);
        Uri? redirect = null;

        if (services.GetRequiredService<LiveSignInRegistry>().Find(a) is { } attempt
            && string.Equals(attempt.Slug, slug, StringComparison.OrdinalIgnoreCase))
        {
            if (person?.Owner != attempt.Owner)
            {
                await NotYoursAsync(context);
                return;
            }
            redirect = await attempt.CancelAsync(completer);
        }
        else if (!string.IsNullOrEmpty(p) && completer is not null)
        {
            var pending = await completer.FindAsync(p, context.RequestAborted);
            if (pending is not null && string.Equals(pending.Site, slug, StringComparison.OrdinalIgnoreCase))
            {
                if (person?.Owner != pending.Owner)
                {
                    await NotYoursAsync(context);
                    return;
                }
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

    /// <summary>The person signed in to Multiplex on this request, by their sign-in cookie (the access gate admits no one else here).</summary>
    private static Identity.MultiplexPerson? PersonOf(HttpContext context) => Identity.MultiplexIdentity.PersonOf(context.User);

    private static async Task StartLiveAsync(HttpContext context, ISiteSignInDefinition site, string pendingId, string owner)
    {
        var attempt = context.RequestServices.GetRequiredService<LiveSignInRegistry>().StartOrResume(site, pendingId, owner);
        if (attempt is null)
        {
            await WriteHtmlAsync(context, 503, nonce => ConnectHtml.Busy(nonce, site, pendingId, Token(context)));
            return;
        }
        SeeOther(context, $"/connect/{site.Slug}/live?a={Uri.EscapeDataString(attempt.Id)}");
    }

    /// <summary>
    /// Finds the pending authorization, the site's sign-in (null for a service that uses no site account) and
    /// the signed-in person, who must be the one that started it. On failure writes the page and returns null.
    /// </summary>
    private static async Task<(PendingAuthorization Pending, ISiteSignInDefinition? Site, Identity.MultiplexPerson Person)?> ResolveAsync(
        HttpContext context, string slug, string? pendingId)
    {
        var services = context.RequestServices;
        if (PersonOf(context) is not { } person)
        {
            await NotYoursAsync(context);
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
        if (pending is null || pending.ExpiresAt <= now || !string.Equals(pending.Site, slug, StringComparison.OrdinalIgnoreCase))
        {
            await LinkExpiredAsync(context);
            return null;
        }
        if (!string.Equals(pending.Owner, person.Owner, StringComparison.Ordinal))
        {
            await NotYoursAsync(context);
            return null;
        }

        var site = services.GetServices<ISiteSignInDefinition>()
            .FirstOrDefault(d => string.Equals(d.Slug, pending.Site, StringComparison.OrdinalIgnoreCase));
        return (pending, site, person);
    }

    /// <summary>The live sign-in, when it exists for this slug and belongs to the signed-in person; otherwise writes the page.</summary>
    private static async Task<LiveSignInAttempt?> FindAttemptAsync(HttpContext context, string slug, string? attemptId)
    {
        var attempt = context.RequestServices.GetRequiredService<LiveSignInRegistry>().Find(attemptId);
        if (attempt is null || !string.Equals(attempt.Slug, slug, StringComparison.OrdinalIgnoreCase))
        {
            await WriteHtmlAsync(context, 404, nonce => ConnectHtml.Message(nonce, "Sign-in ended",
                "This sign-in has ended. Go back to the app and connect again."));
            return null;
        }
        if (PersonOf(context)?.Owner != attempt.Owner)
        {
            await NotYoursAsync(context);
            return null;
        }
        return attempt;
    }

    internal static async Task<bool> ValidAntiforgeryAsync(HttpContext context)
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

    internal static FormToken Token(HttpContext context)
    {
        var tokens = context.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(context);
        return new FormToken(tokens.FormFieldName, tokens.RequestToken ?? "");
    }

    private static Task LinkExpiredAsync(HttpContext context) =>
        WriteHtmlAsync(context, 404, nonce => ConnectHtml.Message(nonce, "Link expired",
            "This sign-in link has expired or is not valid. Go back to the app and try connecting again."));

    private static Task NotYoursAsync(HttpContext context) =>
        WriteHtmlAsync(context, 403, nonce => ConnectHtml.Message(nonce, "Not allowed",
            "This connection was started by a different sign-in to Multiplex. Go back to the app and connect again."));

    private static Task NoAuthorizationServerAsync(HttpContext context) =>
        WriteHtmlAsync(context, 503, nonce => ConnectHtml.Message(nonce, "Not available",
            "Connecting is not available on this server yet (its authorization server is not configured)."));

    /// <summary>The WebSocket must come from a page on this server (no cross-site WebSocket hijacking).</summary>
    private static bool SameOrigin(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin)) return true; // not a browser; the sign-in cookie and owner check still apply
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

    private static ILogger Logger(HttpContext context) =>
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Multiplex.Web.SignIn.Connect");

    private static void SeeOther(HttpContext context, string location)
    {
        context.Response.StatusCode = StatusCodes.Status303SeeOther;
        context.Response.Headers.Location = location;
        context.Response.Headers.CacheControl = "no-store";
    }

    /// <summary>Writes one of the self-contained HTML pages with a per-response CSP nonce. Also used by Identity/.</summary>
    internal static async Task WriteHtmlAsync(HttpContext context, int status, Func<string, string> render)
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
