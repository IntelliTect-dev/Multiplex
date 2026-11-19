using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Multiplex.Web.Sites;
using PuppeteerSharp;
using PuppeteerSharp.Input;

namespace Multiplex.Web.SignIn;

public enum AttemptState
{
    Starting,
    Live,
    Checking,

    /// <summary>Signed in, session stored; waiting for the person's browser to collect the redirect.</summary>
    Ready,
    Completed,
    Cancelled,
    TimedOut,
    Failed,
}

/// <summary>
/// One live sign-in: a fresh browser profile showing the site's sign-in page, mirrored to the
/// person's (phone) browser over a WebSocket, with their taps and typing replayed into it.
/// Navigation is locked to the site's allowed hosts. The attempt watches for the person to be
/// signed in, stores that account's session and keeps the profile; the connection being added is
/// bound to that account. Anything else (timeout, cancel, failure) closes the browser and deletes
/// the profile.
/// </summary>
public sealed class LiveSignInAttempt : IAsyncDisposable
{
    /// <summary>
    /// The pause between the key presses that replay text typed on the phone: a typing pace.
    /// The person typed every character; the phone sends them together when Send is tapped.
    /// </summary>
    internal static readonly TimeSpan KeystrokePause = TimeSpan.FromMilliseconds(50);

    private readonly LiveSignInServices _services;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _stateLock = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _completeLock = new(1, 1);
    private readonly Lock _pagesLock = new();
    private readonly List<IPage> _pages = [];
    private readonly Channel<byte[]> _frames = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    private IDisposable? _slot;
    private IBrowser? _browser;
    private ICDPSession? _lockSession;
    private DateTimeOffset _lastBlockedAt = DateTimeOffset.MinValue;
    private IPage? _mainPage;
    private volatile IPage? _activePage;
    private WebSocket? _client;
    private byte[]? _lastFrame;
    private string? _currentHost;
    private bool _currentSecure;
    private int _viewportWidth;
    private int _viewportHeight;
    private double _frameCssWidth;
    private double _frameCssHeight;
    private Uri? _completedUrl;
    private Task _run = Task.CompletedTask;
    private bool _profileKept;

    internal LiveSignInAttempt(ISiteSignInDefinition definition, string pendingId, LiveSignInServices services)
    {
        Definition = definition;
        PendingId = pendingId;
        _services = services;
        Id = Base64Url(RandomNumberGenerator.GetBytes(32));
        TempProfile = services.Manager.Paths.NewTempProfile(definition.Slug);
        _viewportWidth = services.Options.ViewportWidth;
        _viewportHeight = services.Options.ViewportHeight;
        _frameCssWidth = _viewportWidth;
        _frameCssHeight = _viewportHeight;
        StatusMessage = $"Opening the {definition.DisplayName} sign-in page…";
    }

    /// <summary>Unguessable id carried in the live page URL (<c>?a=</c>).</summary>
    public string Id { get; }

    public string PendingId { get; }

    public ISiteSignInDefinition Definition { get; }

    public string Slug => Definition.Slug;

    public string TempProfile { get; }

    public AttemptState State { get; private set; } = AttemptState.Starting;

    public string StatusMessage { get; private set; }

    /// <summary>Where to send the person after a cancellation (from <see cref="IAuthorizationCompleter.FailAsync"/>).</summary>
    public Uri? ReturnUrl { get; private set; }

    /// <summary>The signed-in account once the attempt is <see cref="AttemptState.Ready"/>.</summary>
    public SiteAccount? Account { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    /// <summary>True once the browser can no longer be driven (anything past <see cref="AttemptState.Checking"/>).</summary>
    public bool HasEnded => State is not (AttemptState.Starting or AttemptState.Live or AttemptState.Checking);

    /// <summary>True when a new attempt for the same pending authorization may replace this one.</summary>
    public bool CanBeReplaced => State is AttemptState.TimedOut or AttemptState.Failed;

    /// <summary>The page being mirrored; for tests and diagnostics.</summary>
    internal IPage? ActivePage => _activePage;

    /// <summary>Completes when the attempt's browser work has finished and its resources are released.</summary>
    internal Task Completion => _run;

    public string DonePath => $"/connect/{Slug}/done?a={Id}";

    internal void Start(IDisposable slot)
    {
        _slot = slot;
        _lifetime.CancelAfter(_services.Options.AttemptTimeout);
        _run = Task.Run(RunAsync);
    }

    // ---------------------------------------------------------------- lifecycle

    private async Task RunAsync()
    {
        var ct = _lifetime.Token;
        var pump = Task.Run(() => PumpFramesAsync(ct));
        try
        {
            _browser = await _services.Manager.LaunchAsync(Slug, TempProfile, ct);
            _browser.Disconnected += (_, _) =>
            {
                if (!HasEnded && State != AttemptState.Checking)
                    _ = EndAsync(AttemptState.Failed, "The sign-in browser closed unexpectedly. Please start again.");
            };
            await LockNavigationAsync(_browser);

            var page = (await _browser.PagesAsync()).FirstOrDefault() ?? await _browser.NewPageAsync();
            _mainPage = page;
            await AttachPageAsync(page);
            await SetActiveAsync(page);
            if (!TrySetState(AttemptState.Starting, AttemptState.Live, $"Waiting for you to sign in to {Definition.DisplayName}…"))
                return;

            try
            {
                await page.GoToAsync(Definition.SignInStartUrl.AbsoluteUri,
                    new NavigationOptions { Timeout = 60_000, WaitUntil = [WaitUntilNavigation.DOMContentLoaded] });
            }
            catch (Exception ex) when (ex is NavigationException or TimeoutException)
            {
                // The page may still have rendered; the person sees whatever it shows and can reload.
                _services.Logger.LogWarning("Opening the {Site} sign-in page did not finish: {Error}", Slug, ex.GetType().Name);
            }

            while (State == AttemptState.Live)
            {
                await Task.Delay(_services.Options.PollInterval, ct);
                if (State != AttemptState.Live) break;

                SiteAccount? account = null;
                try
                {
                    account = await Definition.TryGetSignedInAccountAsync(page, ct).WaitAsync(TimeSpan.FromSeconds(30), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Usually a navigation tearing down the page's context mid-check; try again next tick.
                    _services.Logger.LogDebug("Sign-in check for {Site} failed: {Error}", Slug, ex.GetType().Name);
                }

                if (account is not null && TrySetState(AttemptState.Live, AttemptState.Checking, $"Signed in. Checking your {Definition.DisplayName} account…"))
                {
                    await OnSignedInAsync(page, account, ct);
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            await EndAsync(AttemptState.TimedOut, "The sign-in took too long and was stopped. You can start again.");
        }
        catch (Exception ex)
        {
            // The type only: a message can carry paths, URLs or text from the site's page.
            _services.Logger.LogError("Live sign-in for {Site} failed: {Error}", Slug, ex.GetType().Name);
            await EndAsync(AttemptState.Failed, "Something went wrong while signing in. Please start again.");
        }
        finally
        {
            await CleanUpAsync();
            _frames.Writer.TryComplete();
            await pump.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private async Task OnSignedInAsync(IPage page, SiteAccount account, CancellationToken ct)
    {
        _services.Logger.LogInformation("Live sign-in to {Site} reached account {AccountId}.", Slug, account.AccountId);

        var snapshot = await Definition.CaptureSnapshotAsync(page, ct);
        await CloseBrowserAsync();

        using (await _services.Manager.LockAccountAsync(Slug, account.AccountId, ct))
        {
            await _services.Manager.PromoteProfileAsync(Slug, TempProfile, account.AccountId, ct);
            _profileKept = true;
            await _services.Store.SetAsync(Slug, account.AccountId, snapshot, ct);
            await _services.Index.UpsertAsync(account, ct);
        }

        Account = account;
        _services.Logger.LogInformation("Stored the {Site} session for account {AccountId}.", Slug, account.AccountId);
        await EndAsync(AttemptState.Ready, $"Signed in to {Definition.DisplayName}. Returning you to the app…", DonePath);
    }

    /// <summary>
    /// Issues the authorization code (once) and returns where to send the person. Called when
    /// their browser arrives at the done URL, so the short-lived code is used straight away.
    /// </summary>
    public async Task<Uri?> CompleteAsync(IAuthorizationCompleter completer, CancellationToken ct)
    {
        await _completeLock.WaitAsync(ct);
        try
        {
            if (State == AttemptState.Completed) return _completedUrl;
            if (State != AttemptState.Ready || Account is null) return null;
            _completedUrl = await completer.CompleteAsync(PendingId, Account, ct);
            SetState(AttemptState.Completed, StatusMessage);
            return _completedUrl;
        }
        finally
        {
            _completeLock.Release();
        }
    }

    /// <summary>The person gave up: stop the browser, delete the profile and end the authorization.</summary>
    public async Task<Uri?> CancelAsync(IAuthorizationCompleter? completer)
    {
        if (!TryEnd(AttemptState.Cancelled, "Sign-in cancelled."))
            return State is AttemptState.Cancelled ? ReturnUrl : null;

        try { await _lifetime.CancelAsync(); } catch (ObjectDisposedException) { }
        if (completer is not null)
            ReturnUrl = await completer.FailAsync(PendingId, "access_denied", "Sign-in cancelled", CancellationToken.None);
        await AfterEndAsync(AttemptState.Cancelled, null);
        return ReturnUrl;
    }

    private async Task EndAsync(AttemptState state, string message, string? next = null)
    {
        if (TryEnd(state, message)) await AfterEndAsync(state, next);
    }

    /// <summary>Moves to an end state exactly once; false when the attempt had already ended.</summary>
    private bool TryEnd(AttemptState state, string message)
    {
        lock (_stateLock)
        {
            if (HasEnded) return false;
            State = state;
            StatusMessage = message;
            EndedAt = _services.Time.GetUtcNow();
            return true;
        }
    }

    private async Task AfterEndAsync(AttemptState state, string? next)
    {
        await BroadcastStatusAsync(next);
        if (state != AttemptState.Ready && !_lifetime.IsCancellationRequested)
        {
            try { await _lifetime.CancelAsync(); } catch (ObjectDisposedException) { }
        }
    }

    private void SetState(AttemptState state, string message)
    {
        lock (_stateLock)
        {
            State = state;
            StatusMessage = message;
        }
        _ = BroadcastStatusAsync(null);
    }

    private bool TrySetState(AttemptState from, AttemptState to, string message)
    {
        lock (_stateLock)
        {
            if (State != from) return false;
            State = to;
            StatusMessage = message;
        }
        _ = BroadcastStatusAsync(null);
        return true;
    }

    private async Task CleanUpAsync()
    {
        await CloseBrowserAsync();
        // Free the live-browser slot before any wait on the profile's files, so a new sign-in is
        // not refused while Chrome lets go of this one.
        Interlocked.Exchange(ref _slot, null)?.Dispose();
        if (!_profileKept)
        {
            try
            {
                await _services.Manager.DiscardProfileAsync(TempProfile);
            }
            catch (Exception ex)
            {
                _services.Logger.LogError("Could not delete temporary sign-in profile {Profile}: {Error}", Path.GetFileName(TempProfile), ex.GetType().Name);
            }
        }
    }

    private async Task CloseBrowserAsync()
    {
        var browser = Interlocked.Exchange(ref _browser, null);
        _activePage = null;
        await _services.Manager.CloseAsync(browser);
    }

    public async ValueTask DisposeAsync()
    {
        if (!HasEnded) await EndAsync(AttemptState.Cancelled, "The server stopped this sign-in.");
        try { await _lifetime.CancelAsync(); } catch (ObjectDisposedException) { }
        await _run.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (_client is { State: WebSocketState.Open } client)
        {
            try { await client.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "server stopping", CancellationToken.None); } catch (Exception) { }
        }
        _lifetime.Dispose();
    }

    // ---------------------------------------------------------------- pages, navigation lock, screencast

    private async Task AttachPageAsync(IPage page)
    {
        lock (_pagesLock) _pages.Add(page);
        page.Popup += (_, e) => _ = OnPopupAsync(e.PopupPage);
        page.FrameNavigated += (_, e) =>
        {
            if (e.Frame != page.MainFrame) return;
            _ = OnNavigatedAsync(page, e.Frame.Url);
        };
        page.Close += (_, _) => _ = OnPageClosedAsync(page);
        page.Client.MessageReceived += (_, e) => OnCdpMessage(page, e);
        await page.SetViewportAsync(Viewport());
    }

    /// <summary>
    /// The navigation lock, browser-wide: every Document request (in any tab, including a new
    /// tab's very first request) is paused, and a top-level one to a host outside
    /// <see cref="ISiteSignInDefinition.AllowedNavigationHosts"/> is failed before it leaves the
    /// browser. Frames and sub-resources may load from anywhere.
    /// </summary>
    private async Task LockNavigationAsync(IBrowser browser)
    {
        var session = await browser.Target.CreateCDPSessionAsync();
        _lockSession = session;
        session.MessageReceived += (_, e) =>
        {
            if (e.MessageID == "Fetch.requestPaused") _ = DecideAsync(session, e.MessageData);
        };
        await session.SendAsync("Fetch.enable", new Dictionary<string, object>
        {
            ["patterns"] = new[]
            {
                new Dictionary<string, object> { ["urlPattern"] = "*", ["resourceType"] = "Document", ["requestStage"] = "Request" },
            },
        });
    }

    private async Task DecideAsync(ICDPSession session, System.Text.Json.JsonElement paused)
    {
        try
        {
            var decision = await NavigationLock.DecideAsync(paused, Definition.AllowedNavigationHosts,
                frameId => IsTopLevelFrameAsync(session, frameId),
                (method, args) => session.SendAsync(method, args));
            if (decision.Outcome == PausedRequestOutcome.Blocked)
                await NotifyBlockedAsync(decision.Url);
            else if (decision.Outcome == PausedRequestOutcome.FailedClosed)
                _services.Logger.LogDebug("Navigation lock on {Site} refused a request it could not judge: {Error}", Slug, decision.Error?.GetType().Name);
        }
        catch (Exception ex)
        {
            // The request or browser went away mid-decision.
            _services.Logger.LogDebug("Navigation lock on {Site} skipped a request: {Error}", Slug, ex.GetType().Name);
        }
    }

    /// <summary>A page's main frame has the same id as its target; iframes (even out-of-process ones) are not "page" targets.</summary>
    private static async Task<bool> IsTopLevelFrameAsync(ICDPSession session, string? frameId)
    {
        if (string.IsNullOrEmpty(frameId)) return true; // unknown: treat as top level, i.e. block
        var targets = await session.SendAsync<System.Text.Json.JsonElement>("Target.getTargets");
        foreach (var info in targets.GetProperty("targetInfos").EnumerateArray())
        {
            if (info.GetProperty("targetId").GetString() == frameId)
                return info.GetProperty("type").GetString() == "page";
        }
        return false;
    }

    private ViewPortOptions Viewport() => new()
    {
        Width = _viewportWidth,
        Height = _viewportHeight,
        DeviceScaleFactor = _services.Options.DeviceScaleFactor,
    };

    private bool IsAllowed(string url) => NavigationLock.IsAllowed(url, Definition.AllowedNavigationHosts);

    /// <summary>
    /// Second line of the navigation lock: a tab whose navigation was refused (it shows Chrome's
    /// error page) is closed; a top-level page that somehow committed a page outside the allowed
    /// hosts is sent back to the sign-in page.
    /// </summary>
    private async Task OnNavigatedAsync(IPage page, string url)
    {
        try
        {
            var action = NavigationLock.AfterNavigation(url, Definition.AllowedNavigationHosts);
            if (action == CommittedPageAction.Ignore) return;
            if (action == CommittedPageAction.ErrorPage)
            {
                if (page != _mainPage)
                {
                    await page.CloseAsync();
                }
                else if (_services.Time.GetUtcNow() - _lastBlockedAt < TimeSpan.FromSeconds(5))
                {
                    // The error page is our own refusal: put the person back where they were.
                    // (A genuine network error stays on screen; they can Reload.)
                    try { await page.GoBackAsync(new NavigationOptions { Timeout = 30_000 }); }
                    catch (Exception) { }
                    if (!IsAllowed(page.Url))
                        await page.GoToAsync(Definition.SignInStartUrl.AbsoluteUri, new NavigationOptions { Timeout = 30_000 });
                }
                return;
            }
            if (action == CommittedPageAction.Blocked)
            {
                await NotifyBlockedAsync(url);
                if (page == _mainPage)
                    await page.GoToAsync(Definition.SignInStartUrl.AbsoluteUri, new NavigationOptions { Timeout = 30_000 });
                else
                    await page.CloseAsync();
                return;
            }
            if (page == _activePage) await SendUrlAsync(url);
        }
        catch (Exception ex)
        {
            _services.Logger.LogDebug("Post-navigation check on {Site} skipped: {Error}", Slug, ex.GetType().Name);
        }
    }

    private async Task OnPopupAsync(IPage popup)
    {
        try
        {
            if (HasEnded) { await popup.CloseAsync(); return; }
            var url = popup.Url;
            // Its request was already refused by the lock (which told the phone); just close the tab.
            if (url is not ("" or "about:blank") && !IsAllowed(url))
            {
                await popup.CloseAsync();
                return;
            }
            await AttachPageAsync(popup);
            await SetActiveAsync(popup);
        }
        catch (Exception ex)
        {
            _services.Logger.LogDebug("Popup on {Site} could not be mirrored: {Error}", Slug, ex.GetType().Name);
        }
    }

    private async Task OnPageClosedAsync(IPage page)
    {
        IPage? fallback;
        lock (_pagesLock)
        {
            _pages.Remove(page);
            fallback = _pages.LastOrDefault(p => !p.IsClosed);
        }
        if (page == _activePage && fallback is not null && !HasEnded)
        {
            try { await SetActiveAsync(fallback); } catch (Exception) { }
        }
    }

    private async Task SetActiveAsync(IPage page)
    {
        var previous = _activePage;
        _activePage = page;
        if (previous is not null && previous != page && !previous.IsClosed)
        {
            try { await previous.Client.SendAsync("Page.stopScreencast"); } catch (Exception) { }
        }
        await StartScreencastAsync(page);
        if (Uri.TryCreate(page.Url, UriKind.Absolute, out _)) await SendUrlAsync(page.Url);
    }

    private Task StartScreencastAsync(IPage page) =>
        page.Client.SendAsync("Page.startScreencast", new Dictionary<string, object>
        {
            ["format"] = "jpeg",
            ["quality"] = Math.Clamp(_services.Options.FrameQuality, 1, 100),
            ["maxWidth"] = (int)Math.Round(_viewportWidth * _services.Options.DeviceScaleFactor),
            ["maxHeight"] = (int)Math.Round(_viewportHeight * _services.Options.DeviceScaleFactor),
            ["everyNthFrame"] = 1,
        });

    private void OnCdpMessage(IPage page, MessageEventArgs e)
    {
        if (e.MessageID != "Page.screencastFrame") return;
        try
        {
            var data = e.MessageData;
            var sessionId = data.GetProperty("sessionId").GetInt32();
            // Ack straight away so Chrome keeps sending; only the newest frame is forwarded.
            _ = AckAsync(page, sessionId);
            if (page != _activePage) return;

            if (data.TryGetProperty("metadata", out var meta))
            {
                if (meta.TryGetProperty("deviceWidth", out var w) && w.TryGetDouble(out var width) && width > 0) _frameCssWidth = width;
                if (meta.TryGetProperty("deviceHeight", out var h) && h.TryGetDouble(out var height) && height > 0) _frameCssHeight = height;
            }
            var bytes = Convert.FromBase64String(data.GetProperty("data").GetString() ?? "");
            _lastFrame = bytes;
            _frames.Writer.TryWrite(bytes);
        }
        catch (Exception ex) when (ex is FormatException or KeyNotFoundException or InvalidOperationException)
        {
        }
    }

    private static async Task AckAsync(IPage page, int sessionId)
    {
        try
        {
            await page.Client.SendAsync("Page.screencastFrameAck", new Dictionary<string, object> { ["sessionId"] = sessionId });
        }
        catch (Exception)
        {
        }
    }

    private async Task PumpFramesAsync(CancellationToken ct)
    {
        await foreach (var frame in _frames.Reader.ReadAllAsync(CancellationToken.None))
        {
            if (_client is { State: WebSocketState.Open } client)
                await SendAsync(client, frame, WebSocketMessageType.Binary);
        }
    }

    // ---------------------------------------------------------------- the phone's WebSocket

    /// <summary>
    /// Serves one connected phone page until it disconnects. A newer connection replaces an
    /// older one; a dropped connection does not end the attempt (people switch apps to read a
    /// code), and a returning page is sent the current state and the last frame.
    /// </summary>
    public async Task ServeClientAsync(WebSocket socket, CancellationToken requestAborted)
    {
        var previous = Interlocked.Exchange(ref _client, socket);
        if (previous is not null && previous != socket)
        {
            // A distinct close code tells the older tab not to reconnect (else two tabs would take turns forever).
            try { await previous.CloseAsync(MirrorProtocol.ReplacedCloseStatus, "replaced", CancellationToken.None); } catch (Exception) { }
        }

        try
        {
            await SendTextAsync(socket, MirrorProtocol.Status(StateName(), StatusMessage, NextFor(State)));
            await SendTextAsync(socket, MirrorProtocol.Viewport(_viewportWidth, _viewportHeight));
            if (_currentHost is not null) await SendTextAsync(socket, MirrorProtocol.Url(_currentHost, _currentSecure));
            if (_lastFrame is { } frame && !HasEnded) await SendAsync(socket, frame, WebSocketMessageType.Binary);

            var buffer = new byte[MirrorProtocol.MaxMessageBytes];
            while (socket.State == WebSocketState.Open && !requestAborted.IsCancellationRequested)
            {
                var length = 0;
                ValueWebSocketReceiveResult result;
                do
                {
                    if (length == buffer.Length)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "too big", CancellationToken.None);
                        return;
                    }
                    result = await socket.ReceiveAsync(buffer.AsMemory(length), requestAborted);
                    length += result.Count;
                } while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close);

                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.MessageType != WebSocketMessageType.Text) continue;
                if (MirrorProtocol.Parse(buffer.AsSpan(0, length)) is { } input)
                    await HandleInputAsync(input);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
        }
        finally
        {
            Interlocked.CompareExchange(ref _client, null, socket);
        }
    }

    /// <summary>Replays one input from the phone into the mirrored page. Ignored unless the attempt is live.</summary>
    internal async Task HandleInputAsync(MirrorInput input)
    {
        // The phone reports its size as soon as it connects, usually while the browser is still starting.
        if (input is ViewportInput viewport)
        {
            if (!HasEnded)
            {
                try { await ResizeAsync(viewport.Width, viewport.Height); }
                catch (Exception ex) { _services.Logger.LogDebug("Resizing the {Site} mirror failed: {Error}", Slug, ex.GetType().Name); }
            }
            return;
        }
        if (State != AttemptState.Live || _activePage is not { IsClosed: false } page) return;
        try
        {
            switch (input)
            {
                case TapInput tap:
                    await page.Mouse.ClickAsync((decimal)(tap.X * _frameCssWidth), (decimal)(tap.Y * _frameCssHeight));
                    break;
                case ScrollInput scroll:
                    await page.Mouse.MoveAsync((decimal)(scroll.X * _frameCssWidth), (decimal)(scroll.Y * _frameCssHeight));
                    await page.Mouse.WheelAsync((decimal)(scroll.DeltaX * _frameCssWidth), (decimal)(scroll.DeltaY * _frameCssHeight));
                    break;
                case TextInput text:
                    // One key press per character, at a typing pace, as the person typed it on the
                    // phone; a character no key types is inserted as text. A site's bot protection
                    // scores the keystrokes on its sign-in form, and text that appears with no key
                    // presses is not how a person types (#61).
                    await page.Keyboard.TypeAsync(text.Text, new TypeOptions { Delay = (int)KeystrokePause.TotalMilliseconds });
                    break;
                case KeyInput key:
                    await page.Keyboard.PressAsync(key.Key);
                    break;
                case BackInput:
                    if (page != _mainPage) await page.CloseAsync();
                    else _ = page.GoBackAsync(new NavigationOptions { Timeout = 30_000 }).ContinueWith(_ => { }, TaskScheduler.Default);
                    break;
                case ReloadInput:
                    _ = page.ReloadAsync(new ReloadOptions { Timeout = 30_000 }).ContinueWith(_ => { }, TaskScheduler.Default);
                    break;
            }
        }
        catch (Exception ex)
        {
            // Never log the input itself: it may be a password.
            _services.Logger.LogDebug("Replaying a {Input} into the {Site} page failed: {Error}", input.GetType().Name, Slug, ex.GetType().Name);
        }
    }

    /// <summary>Matches the mirrored page's viewport to the phone's mirror area, within sane bounds.</summary>
    private async Task ResizeAsync(int width, int height)
    {
        width = Math.Clamp(width, 320, 1024);
        height = Math.Clamp(height, 360, 1100);
        if (Math.Abs(width - _viewportWidth) < 4 && Math.Abs(height - _viewportHeight) < 4)
        {
            await SendViewportAsync();
            return;
        }
        _viewportWidth = width;
        _viewportHeight = height;
        _frameCssWidth = width;
        _frameCssHeight = height;
        IPage[] pages;
        lock (_pagesLock) pages = [.. _pages.Where(p => !p.IsClosed)];
        foreach (var p in pages) await p.SetViewportAsync(Viewport());
        if (_activePage is { IsClosed: false } active)
        {
            try { await active.Client.SendAsync("Page.stopScreencast"); } catch (Exception) { }
            await StartScreencastAsync(active);
        }
        await SendViewportAsync();
    }

    /// <summary>Tells the phone the CSS size of the mirrored viewport, so it draws frames at that size.</summary>
    private async Task SendViewportAsync()
    {
        if (_client is { State: WebSocketState.Open } client)
            await SendTextAsync(client, MirrorProtocol.Viewport(_viewportWidth, _viewportHeight));
    }

    private async Task SendUrlAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host.Length == 0) return;
        _currentHost = uri.Host;
        _currentSecure = uri.Scheme == Uri.UriSchemeHttps;
        if (_client is { State: WebSocketState.Open } client) await SendTextAsync(client, MirrorProtocol.Url(uri.Host, _currentSecure));
    }

    private async Task NotifyBlockedAsync(string url)
    {
        _lastBlockedAt = _services.Time.GetUtcNow();
        // file:, data: and the like have no host to name.
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : "another site";
        _services.Logger.LogInformation("Blocked a navigation from the {Site} sign-in to {Host}.", Slug, host);
        if (_client is { State: WebSocketState.Open } client) await SendTextAsync(client, MirrorProtocol.Blocked(host));
    }

    private async Task BroadcastStatusAsync(string? next)
    {
        if (_client is { State: WebSocketState.Open } client)
            await SendTextAsync(client, MirrorProtocol.Status(StateName(), StatusMessage, next ?? NextFor(State)));
    }

    private string? NextFor(AttemptState state) => state switch
    {
        AttemptState.Ready or AttemptState.Completed => DonePath,
        AttemptState.Cancelled => ReturnUrl?.AbsoluteUri,
        _ => null,
    };

    private string StateName() => State switch
    {
        AttemptState.Starting => "starting",
        AttemptState.Live => "live",
        AttemptState.Checking => "checking",
        AttemptState.Ready or AttemptState.Completed => "ready",
        AttemptState.Cancelled => "cancelled",
        AttemptState.TimedOut => "timeout",
        _ => "failed",
    };

    private Task SendTextAsync(WebSocket socket, string json) =>
        SendAsync(socket, Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text);

    private async Task SendAsync(WebSocket socket, byte[] payload, WebSocketMessageType type)
    {
        await _sendLock.WaitAsync();
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.SendAsync(new ReadOnlyMemory<byte>(payload), type, endOfMessage: true, CancellationToken.None)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (Exception ex) when (ex is WebSocketException or TimeoutException or ObjectDisposedException or OperationCanceledException)
        {
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>What a <see cref="LiveSignInAttempt"/> needs from the host.</summary>
internal sealed record LiveSignInServices(
    SiteBrowserManager Manager,
    SiteSessionStore Store,
    SiteAccountIndex Index,
    SignInOptions Options,
    Func<IAuthorizationCompleter?> Completer,
    TimeProvider Time,
    ILogger Logger);
