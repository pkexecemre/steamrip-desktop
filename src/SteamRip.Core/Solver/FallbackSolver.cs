using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SteamRip.Core.Solver;

/// <summary>
/// Prefers a running FlareSolverr and falls back to the bundled headless Chromium when there is none (or when it
/// errors). The fallback is what makes the direct hosters work on a machine where only the app is installed: without
/// it, FileDitch and BuzzHeavier — the mirrors that actually serve the pre-installed archives — cannot resolve at all.
/// </summary>
public sealed class FallbackSolver(
    FlareSolverrClient flare,
    IBrowserSolver headless,
    ILogger<FallbackSolver>? logger = null
) : IBrowserSolver
{
    private readonly ILogger _log = logger ?? NullLogger<FallbackSolver>.Instance;
    private bool? _flareOk;
    private DateTime _checkedUtc = DateTime.MinValue;

    private async Task<bool> FlareOkAsync(CancellationToken ct)
    {
        if (_flareOk is { } known && DateTime.UtcNow - _checkedUtc < TimeSpan.FromMinutes(1))
            return known;
        var ok = await flare.IsHealthyAsync(ct: ct);
        _flareOk = ok;
        _checkedUtc = DateTime.UtcNow;
        _log.Log(
            ok ? LogLevel.Debug : LogLevel.Information,
            ok
                ? "FlareSolverr answers at {Url}"
                : "FlareSolverr does not answer at {Url}; using the built-in headless browser for challenge pages",
            flare.BaseUrl
        );
        return ok;
    }

    public async Task<SolveResult> GetAsync(
        string url,
        bool disableMedia = false,
        CancellationToken ct = default
    )
    {
        if (await FlareOkAsync(ct))
        {
            try
            {
                return await flare.GetAsync(url, disableMedia: disableMedia, ct: ct);
            }
            catch (SolverException e)
            {
                _log.LogWarning(
                    "FlareSolverr failed on {Url} ({Error}); retrying with the headless browser",
                    url,
                    e.Message
                );
            }
        }
        return await headless.GetAsync(url, disableMedia, ct);
    }

    public async Task<IBrowserSession> CreateSessionAsync(CancellationToken ct = default)
    {
        if (await FlareOkAsync(ct))
        {
            try
            {
                return await flare.CreateSessionAsync(ct);
            }
            catch (Exception e) when (e is SolverException or HttpRequestException)
            {
                _log.LogWarning(
                    "FlareSolverr session failed ({Error}); using the headless browser",
                    e.Message
                );
                _flareOk = false;
            }
        }
        return await headless.CreateSessionAsync(ct);
    }
}
