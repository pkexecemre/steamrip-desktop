using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SteamRip.Core.Solver;

/// <summary>
/// Where a Chromium-family browser can be found. The app does not ship one: driving the browser the user already
/// has (or the Chromium Playwright put in the cache) costs nothing in package size, which matters when the whole
/// app is a single ~95 MB binary.
/// </summary>
public static class Browsers
{
    private static readonly string[] Names =
    [
        "chromium",
        "chromium-browser",
        "google-chrome",
        "google-chrome-stable",
        "brave-browser",
        "microsoft-edge",
        "vivaldi-stable",
    ];

    /// <summary>Every browser the app may drive, most preferred first.</summary>
    public static IReadOnlyList<string> All()
    {
        var found = new List<string>();
        foreach (var name in Names)
        {
            var path = Paths(name);
            if (path is not null && !found.Contains(path))
                found.Add(path);
        }
        var cache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache",
            "ms-playwright"
        );
        if (Directory.Exists(cache))
            found.AddRange(
                Directory
                    .EnumerateDirectories(cache, "chromium-*")
                    .SelectMany(d =>
                        Directory.EnumerateFiles(d, "chrome", SearchOption.AllDirectories)
                    )
                    .Where(File.Exists)
            );
        return found;
    }

    public static string? Find()
    {
        var all = All();
        return all.Count > 0 ? all[0] : null;
    }

    private static string? Paths(string name)
    {
        var exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        foreach (
            var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries
            )
        )
        {
            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }
}

/// <summary>One page as the resolver sees links and response headers on it.</summary>
public sealed record PageLink(string Href, string Text);

/// <summary>
/// Minimal DevTools-protocol client. Headless or visible Chromium is launched with a random debugging port; every
/// call is JSON over the WebSocket. Enough of CDP to load a hoster page, wait for its challenge script, click the
/// download control and read what the page's own request came back with — including response headers the page's
/// JavaScript is not allowed to see (the <c>hx-redirect</c> a download endpoint answers with).
/// </summary>
public sealed partial class CdpBrowser : IAsyncDisposable
{
    public const string UserAgent =
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/154.0.0.0 Safari/537.36";

    private readonly Process _process;
    private readonly ClientWebSocket _socket;
    private readonly string _profileDir;
    private readonly SemaphoreSlim _send = new(1);
    private readonly Dictionary<int, TaskCompletionSource<JsonElement>> _pending = [];
    private readonly Dictionary<string, string> _requestUrls = [];
    private readonly ILogger _log;
    private readonly CancellationTokenSource _pump = new();
    private int _nextId;

    private CdpBrowser(Process process, ClientWebSocket socket, string profileDir, ILogger log)
    {
        _process = process;
        _socket = socket;
        _profileDir = profileDir;
        _log = log;
        _ = Task.Run(() => PumpAsync(_pump.Token));
    }

    /// <summary>Browser target session id (a tab).</summary>
    public string SessionId { get; private set; } = "";

    public static async Task<CdpBrowser> LaunchAsync(
        string executable,
        bool headless,
        ILogger log,
        CancellationToken ct
    )
    {
        var profile = Path.Combine(
            Path.GetTempPath(),
            "sr-browser-" + Guid.NewGuid().ToString("N")[..10]
        );
        Directory.CreateDirectory(profile);
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = headless,
        };
        foreach (var a in Arguments(profile, headless))
            psi.ArgumentList.Add(a);
        var process = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            lock (stderr)
                if (stderr.Length < 4000)
                    stderr.AppendLine(e.Data);
        };
        process.Start();
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        // Everything after Start belongs to this method until a browser is returned: a cancellation or a
        // failed connect would otherwise leave a headless Chromium and its temp profile behind forever, and
        // the hourly retry adds another one each time.
        try
        {
            var wsUrl = await DevToolsUrlAsync(profile, TimeSpan.FromSeconds(20), ct);
            if (wsUrl is null)
            {
                throw new SolverException(
                    "the browser did not open a DevTools port ("
                        + (
                            stderr.Length > 400 ? stderr.ToString()[^400..] : stderr.ToString()
                        ).Trim()
                        + ")"
                );
            }
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(wsUrl), ct);
            var browser = new CdpBrowser(process, socket, profile, log) { SessionId = "" };
            await browser.AttachAsync(ct);
            log.LogInformation(
                "CDP: {Browser} ready at {Url}",
                Path.GetFileName(executable),
                wsUrl
            );
            return browser;
        }
        catch
        {
            TryKill(process);
            try
            {
                Directory.Delete(profile, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.LogDebug(
                    "CDP: could not remove the temp profile {Profile}: {Error}",
                    profile,
                    e.Message
                );
            }
            throw;
        }
    }

    private static IEnumerable<string> Arguments(string profile, bool headless)
    {
        yield return headless ? "--headless=new" : "--start-maximized";
        yield return "--remote-debugging-port=0";
        yield return "--user-data-dir=" + profile;
        yield return "--no-first-run";
        yield return "--no-default-browser-check";
        yield return "--disable-gpu";
        yield return "--disable-dev-shm-usage";
        yield return "--disable-blink-features=AutomationControlled";
        // Chromium honours the desktop's proxy settings; a dead corporate/system proxy makes every hoster look
        // unreachable (ERR_PROXY_CONNECTION_FAILED) while curl and Playwright's bundle go out directly.
        yield return "--no-proxy-server";
        yield return "--user-agent=" + UserAgent;
        yield return "--window-size=1366,900";
        yield return "about:blank";
    }

    /// <summary>The DevTools WebSocket URL, from the port file Chromium writes into the profile.</summary>
    private static async Task<string?> DevToolsUrlAsync(
        string profileDir,
        TimeSpan timeout,
        CancellationToken ct
    )
    {
        var file = Path.Combine(profileDir, "DevToolsActivePort");
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(file))
                {
                    var lines = await File.ReadAllLinesAsync(file, ct);
                    if (
                        lines.Length >= 2
                        && int.TryParse(lines[0], out var port)
                        && lines[1].StartsWith("/devtools/browser", StringComparison.Ordinal)
                    )
                        return $"ws://127.0.0.1:{port}{lines[1]}";
                }
            }
            catch (IOException)
            {
                // still being written
            }
            await Task.Delay(150, ct);
        }
        return null;
    }

    private async Task AttachAsync(CancellationToken ct)
    {
        var created = await SendAsync("Target.createTarget", new { url = "about:blank" }, null, ct);
        var targetId = created.TryGetProperty("targetId", out var t) ? t.GetString() : null;
        if (targetId is null)
            throw new SolverException("CDP could not create a tab");
        var attached = await SendAsync(
            "Target.attachToTarget",
            new { targetId, flatten = true },
            null,
            ct
        );
        SessionId = attached.TryGetProperty("sessionId", out var s) ? s.GetString() ?? "" : "";
        await SendAsync("Page.enable", new { }, SessionId, ct);
        await SendAsync("Network.enable", new { }, SessionId, ct);
        await SendAsync("Runtime.enable", new { }, SessionId, ct);
    }

    /// <summary>
    /// Navigates with the site as referer: every hoster here rejects a visit that looks like it came from nowhere
    /// (FileDitch bounces it to the homepage, BuzzHeavier hides its download row), and a per-navigation referrer is
    /// what a real click from steamrip.com would send.
    /// </summary>
    public async Task<string> NavigateAsync(
        string url,
        CancellationToken ct,
        string? referer = "https://steamrip.com/"
    )
    {
        await SendAsync(
            "Page.navigate",
            referer is null ? new { url } : new { url, referrer = referer },
            SessionId,
            ct
        );
        return await WaitForReadyAsync(TimeSpan.FromSeconds(45), ct);
    }

    /// <summary>Waits for a request whose URL satisfies <paramref name="predicate"/>; returns that URL.</summary>
    public async Task<string?> WaitForRequestAsync(
        Func<string, bool> predicate,
        TimeSpan timeout,
        CancellationToken ct
    )
    {
        var tcs = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        lock (_requestWatch)
            _requestWatch.Add((predicate, tcs));
        try
        {
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout, ct));
            return done == tcs.Task ? await tcs.Task : null;
        }
        finally
        {
            lock (_requestWatch)
                _requestWatch.RemoveAll(w => w.Tcs == tcs);
        }
    }

    private readonly List<(
        Func<string, bool> Predicate,
        TaskCompletionSource<string> Tcs
    )> _requestWatch = [];

    public string? LastNavigationError { get; private set; }

    /// <summary>Waits until the document is interactive again; hoster gates navigate several times.</summary>
    public async Task<string> WaitForReadyAsync(TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        var settle = 0;
        var last = "";
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            var state = await EvaluateAsync<string>("document.readyState", ct);
            var url = await EvaluateAsync<string>("location.href", ct) ?? "";
            if (state == "complete" && url == last)
            {
                if (++settle >= 2)
                    return url;
            }
            else
            {
                settle = 0;
                last = url;
            }
            await Task.Delay(250, ct);
        }
        return last;
    }

    public async Task<string?> EvaluateAsync(string expression, CancellationToken ct) =>
        await EvaluateAsync<string>(expression, ct);

    public async Task<T?> EvaluateAsync<T>(string expression, CancellationToken ct)
    {
        var result = await SendAsync(
            "Runtime.evaluate",
            new
            {
                expression,
                returnByValue = true,
                awaitPromise = true,
                userGesture = true,
            },
            SessionId,
            ct
        );
        if (result.TryGetProperty("exceptionDetails", out _))
            return default;
        if (!result.TryGetProperty("result", out var value))
            return default;
        if (!value.TryGetProperty("value", out var v))
            return default;
        if (typeof(T) == typeof(string))
            return v.ValueKind == JsonValueKind.String ? (T)(object)(v.GetString() ?? "") : default;
        object? primitive = v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.TryGetInt64(out var l) ? l : v.GetDouble(),
            _ => null,
        };
        if (primitive is T typed)
            return typed;
        return primitive is null ? default : v.Deserialize<T>(Json);
    }

    public Task<string?> HtmlAsync(CancellationToken ct) =>
        EvaluateAsync("document.documentElement.outerHTML", ct);

    /// <summary>Every visible link, so a caller can look for the file URL a gate set at runtime.</summary>
    public async Task<IReadOnlyList<PageLink>> LinksAsync(CancellationToken ct)
    {
        var json = await EvaluateAsync(
            "JSON.stringify(Array.from(document.querySelectorAll('a[href]')).map(a => ({h: a.href, t: (a.innerText||'').trim().slice(0,60)})))",
            ct
        );
        if (string.IsNullOrEmpty(json))
            return [];
        using var doc = JsonDocument.Parse(json);
        return
        [
            .. doc
                .RootElement.EnumerateArray()
                .Select(e => new PageLink(
                    e.GetProperty("h").GetString() ?? "",
                    e.TryGetProperty("t", out var t) ? t.GetString() ?? "" : ""
                )),
        ];
    }

    public async Task ClickAsync(string selector, CancellationToken ct)
    {
        var clicked = await EvaluateAsync<bool?>(
            "(() => { const e = document.querySelector("
                + JsonSerializer.Serialize(selector)
                + "); if (!e) return false; e.scrollIntoView({block:'center'}); e.click(); return true; })()",
            ct
        );
        if (clicked != true)
            throw new SolverException("nothing to click for " + selector);
    }

    /// <summary>Headers of the next response whose URL contains <paramref name="urlPart"/> (CDP sees what the page cannot).</summary>
    public async Task<IReadOnlyDictionary<string, string>?> WaitForResponseAsync(
        string urlPart,
        TimeSpan timeout,
        CancellationToken ct
    )
    {
        var tcs = new TaskCompletionSource<IReadOnlyDictionary<string, string>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        lock (_watched)
            _watched.Add((urlPart, tcs));
        try
        {
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout, ct));
            return done == tcs.Task ? await tcs.Task : null;
        }
        finally
        {
            lock (_watched)
                _watched.RemoveAll(w => w.Tcs == tcs);
        }
    }

    private readonly List<(
        string Part,
        TaskCompletionSource<IReadOnlyDictionary<string, string>> Tcs
    )> _watched = [];

    private async Task PumpAsync(CancellationToken ct)
    {
        var buffer = new byte[1 << 20];
        var accumulated = new MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                var result = await _socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                accumulated.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                    continue;
                var text = Encoding.UTF8.GetString(accumulated.ToArray());
                accumulated.SetLength(0);
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var id))
                {
                    TaskCompletionSource<JsonElement>? tcs;
                    lock (_pending)
                        _pending.Remove(id, out tcs);
                    if (tcs is null)
                        continue;
                    if (
                        root.TryGetProperty("error", out var err)
                        && err.TryGetProperty("message", out var msg)
                    )
                        tcs.TrySetException(new SolverException("CDP: " + msg.GetString()));
                    else
                        tcs.TrySetResult(
                            root.TryGetProperty("result", out var res) ? res.Clone() : default
                        );
                    continue;
                }
                if (root.TryGetProperty("method", out var method))
                    OnEvent(method.GetString() ?? "", root);
            }
        }
        catch (Exception e)
            when (e is WebSocketException or OperationCanceledException or JsonException)
        {
            _log.LogDebug("CDP pump ended: {Error}", e.Message);
        }
    }

    private void OnEvent(string method, JsonElement root)
    {
        if (!root.TryGetProperty("params", out var p))
            return;
        switch (method)
        {
            case "Network.requestWillBeSent":
            {
                if (
                    p.TryGetProperty("requestId", out var rid)
                    && p.TryGetProperty("request", out var req)
                    && req.TryGetProperty("url", out var urlEl)
                )
                {
                    var requested = urlEl.GetString() ?? "";
                    _requestUrls[rid.GetString() ?? ""] = requested;
                    List<(Func<string, bool> Predicate, TaskCompletionSource<string> Tcs)> hits;
                    lock (_requestWatch)
                        hits = _requestWatch.Where(w => w.Predicate(requested)).ToList();
                    foreach (var hit in hits)
                        hit.Tcs.TrySetResult(requested);
                }
                break;
            }
            case "Network.loadingFailed":
                if (
                    p.TryGetProperty("type", out var kindEl)
                    && kindEl.GetString() == "Document"
                    && p.TryGetProperty("errorText", out var errorEl)
                )
                {
                    LastNavigationError = errorEl.GetString();
                    _log.LogDebug("navigation failed: {Error}", LastNavigationError);
                }
                break;
            case "Network.responseReceivedExtraInfo":
            {
                if (!p.TryGetProperty("requestId", out var idEl))
                    break;
                var requestId = idEl.GetString() ?? "";
                if (!_requestUrls.TryGetValue(requestId, out var requestUrl))
                    break;
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (p.TryGetProperty("headers", out var hs))
                    foreach (var h in hs.EnumerateObject())
                        headers[h.Name] = h.Value.GetString() ?? "";
                List<(
                    string Part,
                    TaskCompletionSource<IReadOnlyDictionary<string, string>> Tcs
                )> hits;
                lock (_watched)
                    hits = _watched
                        .Where(w => requestUrl.Contains(w.Part, StringComparison.Ordinal))
                        .ToList();
                foreach (var hit in hits)
                    hit.Tcs.TrySetResult(headers);
                break;
            }
        }
    }

    private async Task<JsonElement> SendAsync(
        string method,
        object parameters,
        string? sessionId,
        CancellationToken ct
    )
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        lock (_pending)
            _pending[id] = tcs;
        var payload = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters,
        };
        if (sessionId is { Length: > 0 })
            payload["sessionId"] = sessionId;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        await _send.WaitAsync(ct);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _send.Release();
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var linked = ct.Register(() => tcs.TrySetCanceled(ct));
        var finished = await Task.WhenAny(
            tcs.Task,
            Task.Delay(TimeSpan.FromSeconds(45), timeout.Token)
        );
        if (finished != tcs.Task)
        {
            lock (_pending)
                _pending.Remove(id);
            throw new SolverException("CDP timed out on " + method);
        }
        return await tcs.Task;
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async ValueTask DisposeAsync()
    {
        _pump.Cancel();
        try
        {
            await _socket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "done",
                CancellationToken.None
            );
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        {
            // already closed
        }
        _socket.Dispose();
        TryKill(_process);
        try
        {
            Directory.Delete(_profileDir, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the OS cleans /tmp
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // gone
        }
        finally
        {
            process.Dispose();
        }
    }
}
