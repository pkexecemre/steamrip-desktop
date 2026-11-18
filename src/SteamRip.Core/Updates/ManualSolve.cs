using Microsoft.Extensions.Logging;
using SteamRip.Core.Catalog;
using SteamRip.Core.Hosters;
using SteamRip.Core.Solver;

namespace SteamRip.Core.Updates;

/// <summary>
/// User-triggered fallback: open a visible browser on a mirror, let the user pass the check, and hand back the direct
/// link. BuzzHeavier gates its download endpoint with Turnstile and FileDitch sets the link from an obfuscated
/// click handler, so a plain HTTP request cannot reach either; a page the user can see can.
/// </summary>
public static class ManualSolve
{
    public static async Task<ResolvedDownload> ViaBrowserAsync(
        CatalogPost post,
        ILoggerFactory? loggers = null,
        CancellationToken ct = default
    )
    {
        var log = loggers?.CreateLogger("ManualSolve");
        var solver = new CdpSolver(loggers?.CreateLogger<CdpSolver>(), headless: false);
        if (!solver.Available)
            throw new NeedsAttentionException(
                "No Chromium-family browser is installed; install chromium to use the browser fallback."
            );
        await using var session = await solver.CreateSessionAsync(ct);
        if (session is not IInteractiveSession page)
            throw new NeedsAttentionException("The browser session cannot be driven.");

        if (post.Mirrors.FirstOrDefault(m => m.Host == Hoster.BuzzHeavier) is { } buzz)
        {
            log?.LogInformation(
                "Browser fallback for {Name} via BuzzHeavier {Url}; pass the check in the window",
                post.Name,
                buzz.Url
            );
            var landed = await page.GetAsync(buzz.Url, ct: ct);
            var title = (await page.TitleAsync(ct))?.Trim();
            var redirect = await page.RedirectFromClickAsync(
                "/download",
                TimeSpan.FromMinutes(3),
                ct
            );
            if (redirect is null)
                throw new NeedsAttentionException(
                    "The BuzzHeavier check was not passed in the window; nothing was downloaded."
                );
            log?.LogInformation("Browser fallback produced {File}", title ?? landed.FinalUrl);
            return new ResolvedDownload(
                Hoster.BuzzHeavier,
                redirect,
                new Dictionary<string, string>(),
                string.IsNullOrWhiteSpace(title) ? null : title
            );
        }

        var mirror =
            post.Mirrors.FirstOrDefault(m => m.Host == Hoster.FileDitch)
            ?? throw new NeedsAttentionException(
                "This post has no BuzzHeavier or FileDitch mirror; the browser fallback cannot help."
            );
        log?.LogInformation(
            "Browser fallback for {Name} via FileDitch {Url}",
            post.Name,
            mirror.Url
        );
        var fileDitchPage = await page.GetAsync(mirror.Url, ct: ct);
        var link = await page.WaitForArchiveLinkAsync(
            TimeSpan.FromMinutes(2),
            pageUrl: fileDitchPage.FinalUrl,
            ct: ct
        );
        if (link is null)
        {
            // The page may have rendered the real download button without a click; try the page's own parser first.
            var html = await page.ActionAsync(
                [
                    new
                    {
                        type = "eval",
                        script = "document.documentElement.outerHTML",
                        returnResult = true,
                    },
                ],
                ct
            );
            if (html is { Length: > 0 })
            {
                var (url, expires, fileName) = HostPages.ParseFileDitch(html);
                return new ResolvedDownload(
                    Hoster.FileDitch,
                    url,
                    new Dictionary<string, string>(),
                    fileName,
                    ExpiresUtc: expires
                );
            }
            throw new NeedsAttentionException(
                "FileDitch never produced a download link in the window; nothing was downloaded."
            );
        }
        var name = Path.GetFileName(new Uri(link).AbsolutePath);
        log?.LogInformation("Browser fallback produced {File}", name);
        return new ResolvedDownload(Hoster.FileDitch, link, new Dictionary<string, string>(), name);
    }
}
