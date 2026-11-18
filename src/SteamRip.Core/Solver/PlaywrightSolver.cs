using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;

namespace SteamRip.Core.Solver;

/// <summary>
/// Headless Chromium as a solver. BuzzHeavier and FileDitch sit behind a Cloudflare interstitial; a real browser
/// clears it in about a second, which is why this exists: without it every direct mirror dies unless the owner runs
/// a FlareSolverr container, and the only thing left to download is whatever the remaining mirror serves.
/// </summary>
public sealed class PlaywrightSolver(ILogger<PlaywrightSolver>? logger = null) : IBrowserSolver
{
    public const string UserAgent =
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/154.0.0.0 Safari/537.36";

    private readonly ILogger _log = logger ?? NullLogger<PlaywrightSolver>.Instance;

    public async Task<SolveResult> GetAsync(
        string url,
        bool disableMedia = false,
        CancellationToken ct = default
    )
    {
        await using var session = await CreateSessionAsync(ct);
        return await session.GetAsync(url, disableMedia, ct);
    }

    public async Task<IBrowserSession> CreateSessionAsync(CancellationToken ct = default)
    {
        IPlaywright? playwright = null;
        IBrowser? browser = null;
        try
        {
            playwright = await Playwright.CreateAsync();
            browser = await LaunchAsync(playwright);
            var context = await browser.NewContextAsync(
                new BrowserNewContextOptions
                {
                    UserAgent = UserAgent,
                    Locale = "en-US",
                    ViewportSize = new ViewportSize { Width = 1366, Height = 900 },
                    // Hosters check where the visitor came from: FileDitch bounces a request without this straight
                    // back to the homepage, and BuzzHeavier's page only renders its download row when it is present.
                    ExtraHTTPHeaders = new Dictionary<string, string>
                    {
                        ["Referer"] = "https://steamrip.com/",
                    },
                }
            );
            var page = await context.NewPageAsync();
            _log.LogInformation("Playwright: headless Chromium session ready");
            return new Session(playwright, browser, context, page, _log);
        }
        catch
        {
            if (browser is not null)
                await browser.DisposeAsync();
            playwright?.Dispose();
            throw;
        }
    }

    private async Task<IBrowser> LaunchAsync(IPlaywright playwright)
    {
        var options = new BrowserTypeLaunchOptions
        {
            Headless = true,
            // Cloudflare looks for the automation switch; without it the interstitial sticks.
            Args = ["--disable-blink-features=AutomationControlled"],
        };
        try
        {
            return await playwright.Chromium.LaunchAsync(options);
        }
        catch (PlaywrightException e)
            when (e.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase)
            )
        {
            _log.LogInformation("Playwright Chromium is missing; downloading it once");
            await BrowserSolver.EnsureInstalledAsync(_log);
            return await playwright.Chromium.LaunchAsync(options);
        }
    }

    private sealed class Session(
        IPlaywright playwright,
        IBrowser browser,
        IBrowserContext context,
        IPage page,
        ILogger log
    ) : IBrowserSession
    {
        public async Task<SolveResult> GetAsync(
            string url,
            bool disableMedia = false,
            CancellationToken ct = default
        )
        {
            log.LogDebug("Playwright GET {Url}", url);
            if (disableMedia)
                await page.RouteAsync(
                    "**/*",
                    route =>
                        route.Request.ResourceType is "image" or "media" or "font"
                            ? route.AbortAsync()
                            : route.ContinueAsync()
                );
            await page.GotoAsync(
                url,
                new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 45_000,
                }
            );
            await WaitForChallengeAsync(ct);
            var html = await page.ContentAsync();
            var ua = await page.EvaluateAsync<string>("navigator.userAgent");
            var cookies = (await context.CookiesAsync()).ToDictionary(c => c.Name, c => c.Value);
            log.LogInformation(
                "Playwright landed on {FinalUrl} ({Bytes} bytes, cookies [{Cookies}])",
                page.Url,
                html.Length,
                string.Join(", ", cookies.Keys)
            );
            return new SolveResult(html, ua, cookies, page.Url, 200, "headless Chromium");
        }

        /// <summary>Cloudflare's page titles its own interstitial; wait until the real page is up.</summary>
        private async Task WaitForChallengeAsync(CancellationToken ct)
        {
            for (var i = 0; i < 40; i++)
            {
                ct.ThrowIfCancellationRequested();
                string title;
                try
                {
                    title = await page.TitleAsync();
                }
                catch (PlaywrightException)
                {
                    await Task.Delay(250, ct);
                    continue;
                }
                var challenged =
                    title.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
                    || title.Contains("Attention Required", StringComparison.OrdinalIgnoreCase)
                    || title.Contains("Verifying", StringComparison.OrdinalIgnoreCase)
                    || title.Contains("Checking your browser", StringComparison.OrdinalIgnoreCase);
                if (!challenged)
                {
                    // one extra beat so the app's own JS (htmx) has attached its handlers
                    await Task.Delay(300, ct);
                    return;
                }
                await Task.Delay(500, ct);
            }
            throw new SolverException(
                "the headless browser could not get past the challenge at " + page.Url
            );
        }

        public async Task<string?> ActionAsync(object[] actions, CancellationToken ct = default)
        {
            string? last = null;
            foreach (var action in actions)
            {
                ct.ThrowIfCancellationRequested();
                var type = Property(action, "type") ?? "";
                switch (type)
                {
                    case "eval":
                    {
                        var script = Property(action, "script") ?? "";
                        var wantResult = Property(action, "returnResult") == "True";
                        var expression = script.TrimStart();
                        if (expression.StartsWith("return ", StringComparison.Ordinal))
                            expression = expression[7..];
                        try
                        {
                            last = await page.EvaluateAsync<string>(expression);
                            log.LogDebug("Playwright eval → {Result}", last);
                        }
                        catch (PlaywrightException e)
                        {
                            if (wantResult)
                                throw new SolverException("in-page script failed: " + e.Message);
                            log.LogDebug("Playwright eval (result ignored): {Error}", e.Message);
                        }
                        break;
                    }
                    case "wait_for":
                    {
                        var selector = Property(action, "selector") ?? "";
                        var timeout = int.TryParse(Property(action, "timeout"), out var t)
                            ? t
                            : 30_000;
                        await page.WaitForSelectorAsync(
                            selector,
                            new PageWaitForSelectorOptions { Timeout = timeout }
                        );
                        break;
                    }
                    case "wait":
                    {
                        var seconds = double.TryParse(Property(action, "seconds"), out var s)
                            ? s
                            : 1;
                        await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
                        break;
                    }
                    default:
                        log.LogDebug("Playwright: ignoring action {Type}", type);
                        break;
                }
            }
            return last;
        }

        private static string? Property(object action, string name) =>
            action.GetType().GetProperty(name)?.GetValue(action)?.ToString();

        public async ValueTask DisposeAsync()
        {
            try
            {
                await context.CloseAsync();
                await browser.DisposeAsync();
            }
            catch (PlaywrightException)
            {
                // the browser is already gone
            }
            finally
            {
                playwright.Dispose();
            }
        }
    }
}
