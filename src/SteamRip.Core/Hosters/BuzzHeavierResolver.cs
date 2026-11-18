using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SteamRip.Core.Catalog;
using SteamRip.Core.Solver;

namespace SteamRip.Core.Hosters;

/// <summary>
/// BuzzHeavier gates on Referer == https://steamrip.com/ and Cloudflare, and its download endpoint answers a page's
/// own request with an <c>hx-redirect</c> header. That header is unreadable from JavaScript (opaque redirect), so it
/// is read from the browser's network layer instead: click the button, watch the response, take the link.
/// </summary>
public sealed class BuzzHeavierResolver(
    IBrowserSolver solver,
    ILogger<BuzzHeavierResolver>? logger = null
) : IHostResolver
{
    private readonly ILogger _log = logger ?? NullLogger<BuzzHeavierResolver>.Instance;

    public Hoster Host => Hoster.BuzzHeavier;

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

        var fileName = session is IInteractiveSession titled
            ? (await titled.TitleAsync(ct))?.Trim()
            : null;
        _log.LogDebug("BuzzHeavier landed at {Url} (title {Title})", page.FinalUrl, fileName);

        if (session is IInteractiveSession live)
        {
            var redirect = await live.RedirectFromClickAsync(
                "/download",
                TimeSpan.FromSeconds(45),
                ct
            );
            if (redirect is not null)
            {
                _log.LogInformation(
                    "BuzzHeavier resolved {File}: {Url}",
                    fileName ?? "?",
                    FileDitchResolver.Redact(redirect)
                );
                return new ResolvedDownload(
                    Host,
                    redirect,
                    new Dictionary<string, string>(),
                    string.IsNullOrWhiteSpace(fileName) ? null : fileName
                );
            }
            _log.LogWarning(
                "BuzzHeavier's download endpoint did not answer (page {Url}); use the browser fallback",
                page.FinalUrl
            );
            throw new HostResolveException(
                Host,
                page.FinalUrl.StartsWith("chrome-error", StringComparison.Ordinal)
                    ? "the browser could not load the page (" + page.FinalUrl + ")"
                    : "the download endpoint answered with a Cloudflare challenge; the visible-browser fallback can pass it"
            );
        }

        // Session types without network access (older FlareSolverr actions) can still try the in-page fetch.
        var inPage = await TryInPageFetchAsync(session, page, ct);
        if (inPage is not null)
            return inPage;
        throw new HostResolveException(Host, "no download link from BuzzHeavier");
    }

    private async Task<ResolvedDownload?> TryInPageFetchAsync(
        IBrowserSession session,
        SolveResult page,
        CancellationToken ct
    )
    {
        try
        {
            var html = page.Html;
            var (hxPath, md5, sha256, fileName) = HostPages.ParseBzzhr(html);
            var js =
                "window.__sr=null; fetch("
                + JsonSerializer.Serialize(hxPath)
                + ", {headers:{'HX-Request':'true','HX-Current-URL':location.href}, redirect:'manual'})"
                + ".then(r=>{window.__sr={status:r.status, redirect:r.headers.get('hx-redirect')}}).catch(e=>{window.__sr={error:String(e)}});";
            await session.ActionAsync(
                [
                    new
                    {
                        type = "eval",
                        script = js,
                        returnResult = false,
                    },
                    new { type = "wait", seconds = 2 },
                    new
                    {
                        type = "eval",
                        script = "return JSON.stringify(window.__sr)",
                        returnResult = true,
                    },
                ],
                ct
            );
            var raw = await session.ActionAsync(
                [
                    new
                    {
                        type = "eval",
                        script = "JSON.stringify(window.__sr)",
                        returnResult = true,
                    },
                ],
                ct
            );
            if (raw is null or "null")
                return null;
            using var doc = JsonDocument.Parse(raw);
            if (
                doc.RootElement.TryGetProperty("redirect", out var redirect)
                && redirect.GetString() is { Length: > 0 } url
            )
                return new ResolvedDownload(
                    Host,
                    url,
                    new Dictionary<string, string>(),
                    fileName,
                    Md5: md5,
                    Sha256: sha256
                );
        }
        catch (HostResolveException)
        {
            throw;
        }
        catch (Exception e) when (e is FormatException or JsonException or SolverException)
        {
            _log.LogDebug("BuzzHeavier in-page fallback failed: {Error}", e.Message);
        }
        return null;
    }
}
