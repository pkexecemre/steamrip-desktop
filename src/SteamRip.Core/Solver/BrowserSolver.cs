using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;

namespace SteamRip.Core.Solver;

/// <summary>
/// Fallback solver: opens a visible Chromium so the user can pass an interactive challenge, then hands cookies + user agent back.
/// Never invoked automatically; the queue parks the item in "needs you" and the user clicks to start this.
/// </summary>
public sealed class BrowserSolver(ILogger<BrowserSolver>? logger = null)
{
    private readonly ILogger _log = logger ?? NullLogger<BrowserSolver>.Instance;

    public static Task EnsureInstalledAsync(ILogger? log = null)
    {
        log?.LogInformation("Checking Playwright Chromium (playwright install chromium)…");
        var code = Program.Main(["install", "chromium"]);
        if (code == 0)
            return Task.CompletedTask;
        log?.LogError("playwright install chromium exited with {Code}", code);
        throw new SolverException($"playwright install chromium exited with {code}");
    }

    /// <summary>
    /// For a hoster whose download *endpoint* sits behind a Cloudflare Turnstile check (BuzzHeavier): open the share
    /// page in a visible browser, let the user pass the check, and read the <c>hx-redirect</c> the page's own button
    /// gets back — without letting the browser start the multi-hundred-GB transfer itself.
    /// </summary>
    public async Task<(string? Redirect, string? Title)> CaptureHxRedirectAsync(
        string shareUrl,
        string referer,
        string buttonSelector,
        TimeSpan? timeout = null,
        CancellationToken ct = default
    )
    {
        using var pw = await Playwright.CreateAsync();
        await using var browser = await pw.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = false }
        );
        var context = await browser.NewContextAsync(
            new BrowserNewContextOptions
            {
                ExtraHTTPHeaders = new Dictionary<string, string> { ["Referer"] = referer },
            }
        );
        var page = await context.NewPageAsync();
        string? redirect = null;
        await page.RouteAsync(
            "**/download?*",
            async route =>
            {
                // Fetch it ourselves with the page's cookies: the response header is the link, the body is a
                // redirect that would start the download in this browser.
                try
                {
                    var response = await route.FetchAsync();
                    redirect = response
                        .Headers.FirstOrDefault(h =>
                            string.Equals(h.Key, "hx-redirect", StringComparison.OrdinalIgnoreCase)
                        )
                        .Value;
                    _log.LogDebug(
                        "BuzzHeavier download endpoint → HTTP {Status}, hx-redirect {Redirect}",
                        response.Status,
                        redirect is null ? "(none)" : "yes"
                    );
                }
                catch (PlaywrightException e)
                {
                    _log.LogDebug("download endpoint fetch failed: {Error}", e.Message);
                }
                await route.FulfillAsync(new RouteFulfillOptions { Status = 204, Body = "" });
            }
        );

        _log.LogInformation(
            "Opening a browser for the challenge on {ShareUrl}; pass the check in the window",
            shareUrl
        );
        await page.GotoAsync(
            referer,
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45_000 }
        );
        await page.EvaluateAsync(
            $"window.location.href = {System.Text.Json.JsonSerializer.Serialize(shareUrl)};"
        );
        await page.WaitForSelectorAsync(
            buttonSelector,
            new PageWaitForSelectorOptions { Timeout = 45_000 }
        );

        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(3));
        var lastClick = DateTime.MinValue;
        string? title = null;
        while (DateTime.UtcNow < deadline && redirect is null)
        {
            ct.ThrowIfCancellationRequested();
            if (page.IsClosed)
                throw new SolverException("Browser window was closed before the check was passed.");
            title ??= await page.TitleAsync();
            if ((DateTime.UtcNow - lastClick).TotalSeconds >= 3)
            {
                lastClick = DateTime.UtcNow;
                try
                {
                    await page.ClickAsync(buttonSelector, new PageClickOptions { Timeout = 5_000 });
                }
                catch (PlaywrightException)
                {
                    // the button moved or the page is mid-challenge; try again
                }
            }
            await Task.Delay(500, ct);
        }
        if (redirect is null)
            throw new SolverException("The check was not passed in the browser window in time.");
        _log.LogInformation(
            "BuzzHeavier check passed; direct link captured ({File})",
            title ?? "unknown title"
        );
        return (redirect, title);
    }

    public async Task<SolveResult> SolveAsync(
        string url,
        string? referer = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default
    )
    {
        _log.LogInformation(
            "Opening visible browser for {Url} (referer {Referer})",
            url,
            referer ?? "none"
        );
        using var pw = await Playwright.CreateAsync();
        await using var browser = await pw.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = false }
        );
        var context = await browser.NewContextAsync(
            referer is null
                ? new BrowserNewContextOptions()
                : new BrowserNewContextOptions
                {
                    ExtraHTTPHeaders = new Dictionary<string, string> { ["Referer"] = referer },
                }
        );
        var page = await context.NewPageAsync();
        await page.GotoAsync(
            url,
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded }
        );

        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(3));
        var stable = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (page.IsClosed)
            {
                _log.LogWarning("Browser window closed before the challenge was solved");
                throw new SolverException(
                    "Browser window was closed before the challenge was solved."
                );
            }
            var title = await page.TitleAsync();
            var challenged =
                title.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
                || title.Contains("Attention Required", StringComparison.OrdinalIgnoreCase)
                || title.Contains("Verifying", StringComparison.OrdinalIgnoreCase);
            stable = challenged ? 0 : stable + 1;
            if (stable >= 6)
                break; // ~3 s on the real page
            await Task.Delay(500, ct);
        }
        if (stable < 6)
        {
            _log.LogWarning("Timed out waiting for the user to solve the challenge at {Url}", url);
            throw new SolverException("Timed out waiting for the challenge to be solved.");
        }

        var cookies = (await context.CookiesAsync())
            .Where(c =>
                page.Url.Contains(c.Domain.TrimStart('.'), StringComparison.OrdinalIgnoreCase)
            )
            .ToDictionary(c => c.Name, c => c.Value);
        var ua = await page.EvaluateAsync<string>("navigator.userAgent");
        var html = await page.ContentAsync();
        _log.LogInformation(
            "Browser solve finished at {FinalUrl} ({Bytes} bytes, cookies [{Cookies}])",
            page.Url,
            html.Length,
            string.Join(", ", cookies.Keys)
        );
        return new SolveResult(html, ua, cookies, page.Url, 200, "Solved in browser");
    }
}
