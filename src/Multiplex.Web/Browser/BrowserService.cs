using System.Collections.Concurrent;
using PuppeteerSharp;

namespace Multiplex.Web.Browser;

/// <summary>
/// Owns one shared headless browser. Launched lazily on first use; each tool call gets its own page.
/// </summary>
public sealed class BrowserService(IConfiguration config, ILogger<BrowserService> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _launchLock = new(1, 1);
    private IBrowser? _browser;
    private readonly ConcurrentBag<IPage> _idlePages = [];

    /// <summary>Pause between tool steps so a headed run can be followed by eye; 0 disables it.</summary>
    public int StepDelayMs { get; } = config.GetValue("Browser:StepDelayMs", 0);

    public Task PauseAsync(CancellationToken ct = default) =>
        StepDelayMs > 0 ? Task.Delay(StepDelayMs, ct) : Task.CompletedTask;

    /// <summary>
    /// Hands out an idle page, else a new one. Pages are pooled rather than closed: closing the last tab of a
    /// headed window closes the window, which breaks the next call.
    /// </summary>
    public async Task<IPage> RentPageAsync(CancellationToken ct = default)
    {
        var browser = await GetBrowserAsync(ct);
        while (_idlePages.TryTake(out var idle))
            if (!idle.IsClosed) return idle;
        return await browser.NewPageAsync();
    }

    public async Task ReturnPageAsync(IPage page)
    {
        if (page.IsClosed) return;
        await page.GoToAsync("about:blank");
        _idlePages.Add(page);
    }

    private async Task<IBrowser> GetBrowserAsync(CancellationToken ct)
    {
        if (_browser is { IsClosed: false }) return _browser;

        await _launchLock.WaitAsync(ct);
        try
        {
            if (_browser is { IsClosed: false }) return _browser;

            // Browser:ExecutablePath points at a system Chrome/Chromium; unset downloads one managed by Puppeteer.
            var executablePath = config["Browser:ExecutablePath"];
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                logger.LogInformation("Browser:ExecutablePath not set; downloading Chromium via Puppeteer.");
                executablePath = (await new BrowserFetcher().DownloadAsync()).GetExecutablePath();
            }

            _browser = await Puppeteer.LaunchAsync(new LaunchOptions
            {
                Headless = config.GetValue("Browser:Headless", true),
                ExecutablePath = executablePath,
                Args = ["--no-sandbox"],
            });

            // A headed launch opens its own about:blank tab; pool it so it gets used instead of left behind.
            foreach (var launchPage in await _browser.PagesAsync()) _idlePages.Add(launchPage);
            return _browser;
        }
        finally
        {
            _launchLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) await _browser.CloseAsync();
        _launchLock.Dispose();
    }
}
