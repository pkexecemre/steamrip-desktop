namespace SteamRip.Core.Solver;

/// <summary>
/// The two things a hoster's download gate needs from a real page: press the control the page put there, and notice
/// the link it produced. Kept out of the resolvers so the manual (visible window) and automatic (headless) paths
/// behave the same.
/// </summary>
public static class InteractivePage
{
    private static readonly string[] ArchiveSuffixes =
    [
        ".rar",
        ".zip",
        ".7z",
        ".r00",
        ".7z.001",
        ".part1.rar",
        ".part01.rar",
    ];

    /// <summary>True when a URL points at an archive (query string ignored).</summary>
    public static bool LooksLikeArchive(string href)
    {
        var path = Uri.TryCreate(href, UriKind.Absolute, out var u) ? u.AbsolutePath : href;
        return ArchiveSuffixes.Any(s => path.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Clicks whatever looks like the page's download control (the hosters obfuscate the real one).</summary>
    public static Task ClickDownloadishAsync(
        this IInteractiveSession page,
        CancellationToken ct = default
    ) =>
        page.ActionAsync(
            [
                new
                {
                    type = "eval",
                    script = "(() => {"
                        + " const els = [...document.querySelectorAll('a,button,[role=button],[id],[class]')]"
                        + "   .filter(e => /btn|download|dl|indir|main/i.test(((e.innerText||'')+' '+(e.id||'')+' '+(e.className||''))));"
                        + " els.slice(0, 10).forEach(e => { try { e.click(); } catch (x) {} });"
                        + " return els.length;"
                        + " })()",
                    returnResult = true,
                },
            ],
            ct
        );

    /// <summary>A link that is actually the file: archives only, not the site's abuse form or its own share page.</summary>
    public static bool IsDownloadLink(string href, string? pageUrl = null)
    {
        if (!LooksLikeArchive(href))
            return false;
        if (!Uri.TryCreate(href, UriKind.Absolute, out var u))
            return false;
        if (u.Host.StartsWith("abuse.", StringComparison.OrdinalIgnoreCase))
            return false;
        if (u.AbsolutePath.Contains("/abuse", StringComparison.OrdinalIgnoreCase))
            return false;
        return pageUrl is null || !string.Equals(href, pageUrl, StringComparison.Ordinal);
    }

    /// <summary>Waits for the page to publish an archive link (gate scripts set it at runtime).</summary>
    public static async Task<string?> WaitForArchiveLinkAsync(
        this IInteractiveSession page,
        TimeSpan timeout,
        bool clickWhileWaiting = true,
        string? pageUrl = null,
        CancellationToken ct = default
    )
    {
        var until = DateTime.UtcNow + timeout;
        var lastClick = DateTime.MinValue;
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            // FileDitch's gate sets the href on its main button; the URL it uses is a signed redirect that need not
            // end in .rar, so the button is the reliable signal.
            var button = await page.ActionAsync(
                [
                    new
                    {
                        type = "eval",
                        script = "(() => { const e = document.querySelector('a.btn-main, .btn-main, #btn-main, a[class*=download], a[id^=dba]'); return e && e.href ? e.href : ''; })()",
                        returnResult = true,
                    },
                ],
                ct
            );
            if (
                button is { Length: > 0 }
                && !button.Contains("/abuse", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(button, pageUrl, StringComparison.Ordinal)
            )
                return button;
            var hit = (await page.LinksAsync(ct))
                .Select(l => l.Href)
                .FirstOrDefault(h => IsDownloadLink(h, pageUrl));
            if (hit is not null)
                return hit;
            if (clickWhileWaiting && (DateTime.UtcNow - lastClick).TotalSeconds >= 2)
            {
                lastClick = DateTime.UtcNow;
                await page.ClickDownloadishAsync(ct);
            }
            await Task.Delay(400, ct);
        }
        return null;
    }

    /// <summary>
    /// Clicks the control and returns the headers of the request it fired; the download endpoints answer with
    /// <c>hx-redirect</c>, which the page's own JS is not allowed to read but DevTools sees.
    /// </summary>
    public static async Task<string?> RedirectFromClickAsync(
        this IInteractiveSession page,
        string urlPart,
        TimeSpan timeout,
        CancellationToken ct = default
    )
    {
        var watch = page.WaitForResponseAsync(urlPart, timeout, ct);
        await Task.Delay(300, ct);
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until && !watch.IsCompleted)
        {
            try
            {
                await page.ClickDownloadishAsync(ct);
            }
            catch (SolverException)
            {
                // the control is not rendered yet
            }
            await Task.Delay(2000, ct);
        }
        var headers = await watch;
        return
            headers is not null
            && headers.TryGetValue("hx-redirect", out var redirect)
            && !string.IsNullOrWhiteSpace(redirect)
            ? redirect
            : null;
    }
}
