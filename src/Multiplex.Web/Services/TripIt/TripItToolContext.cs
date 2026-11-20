using ModelContextProtocol;
using Multiplex.Web.Sites;
using TripIt;
using TripIt.Tools;

namespace Multiplex.Web.Services.TripIt;

/// <summary>
/// Per-request tool plumbing (scoped): finds the account the validated request acts for,
/// builds that account's <see cref="TripItClient"/>, runs the tool, and turns failures into
/// short tool errors. Session renewal happens per TripIt request inside the client, so a
/// write that already succeeded is never repeated.
/// </summary>
public sealed class TripItToolContext(
    IHttpContextAccessor httpContext,
    IServiceProvider requestServices,
    IHttpClientFactory httpClients,
    TripItAccountSessions sessions,
    ConfirmTokens confirmTokens,
    ILoggerFactory loggers)
{
    /// <summary>Shown when the person is signed out of TripIt: the client must sign in again.</summary>
    public const string ReconnectMessage =
        "Your TripIt session has ended (you are signed out of TripIt). Reconnect the TripIt connector " +
        "(in Claude or ChatGPT, open the connector and choose Connect) to sign in again, then retry.";

    private readonly ILogger _log = loggers.CreateLogger("Multiplex.Services.TripIt.Tools");
    private TripItClient? _client;

    /// <summary>
    /// The person and TripIt account the authenticated request acts for. Only that person's own session
    /// for the account is ever used, even when another person connected the same TripIt account.
    /// </summary>
    public (string Owner, string AccountId) Account
    {
        get
        {
            var account = SiteClaims.AccountOf(httpContext.HttpContext?.User);
            if (account is not { } a || !string.Equals(a.Site, TripItAccountSessions.Site, StringComparison.Ordinal))
            {
                // The access gate authenticates every /tripit/mcp request before it gets here.
                throw new McpException("This request is not signed in to a TripIt account. Reconnect the TripIt connector.");
            }
            return (a.Owner, a.AccountId);
        }
    }

    /// <summary>The TripIt account the authenticated request acts for.</summary>
    public string AccountId => Account.AccountId;

    /// <summary>What confirm tokens are bound to: the person and the account, so one person's token never works for another.</summary>
    private string ConfirmKey => $"{Account.Owner}/{Account.AccountId}";

    private string ClientId => httpContext.HttpContext?.User.FindFirst(SiteClaims.ClientId)?.Value ?? "(unknown)";

    /// <summary>The account's client for this request, created on first use.</summary>
    public async Task<TripItClient> ClientAsync(CancellationToken ct)
    {
        if (_client is not null) return _client;
        var (owner, accountId) = Account;
        var snapshot = await sessions.LoadAsync(requestServices, owner, accountId, ct).ConfigureAwait(false);
        var provider = new SnapshotSessionProvider(snapshot,
            (rejected, c) => sessions.RenewAsync(requestServices, owner, accountId, rejected, c));
        _client = new TripItClient(provider, httpClients.CreateClient(TripItServiceRegistration.HttpClientName),
            loggers.CreateLogger<TripItClient>(), pacingKey: accountId);
        return _client;
    }

    /// <summary>Runs a tool for the request's account, mapping failures to short tool errors.</summary>
    public async Task<string> RunAsync(string tool, Func<TripItOperations, Task<string>> work, CancellationToken ct)
    {
        try
        {
            var client = await ClientAsync(ct).ConfigureAwait(false);
            return await work(new TripItOperations(client)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw ToToolError(tool, ex);
        }
    }

    /// <summary>
    /// Runs a risky tool with the confirm step. Without <paramref name="confirmToken"/> it only
    /// previews (changes nothing) and returns a token bound to account, tool and
    /// <paramref name="arguments"/>; with a valid, unused token for the same request it performs
    /// it. The token is used up before the change is attempted, so even a failed attempt needs
    /// a fresh preview: a change whose outcome is unknown is never replayed with the same token.
    /// </summary>
    public Task<string> RunConfirmedAsync(string tool, string? confirmToken, IReadOnlyDictionary<string, string?> arguments,
        Func<TripItOperations, Task<string>> preview, Func<TripItOperations, Task<string>> perform, CancellationToken ct)
        => RunAsync(tool, async ops =>
        {
            var accountId = AccountId;
            if (string.IsNullOrWhiteSpace(confirmToken))
            {
                var text = await preview(ops).ConfigureAwait(false);
                return TripItOperations.ToJson(new
                {
                    confirmation_required = true,
                    nothing_changed = true,
                    preview = text,
                    confirm_token = confirmTokens.Issue(ConfirmKey, tool, arguments),
                    expires_in_seconds = (int)ConfirmTokens.Lifetime.TotalSeconds,
                    next_step = $"Show the preview to the person and ask whether to go ahead. Only if they agree, call {tool} " +
                                "again with exactly the same arguments plus this confirm_token.",
                });
            }
            confirmTokens.Redeem(confirmToken, ConfirmKey, tool, arguments);
            var result = await perform(ops).ConfigureAwait(false);
            _log.LogInformation("TripIt {Tool} performed for account {Account} (client {ClientId}).", tool, accountId, ClientId);
            return result;
        }, ct);

    private McpException ToToolError(string tool, Exception ex)
    {
        switch (ex)
        {
            case McpException mcp:
                return mcp;
            case TripItSessionEndedException:
                return new McpException(ReconnectMessage);
            case TripItSignInUnavailableException unavailable:
                _log.LogError("TripIt {Tool} cannot run: {Reason}", tool, unavailable.Message);
                return new McpException(unavailable.Message);
            case TripItAuthenticationException:
                _log.LogWarning("TripIt {Tool} for account {Account}: TripIt rejected the session and it could not be renewed.",
                    tool, SafeAccount());
                return new McpException(_client?.IsConfigured == false
                    ? "There is no TripIt session for this account yet. Reconnect the TripIt connector to sign in."
                    : "TripIt did not accept this account's session, and it could not be renewed right now. " +
                      "Try again in a minute; if it keeps failing, reconnect the TripIt connector.");
        }
        if (TripItErrorText.ForTool(ex) is { } message)
        {
            _log.LogDebug(ex, "TripIt {Tool} failed for account {Account}.", tool, SafeAccount());
            return new McpException(message);
        }
        _log.LogError(ex, "TripIt {Tool} failed unexpectedly for account {Account}.", tool, SafeAccount());
        return new McpException($"{tool} failed unexpectedly; the details are in the Multiplex server log.");
    }

    private string SafeAccount() => SiteClaims.AccountOf(httpContext.HttpContext?.User)?.AccountId ?? "(none)";
}
