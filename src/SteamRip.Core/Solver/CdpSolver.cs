using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SteamRip.Core.Solver;

/// <summary>Capabilities only a real page can offer: look at the links it set at runtime, click things, watch responses.</summary>
public interface IInteractiveSession : IBrowserSession
{
    Task<string?> TitleAsync(CancellationToken ct = default);

    Task<IReadOnlyList<PageLink>> LinksAsync(CancellationToken ct = default);

    Task ClickAsync(string selector, CancellationToken ct = default);

    /// <summary>Response headers of the next request whose URL contains <paramref name="urlPart"/>, or null on timeout.</summary>
    Task<IReadOnlyDictionary<string, string>?> WaitForResponseAsync(
        string urlPart,
        TimeSpan timeout,
        CancellationToken ct = default
    );

    /// <summary>URL of the next request that satisfies <paramref name="predicate"/>, or null on timeout.</summary>
    Task<string?> WaitForRequestAsync(
        Func<string, bool> predicate,
        TimeSpan timeout,
        CancellationToken ct = default
    );
}

/// <summary>
/// The bundled-nowhere browser solver: drives the Chromium the user already has over DevTools. Used when no
/// FlareSolverr answers, which on a fresh install is always.
/// </summary>
public sealed class CdpSolver(ILogger<CdpSolver>? logger = null, bool headless = true)
    : IBrowserSolver
{
    private readonly ILogger _log = logger ?? NullLogger<CdpSolver>.Instance;

    public static string? Executable => Browsers.Find();

    public bool Available => Executable is not null;

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
        var candidates = Browsers.All();
        if (candidates.Count == 0)
            throw new SolverException(
                "no Chromium-family browser found: install chromium (or Google Chrome) to let the app pass hoster checks"
            );
        var errors = new List<string>();
        foreach (var exe in candidates)
        {
            var browser = await CdpBrowser.LaunchAsync(exe, headless, _log, ct);
            // A distro build can fail TLS/HTTP2 against a hoster while another Chromium build works; the probe
            // makes that a switch to the next browser instead of "the page could not load".
            await browser.NavigateAsync("https://steamrip.com/", ct);
            if (browser.LastNavigationError is null)
                return new Session(browser, _log);
            _log.LogWarning(
                "{Browser} could not reach steamrip.com ({Error}); trying another browser",
                Path.GetFileName(exe),
                browser.LastNavigationError
            );
            errors.Add(Path.GetFileName(exe) + ": " + browser.LastNavigationError);
            await browser.DisposeAsync();
        }
        throw new SolverException(
            "no browser could reach the site (" + string.Join("; ", errors) + ")"
        );
    }

    private sealed class Session(CdpBrowser browser, ILogger log) : IInteractiveSession
    {
        private static readonly string[] ChallengeTitles =
        [
            "just a moment",
            "attention required",
            "verifying",
            "checking your browser",
        ];

        public async Task<SolveResult> GetAsync(
            string url,
            bool disableMedia = false,
            CancellationToken ct = default
        )
        {
            log.LogDebug("CDP GET {Url}", url);
            await browser.NavigateAsync(url, ct);
            await SettleAsync(ct);
            var html = await browser.HtmlAsync(ct) ?? "";
            var finalUrl = await browser.EvaluateAsync<string>("location.href", ct) ?? url;
            var ua = await browser.EvaluateAsync<string>("navigator.userAgent", ct) ?? "";
            log.LogInformation("CDP landed on {FinalUrl} ({Bytes} bytes)", finalUrl, html.Length);
            return new SolveResult(
                html,
                ua,
                new Dictionary<string, string>(),
                finalUrl,
                200,
                "cdp"
            );
        }

        /// <summary>Hoster gates navigate and then run their check; wait until the title stops being Cloudflare's.</summary>
        private async Task SettleAsync(CancellationToken ct)
        {
            for (var i = 0; i < 60; i++)
            {
                ct.ThrowIfCancellationRequested();
                var title = (await TitleAsync(ct) ?? "").ToLowerInvariant();
                var challenged = ChallengeTitles.Any(t =>
                    title.Contains(t, StringComparison.Ordinal)
                );
                if (!challenged)
                {
                    await Task.Delay(400, ct);
                    return;
                }
                await Task.Delay(500, ct);
            }
            log.LogWarning("CDP: the challenge did not clear at {Url}", browser.SessionId);
        }

        public Task<string?> TitleAsync(CancellationToken ct = default) =>
            browser.EvaluateAsync<string>("document.title", ct);

        public Task<IReadOnlyList<PageLink>> LinksAsync(CancellationToken ct = default) =>
            browser.LinksAsync(ct);

        public Task ClickAsync(string selector, CancellationToken ct = default) =>
            browser.ClickAsync(selector, ct);

        public Task<IReadOnlyDictionary<string, string>?> WaitForResponseAsync(
            string urlPart,
            TimeSpan timeout,
            CancellationToken ct = default
        ) => browser.WaitForResponseAsync(urlPart, timeout, ct);

        public Task<string?> WaitForRequestAsync(
            Func<string, bool> predicate,
            TimeSpan timeout,
            CancellationToken ct = default
        ) => browser.WaitForRequestAsync(predicate, timeout, ct);

        /// <summary>FlareSolverr-shaped actions, so the resolvers can share one code path.</summary>
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
                        var expression = script.TrimStart();
                        if (expression.StartsWith("return ", StringComparison.Ordinal))
                            expression = expression[7..];
                        last = await browser.EvaluateAsync(expression, ct);
                        break;
                    }
                    case "wait_for":
                    {
                        var selector = Property(action, "selector") ?? "";
                        var timeout = int.TryParse(Property(action, "timeout"), out var t)
                            ? t
                            : 30_000;
                        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeout);
                        while (DateTime.UtcNow < deadline)
                        {
                            var found = await browser.EvaluateAsync<bool?>(
                                "!!document.querySelector("
                                    + System.Text.Json.JsonSerializer.Serialize(
                                        selector.TrimStart('/', 'x', 'p', 'a', 't', 'h', ':')
                                    )
                                    + ")",
                                ct
                            );
                            if (found == true)
                                break;
                            // XPath selectors (the resolvers use "//a[...]") need a different call
                            var byXpath = await browser.EvaluateAsync<bool?>(
                                "!!document.evaluate("
                                    + System.Text.Json.JsonSerializer.Serialize(selector)
                                    + ", document, null, XPathResult.FIRST_ORDERED_NODE_TYPE, null).singleNodeValue",
                                ct
                            );
                            if (byXpath == true)
                                break;
                            await Task.Delay(250, ct);
                        }
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
                        log.LogDebug("CDP: ignoring action {Type}", type);
                        break;
                }
            }
            return last;
        }

        private static string? Property(object action, string name) =>
            action.GetType().GetProperty(name)?.GetValue(action)?.ToString();

        public ValueTask DisposeAsync() => browser.DisposeAsync();
    }
}
