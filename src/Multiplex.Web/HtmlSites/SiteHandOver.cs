using Multiplex.Web.SignIn;

namespace Multiplex.Web.HtmlSites;

/// <summary>What the hand-over page shows for one open robot check.</summary>
public sealed record HandOverView(string Id, DateTimeOffset ExpiresAt, bool NeedsTyping, int WrongAnswers);

public sealed record HandOverPicture(byte[] Data, string MimeType);

public enum HandOverOutcome
{
    /// <summary>The site accepted the answer; its requests are open again.</summary>
    Passed,
    /// <summary>The site asked again; the person may try once more.</summary>
    WrongAnswer,
    /// <summary>The check is over without a pass (too many wrong answers, or the site refused); the service is paused.</summary>
    Closed,
    /// <summary>The answer could not be sent just now; the check is still open.</summary>
    NotSent,
    /// <summary>No such check: the link expired or was already used.</summary>
    Gone,
}

public sealed record HandOverResult(HandOverOutcome Outcome, string Message);

/// <summary>
/// A service whose website sometimes asks for a person: it holds at most one open robot check,
/// and a signed-in person answers it on the <c>/check/{slug}/{id}</c> page. Nothing in Multiplex
/// answers for them; the service only passes on what the person presses or types.
/// </summary>
public interface ISiteHandOver
{
    /// <summary>The service's address part, e.g. <c>amazon</c>.</summary>
    string Slug { get; }

    /// <summary>The website's name as the person knows it, e.g. <c>Amazon</c>.</summary>
    string Site { get; }

    /// <summary>The open check with this id, or null when there is none (expired, answered, or never was).</summary>
    HandOverView? Find(string id);

    /// <summary>The picture the person reads the characters from, fetched by the service; null when the check has none.</summary>
    Task<HandOverPicture?> PictureAsync(string id, CancellationToken ct);

    /// <summary>Sends the person's answer to the website, in the service's own session, and says what came of it.</summary>
    Task<HandOverResult> AnswerAsync(string id, string? typed, CancellationToken ct);
}

/// <summary>
/// The hand-over pages. Like every Multiplex page they need a person signed in to Multiplex
/// (the access gate sees to that before a request gets here), and the answer form carries an
/// antiforgery token.
/// </summary>
public static class SiteHandOverEndpoints
{
    public static IEndpointRouteBuilder MapSiteHandOverPages(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/check/{slug}/{id}");
        group.MapGet("", ShowAsync);
        group.MapGet("/picture", PictureAsync);
        group.MapPost("", AnswerAsync);
        return endpoints;
    }

    private static ISiteHandOver? Service(HttpContext context, string slug) =>
        context.RequestServices.GetServices<ISiteHandOver>().FirstOrDefault(s => s.Slug == slug);

    private static async Task ShowAsync(HttpContext context, string slug, string id)
    {
        if (Service(context, slug) is not { } service || service.Find(id) is not { } check)
        {
            await GoneAsync(context);
            return;
        }
        await ConnectEndpoints.WriteHtmlAsync(context, 200, nonce =>
            ConnectHtml.RobotCheck(nonce, service.Slug, service.Site, check, ConnectEndpoints.Token(context), note: null));
    }

    private static async Task PictureAsync(HttpContext context, string slug, string id)
    {
        var picture = Service(context, slug) is { } service ? await service.PictureAsync(id, context.RequestAborted) : null;
        if (picture is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        // Served as a picture and nothing else: no sniffing, and no script even if someone opens it directly.
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentType = picture.MimeType;
        await context.Response.Body.WriteAsync(picture.Data, context.RequestAborted);
    }

    private static async Task AnswerAsync(HttpContext context, string slug, string id)
    {
        if (Service(context, slug) is not { } service)
        {
            await GoneAsync(context);
            return;
        }
        if (!await ConnectEndpoints.ValidAntiforgeryAsync(context)) return;

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        // Not the request's token: once a person has answered, closing the page must not lose what the site says.
        var result = await service.AnswerAsync(id, form["answer"].ToString(), CancellationToken.None);
        switch (result.Outcome)
        {
            case HandOverOutcome.Passed:
                await ConnectEndpoints.WriteHtmlAsync(context, 200, nonce => ConnectHtml.Message(nonce, "Done",
                    $"{service.Site} accepted it. Go back to your chat and ask it to try again."));
                break;
            case HandOverOutcome.WrongAnswer or HandOverOutcome.NotSent when service.Find(id) is { } check:
                await ConnectEndpoints.WriteHtmlAsync(context, 200, nonce =>
                    ConnectHtml.RobotCheck(nonce, service.Slug, service.Site, check, ConnectEndpoints.Token(context), result.Message));
                break;
            case HandOverOutcome.Gone:
                await GoneAsync(context);
                break;
            default:
                await ConnectEndpoints.WriteHtmlAsync(context, 200, nonce => ConnectHtml.Message(nonce, "Not accepted", result.Message));
                break;
        }
    }

    private static Task GoneAsync(HttpContext context) =>
        ConnectEndpoints.WriteHtmlAsync(context, 404, nonce => ConnectHtml.Message(nonce, "Nothing to answer",
            "This link has expired, or the check was already answered. Go back to your chat and ask it to try again."));
}
