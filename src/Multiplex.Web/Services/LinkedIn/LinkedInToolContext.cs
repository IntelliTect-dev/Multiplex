using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol;
using Multiplex.Web.Services.TripIt;
using Multiplex.Web.Sites;

namespace Multiplex.Web.Services.LinkedIn;

/// <summary>
/// Per-request tool plumbing (scoped): finds the LinkedIn account the validated request acts for,
/// gets that account's <see cref="ILinkedInApi"/>, runs the tool, and turns failures into short
/// tool errors. Changes go through <see cref="RunConfirmedAsync"/>: preview first, act only with a
/// confirm token for exactly the same request.
/// </summary>
public sealed class LinkedInToolContext(
    IHttpContextAccessor httpContext,
    IServiceProvider requestServices,
    ILinkedInApiFactory apis,
    ConfirmTokens confirmTokens,
    ILoggerFactory loggers)
{
    public const string Site = "linkedin";

    /// <summary>Shown when the person is signed out of LinkedIn: the client must sign in again.</summary>
    public const string ReconnectMessage =
        "Your LinkedIn session has ended (you are signed out of LinkedIn). Reconnect the LinkedIn connector " +
        "(in Claude or ChatGPT, open the connector and choose Connect) to sign in again, then retry.";

    // Shared by every request: complete and read-only before first use, so no two requests race to finish it.
    internal static readonly JsonSerializerOptions Json = CreateJson();

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        options.MakeReadOnly();
        return options;
    }

    private readonly ILogger _log = loggers.CreateLogger("Multiplex.Services.LinkedIn.Tools");
    private ILinkedInApi? _api;

    /// <summary>The LinkedIn account the authenticated request acts for.</summary>
    public string AccountId
    {
        get
        {
            var account = SiteClaims.AccountOf(httpContext.HttpContext?.User);
            if (account is not { } a || !string.Equals(a.Site, Site, StringComparison.Ordinal))
            {
                // Auth/ authenticates every /linkedin/mcp request before it gets here.
                throw new McpException("This request is not signed in to a LinkedIn account. Reconnect the LinkedIn connector.");
            }
            return a.AccountId;
        }
    }

    private string ClientId => httpContext.HttpContext?.User.FindFirst(SiteClaims.ClientId)?.Value ?? "(unknown)";

    public static string ToJson(object value) => JsonSerializer.Serialize(value, Json);

    /// <summary>Runs a tool for the request's account, mapping failures to short tool errors.</summary>
    public async Task<string> RunAsync(string tool, Func<ILinkedInApi, Task<string>> work, CancellationToken ct)
    {
        try
        {
            _api ??= await apis.CreateAsync(AccountId, requestServices, ct).ConfigureAwait(false);
            return await work(_api).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw ToToolError(tool, ex);
        }
    }

    /// <summary>
    /// Runs a change with the confirm step. <paramref name="prepare"/> checks the request and
    /// describes it; it returns null text when there is nothing to do (for example, already
    /// following), and then nothing is issued or performed. Without <paramref name="confirmToken"/>
    /// the tool only previews and returns a token bound to account, tool and
    /// <paramref name="arguments"/>; with a valid, unused token for the same request it performs
    /// the change. The token is used up before the change is attempted, so a change whose outcome
    /// is unknown is never sent twice with one token.
    /// </summary>
    public Task<string> RunConfirmedAsync<T>(string tool, string? confirmToken, IReadOnlyDictionary<string, string?> arguments,
        Func<ILinkedInApi, Task<Prepared<T>>> prepare, Func<ILinkedInApi, T, Task<string>> perform, CancellationToken ct)
        => RunAsync(tool, async api =>
        {
            var accountId = AccountId;
            var prepared = await prepare(api).ConfigureAwait(false);
            if (prepared.NothingToDo is not null || prepared.State is not { } state)
            {
                return ToJson(new { nothing_changed = true, result = prepared.NothingToDo ?? "Nothing to do." });
            }
            if (string.IsNullOrWhiteSpace(confirmToken))
            {
                return ToJson(new
                {
                    confirmation_required = true,
                    nothing_changed = true,
                    preview = prepared.Preview,
                    confirm_token = confirmTokens.Issue(accountId, tool, arguments),
                    expires_in_seconds = (int)ConfirmTokens.Lifetime.TotalSeconds,
                    next_step = $"Show the preview to the person and ask whether to go ahead. Only if they agree, call {tool} " +
                                "again with exactly the same arguments plus this confirm_token.",
                });
            }
            confirmTokens.Redeem(confirmToken, accountId, tool, arguments);
            var result = await perform(api, state).ConfigureAwait(false);
            _log.LogInformation("LinkedIn {Tool} performed for account {Account} (client {ClientId}).", tool, accountId, ClientId);
            return result;
        }, ct);

    private McpException ToToolError(string tool, Exception ex)
    {
        switch (ex)
        {
            case McpException mcp:
                return mcp;
            case LinkedInSessionEndedException:
                return new McpException(ReconnectMessage);
            case LinkedInChallengeException challenge:
                _log.LogWarning("LinkedIn {Tool} for account {Account} was stopped by a security check or block.", tool, SafeAccount());
                return new McpException(challenge.Message);
            case LinkedInApiException api:
                _log.LogDebug(ex, "LinkedIn {Tool} failed for account {Account}.", tool, SafeAccount());
                return new McpException(api.Message);
        }
        _log.LogError(ex, "LinkedIn {Tool} failed unexpectedly for account {Account}.", tool, SafeAccount());
        return new McpException($"{tool} failed unexpectedly; the details are in the Multiplex server log.");
    }

    private string SafeAccount() => SiteClaims.AccountOf(httpContext.HttpContext?.User)?.AccountId ?? "(none)";

}

/// <summary>
/// What a change will do (<see cref="Preview"/>) and what it found while checking
/// (<see cref="State"/>, handed to the change itself), or why there is nothing to do (<see cref="NothingToDo"/>).
/// </summary>
public sealed record Prepared<T>(T? State, string? Preview, string? NothingToDo);

public static class Prepared
{
    public static Prepared<T> Change<T>(T state, string preview) => new(state, preview, null);

    public static Prepared<T> Nothing<T>(string why) => new(default, null, why);
}
