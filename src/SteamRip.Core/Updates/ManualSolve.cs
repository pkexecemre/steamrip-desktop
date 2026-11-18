using Microsoft.Extensions.Logging;
using SteamRip.Core.Catalog;
using SteamRip.Core.Hosters;
using SteamRip.Core.Solver;

namespace SteamRip.Core.Updates;

/// <summary>
/// User-triggered fallback: open a visible browser on a mirror, let the user pass the challenge, and hand back the
/// direct link. BuzzHeavier needs this because its download endpoint answers a plain fetch with a Turnstile
/// challenge (<c>cf-mitigated: challenge</c>); once the check is passed in the window the page's own button gets the
/// <c>hx-redirect</c> that points at the file.
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
        var browser = new BrowserSolver(loggers?.CreateLogger<BrowserSolver>());
        await BrowserSolver.EnsureInstalledAsync(log);

        if (post.Mirrors.FirstOrDefault(m => m.Host == Hoster.BuzzHeavier) is { } buzz)
        {
            log?.LogInformation(
                "Browser fallback for {Name} via BuzzHeavier {Url}",
                post.Name,
                buzz.Url
            );
            var (redirect, title) = await browser.CaptureHxRedirectAsync(
                buzz.Url,
                "https://steamrip.com/",
                "//a[contains(@class,'download-btn')]",
                ct: ct
            );
            if (redirect is null)
                throw new NeedsAttentionException("The BuzzHeavier check was not passed.");
            var name = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
            log?.LogInformation("Browser fallback produced {File}", name ?? StringUnknown);
            return new ResolvedDownload(
                Hoster.BuzzHeavier,
                redirect,
                new Dictionary<string, string>(),
                name
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
        var solved = await browser.SolveAsync(mirror.Url, "https://steamrip.com/", ct: ct);
        var (url, expires, fileName) = HostPages.ParseFileDitch(solved.Html);
        log?.LogInformation(
            "Browser fallback produced {File} (expires {Expires:u})",
            fileName,
            expires
        );
        return new ResolvedDownload(
            Hoster.FileDitch,
            url,
            new Dictionary<string, string>(),
            fileName,
            ExpiresUtc: expires
        );
    }

    private const string StringUnknown = "(unknown name)";
}
