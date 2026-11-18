using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SteamRip.Core.Hosters;

namespace SteamRip.Core.Downloads;

/// <summary>Where a BitTorrent payload ended up: the directory aria2 wrote to and every file it produced.</summary>
public sealed record TorrentDownload(string Directory, IReadOnlyList<string> Files)
{
    /// <summary>The one file a single-file torrent produced, or null when it produced a folder.</summary>
    public string? SingleFile => Files.Count == 1 ? Files[0] : null;
}

/// <summary>
/// aria2c behind a JSON-RPC socket. Used instead of the built-in HTTP loop when it is installed: it splits a
/// transfer over up to 16 connections, resumes a <c>.part</c> from an earlier attempt, retries a host that closes
/// on us, verifies a published checksum itself, and — the reason it is here — turns the torrent mirrors some
/// hosters publish into downloads instead of a 113 GiB <c>.rar</c> we cannot open.
/// </summary>
/// <remarks>
/// aria2 runs as a child process with RPC on loopback, so progress, cancellation and the exit reason all come from
/// a status call rather than from scraping a progress line that is meant for a terminal.
/// </remarks>
public sealed partial class Aria2Client(ILogger<Aria2Client>? logger = null)
{
    private const int RpcPortAttempts = 8;

    private static readonly string[] StatusFields =
    [
        "status",
        "totalLength",
        "completedLength",
        "downloadSpeed",
        "errorMessage",
        "files",
    ];

    private static readonly string[] FileFields = ["files"];

    private readonly ILogger _log = logger ?? NullLogger<Aria2Client>.Instance;

    /// <summary>The aria2c on PATH, or null when it is not installed.</summary>
    public string? ExecutablePath { get; init; } = Find();

    public bool Available => ExecutablePath is not null;

    public string Describe() =>
        ExecutablePath is null
            ? "aria2: missing (built-in downloader)"
            : "aria2: " + ExecutablePath;

    /// <summary>HTTP(S) transfer with multi-connection + resume. Writes straight to <paramref name="destination"/> (+ aria2's own .aria2 control file).</summary>
    public async Task<string> DownloadAsync(
        ResolvedDownload src,
        string destination,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct
    )
    {
        if (ExecutablePath is null)
            throw new DownloadException("aria2c is not installed", retryResolve: false);
        var dir = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        var name = Path.GetFileName(destination);
        Directory.CreateDirectory(dir);

        var options = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["dir"] = dir,
            ["out"] = name,
            ["continue"] = "true",
            ["max-connection-per-server"] = "16",
            ["split"] = "16",
            ["min-split-size"] = "4M",
            ["max-tries"] = "5",
            ["retry-wait"] = "5",
        };
        var headers = new List<string>();
        foreach (var (k, v) in src.Headers)
            headers.Add(k + ": " + v);
        if (!src.Headers.ContainsKey("User-Agent"))
            headers.Add("User-Agent: " + Downloader.DefaultUserAgent);
        if (headers.Count > 0)
            options["header"] = string.Join("\n", headers);
        // Let aria2 verify the hash it was told about, and fail the transfer instead of handing us a bad file.
        if (src.Sha256 is { Length: > 0 })
            options["checksum"] = "sha-256=" + src.Sha256.ToLowerInvariant();
        else if (src.Md5 is { Length: > 0 })
            options["checksum"] = "md5=" + src.Md5.ToLowerInvariant();

        _log.LogInformation(
            "aria2 GET {Host} {Url} → {Dest} (checksum {Checksum}, headers {Headers})",
            src.Host,
            FileDitchResolver.Redact(src.Url),
            destination,
            options.GetValueOrDefault("checksum", "none"),
            string.Join(", ", src.Headers.Keys)
        );

        await using var session = await Aria2Session
            .StartAsync(ExecutablePath, dir, _log, ct)
            .ConfigureAwait(false);
        var gid = await session
            .CallAsync("aria2.addUri", [new[] { src.Url }, options], ct)
            .ConfigureAwait(false);
        var result = await session
            .WaitAsync(gid!, Path.GetFileName(destination), progress, torrent: false, ct)
            .ConfigureAwait(false);

        if (result.Files.Count == 0 || !File.Exists(destination))
        {
            throw new DownloadException(
                "aria2 reported success but " + name + " is not there",
                retryResolve: true
            );
        }
        _log.LogInformation(
            "aria2 finished {File}: {Bytes} bytes{(Checked)}",
            name,
            new FileInfo(destination).Length,
            options.ContainsKey("checksum") ? ", checksum verified by aria2" : ""
        );
        return destination;
    }

    /// <summary>Runs a local .torrent through aria2's BitTorrent engine (web seeds included) into <paramref name="dir"/>.</summary>
    public async Task<TorrentDownload> DownloadTorrentAsync(
        string torrentFile,
        string dir,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct
    )
    {
        if (ExecutablePath is null)
            throw new DownloadException(
                "This mirror is a torrent and aria2 is not installed. Install aria2 to download it.",
                retryResolve: false
            );
        Directory.CreateDirectory(dir);
        var base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(torrentFile, ct));
        _log.LogInformation(
            "aria2 torrent {Torrent} → {Dir} ({Bytes} bytes of metadata)",
            Path.GetFileName(torrentFile),
            dir,
            new FileInfo(torrentFile).Length
        );

        await using var session = await Aria2Session
            .StartAsync(ExecutablePath, dir, _log, ct)
            .ConfigureAwait(false);
        var options = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["dir"] = dir,
            ["seed-time"] = "0",
            ["bt-stop-timeout"] = "900",
            ["follow-torrent"] = "mem",
        };
        var gid = await session
            .CallAsync("aria2.addTorrent", [base64, Array.Empty<string>(), options], ct)
            .ConfigureAwait(false);
        var result = await session
            .WaitAsync(gid!, null, progress, torrent: true, ct)
            .ConfigureAwait(false);

        var files = result.Files.Where(File.Exists).ToList();
        if (files.Count == 0)
            throw new DownloadException(
                "the torrent finished without producing a file",
                retryResolve: true
            );
        _log.LogInformation(
            "aria2 torrent done: {Count} file(s) in {Dir} ({Bytes})",
            files.Count,
            dir,
            files.Count == 1
                ? new FileInfo(files[0]).Length + " bytes"
                : string.Join(", ", files.Select(f => Path.GetFileName(f)))
        );
        return new TorrentDownload(dir, files);
    }

    public static string? Find()
    {
        var exeNames = OperatingSystem.IsWindows() ? new[] { "aria2c.exe", "aria2c" } : ["aria2c"];
        List<string> dirs =
        [
            AppContext.BaseDirectory,
            .. (Environment.GetEnvironmentVariable("PATH") ?? "").Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries
            ),
        ];
        return dirs.SelectMany(d => exeNames.Select(e => Path.Combine(d, e)))
            .FirstOrDefault(File.Exists);
    }

    internal static string Fmt(long bytes) =>
        bytes switch
        {
            >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GiB",
            >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MiB",
            >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KiB",
            _ => bytes + " B",
        };

    /// <summary>aria2's sizes are decimal strings of bytes; this keeps the JSON parse in one place.</summary>
    internal static long Bytes(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

    /// <summary>One aria2c process with its RPC socket; disposes by shutting the process down.</summary>
    private sealed class Aria2Session : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly HttpClient _rpc;
        private readonly string _secret;
        private readonly ILogger _log;
        private readonly StringBuilder _stderr;
        private int _nextId;

        private Aria2Session(
            Process process,
            HttpClient rpc,
            string secret,
            ILogger log,
            StringBuilder stderr
        )
        {
            _process = process;
            _rpc = rpc;
            _secret = secret;
            _log = log;
            _stderr = stderr;
        }

        public string Stderr
        {
            get
            {
                lock (_stderr)
                    return _stderr.ToString();
            }
        }

        public static async Task<Aria2Session> StartAsync(
            string exe,
            string dir,
            ILogger log,
            CancellationToken ct
        )
        {
            var secret = Guid.NewGuid().ToString("N")[..16];
            for (var attempt = 0; attempt < RpcPortAttempts; attempt++)
            {
                var port = FreePort();
                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                foreach (var a in Arguments(dir, port, secret))
                    psi.ArgumentList.Add(a);
                var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                if (!process.Start())
                    throw new DownloadException("could not start aria2c", retryResolve: false);
                // aria2's own log goes to stderr; keep it for the failure message, drop the noise otherwise.
                var stderr = new StringBuilder();
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data is null)
                        return;
                    lock (stderr)
                    {
                        if (stderr.Length < 4000)
                            stderr.AppendLine(e.Data);
                    }
                    log.LogDebug("[aria2] {Line}", e.Data);
                };
                process.BeginErrorReadLine();
                process.OutputDataReceived += (_, _) => { };
                process.BeginOutputReadLine();

                var rpc = new HttpClient
                {
                    BaseAddress = new Uri($"http://127.0.0.1:{port}/jsonrpc"),
                };
                var session = new Aria2Session(process, rpc, secret, log, stderr);
                if (await session.WaitForRpcAsync(TimeSpan.FromSeconds(10), ct))
                    return session;
                var why = session.Stderr.Trim();
                await session.DisposeAsync().ConfigureAwait(false);
                log.LogWarning(
                    "aria2c RPC did not come up on port {Port} (exit {Code}); retrying. stderr: {Stderr}",
                    port,
                    process.HasExited
                        ? process.ExitCode.ToString(CultureInfo.InvariantCulture)
                        : "running",
                    why.Length > 500 ? why[^500..] : why
                );
            }
            throw new DownloadException(
                "aria2c did not start its RPC service; is another aria2 running?",
                retryResolve: false
            );
        }

        private static IEnumerable<string> Arguments(string dir, int port, string secret) =>
            [
                "--enable-rpc",
                "--rpc-listen-all=false",
                $"--rpc-listen-port={port}",
                $"--rpc-secret={secret}",
                "--rpc-max-request-size=32M",
                $"--stop-with-process={Environment.ProcessId}",
                $"--dir={dir}",
                "--continue=true",
                "--auto-file-renaming=false",
                "--allow-overwrite=true",
                "--file-allocation=none",
                "--max-connection-per-server=16",
                "--split=16",
                "--min-split-size=4M",
                "--max-tries=5",
                "--retry-wait=5",
                "--timeout=30",
                "--connect-timeout=15",
                "--lowest-speed-limit=1K",
                "--bt-max-peers=100",
                "--seed-time=0",
                "--summary-interval=0",
                "--console-log-level=warn",
                "--quiet=true",
            ];

        private static int FreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private async Task<bool> WaitForRpcAsync(TimeSpan timeout, CancellationToken ct)
        {
            var until = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < until)
            {
                if (_process.HasExited)
                    return false;
                try
                {
                    _ = await CallAsync("aria2.getVersion", [], ct, TimeSpan.FromSeconds(2))
                        .ConfigureAwait(false);
                    return true;
                }
                catch (Exception e)
                    when (e is HttpRequestException or TaskCanceledException or DownloadException)
                {
                    // not listening yet: the socket only opens once aria2 has parsed its arguments
                    await Task.Delay(150, ct).ConfigureAwait(false);
                }
            }
            return false;
        }

        /// <summary>JSON-RPC call; returns the raw <c>result</c> or throws <see cref="DownloadException"/> with aria2's message.</summary>
        public async Task<string?> CallAsync(
            string method,
            object[] parameters,
            CancellationToken ct,
            TimeSpan? timeout = null
        )
        {
            var id = Interlocked.Increment(ref _nextId).ToString(CultureInfo.InvariantCulture);
            var payload = new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = new object[] { "token:" + _secret }
                    .Concat(parameters)
                    .ToArray(),
            };
            using var json = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"
            );
            using var attempt = timeout is { } t ? new CancellationTokenSource(t) : null;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                ct,
                attempt?.Token ?? CancellationToken.None
            );
            HttpResponseMessage resp;
            try
            {
                resp = await _rpc.PostAsync("", json, linked.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException e)
            {
                throw new DownloadException("aria2 RPC: " + e.Message, retryResolve: false);
            }
            using (resp)
            {
                var body = await resp.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                if (
                    doc.RootElement.TryGetProperty("error", out var error)
                    && error.TryGetProperty("message", out var message)
                )
                    throw new DownloadException(
                        "aria2: " + message.GetString(),
                        retryResolve: false
                    );
                return doc.RootElement.TryGetProperty("result", out var result)
                    ? result.ValueKind == JsonValueKind.String
                        ? result.GetString()
                        : result.GetRawText()
                    : null;
            }
        }

        /// <summary>Polls the download until it completes, fails or is cancelled; feeds <paramref name="progress"/>.</summary>
        public async Task<TorrentDownload> WaitAsync(
            string gid,
            string? expectedName,
            IProgress<DownloadProgress>? progress,
            bool torrent,
            CancellationToken ct
        )
        {
            var stalledSince = DateTime.UtcNow;
            var lastBytes = 0L;
            var files = new List<string>();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var status = await TellAsync(gid, ct).ConfigureAwait(false);
                var state = status.GetValueOrDefault("status", "");
                var total = Bytes(status.GetValueOrDefault("totalLength"));
                var done = Bytes(status.GetValueOrDefault("completedLength"));
                var speed = Bytes(status.GetValueOrDefault("downloadSpeed"));
                if (done > lastBytes)
                {
                    lastBytes = done;
                    stalledSince = DateTime.UtcNow;
                }
                progress?.Report(new DownloadProgress(done, total > 0 ? total : null, speed));

                switch (state)
                {
                    case "complete":
                        files = await FilesAsync(gid, ct).ConfigureAwait(false);
                        if (
                            expectedName is not null
                            && files.Count > 0
                            && !files.Any(f =>
                                string.Equals(
                                    Path.GetFileName(f),
                                    expectedName,
                                    StringComparison.Ordinal
                                )
                            )
                        )
                            throw new DownloadException(
                                "aria2 wrote " + string.Join(", ", files.Select(Path.GetFileName)),
                                retryResolve: true
                            );
                        return new TorrentDownload(
                            files.Count > 0 ? Path.GetDirectoryName(files[0])! : ".",
                            files
                        );
                    case "error":
                        var message = status.GetValueOrDefault(
                            "errorMessage",
                            "unknown aria2 error"
                        );
                        _log.LogError("aria2 failed: {Message}", message);
                        throw new DownloadException(
                            "aria2: " + message,
                            retryResolve: message.Contains("403", StringComparison.Ordinal)
                                || message.Contains("404", StringComparison.Ordinal)
                        );
                    case "removed":
                        throw new DownloadException("aria2: download removed", retryResolve: false);
                    case "paused":
                        throw new DownloadException("aria2: download paused", retryResolve: true);
                }
                var stallLimit = torrent ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(3);
                if (DateTime.UtcNow - stalledSince > stallLimit && speed == 0)
                {
                    _log.LogWarning(
                        "aria2 stalled at {Done}/{Total} bytes for {Minutes:0} min; giving up on this link",
                        done,
                        total,
                        (DateTime.UtcNow - stalledSince).TotalMinutes
                    );
                    throw new DownloadException(
                        $"aria2 stalled at {Fmt(done)}/{Fmt(total)}"
                            + (torrent ? " (no peers or seeders)" : ""),
                        retryResolve: !torrent
                    );
                }
                await Task.Delay(torrent ? 2000 : 750, ct).ConfigureAwait(false);
            }
        }

        private async Task<Dictionary<string, string>> TellAsync(string gid, CancellationToken ct)
        {
            var raw = await CallAsync("aria2.tellStatus", [gid, StatusFields], ct)
                .ConfigureAwait(false);
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            if (raw is null)
                return fields;
            using var doc = JsonDocument.Parse(raw);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Name == "files")
                {
                    var list = property
                        .Value.EnumerateArray()
                        .Select(f => f.TryGetProperty("path", out var p) ? p.GetString() : null)
                        .Where(p => p is { Length: > 0 })
                        .Select(p => p!);
                    fields["files"] = string.Join('\n', list);
                    continue;
                }
                fields[property.Name] =
                    property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString() ?? ""
                        : property.Value.GetRawText();
            }
            return fields;
        }

        private async Task<List<string>> FilesAsync(string gid, CancellationToken ct)
        {
            var raw = await CallAsync("aria2.tellStatus", [gid, FileFields], ct)
                .ConfigureAwait(false);
            if (raw is null)
                return [];
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("files", out var filesEl))
                return [];
            return
            [
                .. filesEl
                    .EnumerateArray()
                    .Where(f =>
                        !f.TryGetProperty("selected", out var selected)
                        || selected.GetString() != "false"
                    )
                    .Select(f => f.TryGetProperty("path", out var p) ? p.GetString() : null)
                    .Where(p => p is { Length: > 0 })
                    .Select(p => p!),
            ];
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await CallAsync("aria2.forceShutdown", [], cts.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is DownloadException or OperationCanceledException)
            {
                // already gone, or the socket closed under us
            }
            _rpc.Dispose();
            try
            {
                if (!_process.WaitForExit(3000))
                    _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // exited between the check and the kill
            }
            _process.Dispose();
        }
    }
}
