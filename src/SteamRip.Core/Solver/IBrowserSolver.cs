namespace SteamRip.Core.Solver;

/// <summary>
/// Something that can get past a hoster's browser challenge and hand back the page, plus run a few scripted
/// steps inside it. Two implementations: FlareSolverr (when it is running) and the bundled headless Chromium.
/// </summary>
public interface IBrowserSolver
{
    Task<SolveResult> GetAsync(
        string url,
        bool disableMedia = false,
        CancellationToken ct = default
    );

    Task<IBrowserSession> CreateSessionAsync(CancellationToken ct = default);
}

/// <summary>One browser context that stays open across several steps (load, evaluate, wait).</summary>
public interface IBrowserSession : IAsyncDisposable
{
    Task<SolveResult> GetAsync(
        string url,
        bool disableMedia = false,
        CancellationToken ct = default
    );

    /// <summary>FlareSolverr-shaped action list (<c>eval</c>, <c>wait_for</c>, <c>wait</c>); returns the last eval result.</summary>
    Task<string?> ActionAsync(object[] actions, CancellationToken ct = default);
}
