using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SteamRip.Core.Catalog;
using SteamRip.Core.Solver;

namespace SteamRip.Core.Hosters;

/// <summary>
/// FileDitch returns its page after a WASM/JS check whose download control has no href until it is clicked: the link
/// is set at runtime. So: load the page in a real browser, try the parser first (older pages carry the signed URL),
/// then press the control and take the archive link that appears.
/// </summary>
public sealed class FileDitchResolver(
    IBrowserSolver solver,
    ILogger<FileDitchResolver>? logger = null
) : IHostResolver
{
    private readonly ILogger _log = logger ?? NullLogger<FileDitchResolver>.Instance;

    public Hoster Host => Hoster.FileDitch;

    public async Task<ResolvedDownload> ResolveAsync(
        string shareUrl,
        CancellationToken ct = default
    )
    {
        await using var session = await solver.CreateSessionAsync(ct);
        SolveResult page;
        try
        {
            page = await session.GetAsync(shareUrl, disableMedia: true, ct: ct);
        }
        catch (SolverException e)
        {
            throw new HostResolveException(Host, e.Message, e);
        }

        var direct = TryParse(page.Html);
        if (direct is not null)
            return direct;

        if (session is not IInteractiveSession live)
            throw new HostResolveException(
                Host,
                "the page needs a browser click to reveal its link and this session cannot click"
            );

        _log.LogInformation("FileDitch: page is gate-only; clicking its download control");
        // 90s, not 45: a gate served through the CDP fallback has been measured taking 39s to load
        // the page before the click even starts, and a timeout here costs the whole resolution.
        // The link appears either as an href the gate sets, or as the request the page fires at the file itself.
        var fromLink = live.WaitForArchiveLinkAsync(
            TimeSpan.FromSeconds(90),
            pageUrl: page.FinalUrl,
            ct: ct
        );
        var fromRequest = live.WaitForRequestAsync(
            u => InteractivePage.IsDownloadLink(u, page.FinalUrl),
            TimeSpan.FromSeconds(90),
            ct
        );
        await Task.WhenAny(fromLink, fromRequest);
        var link = await fromLink ?? await fromRequest;
        if (link is null)
        {
            // Some builds put the signed URL in the DOM right after the click; re-parse what the page holds now.
            var html = await live.ActionAsync(
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
                direct = TryParse(html);
        }
        if (direct is not null)
            return direct;
        if (link is null)
            throw new HostResolveException(
                Host,
                "no download link appeared after clicking (the gate rejected it)"
            );

        // The signed URL's path is a token, not a name; the page title carries the file name.
        var title = (await live.TitleAsync(ct))?.Trim();
        var fileName =
            title is { Length: > 0 } && Path.HasExtension(title)
                ? title
                : Path.GetFileName(new Uri(link).AbsolutePath);
        _log.LogInformation("FileDitch resolved {File} (from the page's own button)", fileName);
        return new ResolvedDownload(Host, link, new Dictionary<string, string>(), fileName);
    }

    private ResolvedDownload? TryParse(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;
        try
        {
            var (url, expires, fileName) = HostPages.ParseFileDitch(html);
            _log.LogInformation(
                "FileDitch resolved {File}: {Url} (expires {Expires:u})",
                fileName,
                Redact(url),
                expires
            );
            return new ResolvedDownload(
                Host,
                url,
                new Dictionary<string, string>(),
                fileName,
                ExpiresUtc: expires
            );
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Signed URLs are bearer tokens; keep the host but trim the token in logs.</summary>
    internal static string Redact(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
            ? $"{u.Scheme}://{u.Host}/…{u.AbsolutePath[^Math.Min(8, u.AbsolutePath.Length)..]}"
            : "<url>";
}
