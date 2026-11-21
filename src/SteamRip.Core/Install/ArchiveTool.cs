using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SteamRip.Core.Install;

/// <summary>Shells out to unrar/7z. Linux: whatever is on PATH. Windows: a bundled <c>7za.exe</c> next to the app, or 7-Zip's install dir.</summary>
public sealed partial class ArchiveTool(ILogger<ArchiveTool>? logger = null)
{
    [GeneratedRegex(@"(\d{1,3})%")]
    private static partial Regex Percent();

    [GeneratedRegex(@"^(?<base>.+)\.part(?<n>\d+)\.(?<ext>rar|7z|zip)$", RegexOptions.IgnoreCase)]
    private static partial Regex MultiPart();

    [GeneratedRegex(@"^(?<base>.+)\.r(?<n>\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex OldRarPart();

    [GeneratedRegex(@"^(?<base>.+)\.z(?<n>\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex ZipPart();

    [GeneratedRegex(@"^(?<base>.+)\.(?<ext>7z|zip|rar)\.(?<n>\d{3})$", RegexOptions.IgnoreCase)]
    private static partial Regex NumericSplit();

    /// <summary>
    /// The first volume of a split archive, given any volume: <c>Foo.part03.rar</c> → <c>Foo.part01.rar</c>,
    /// <c>Foo.r05</c> → <c>Foo.rar</c>, <c>Foo.7z.004</c> → <c>Foo.7z.001</c>. Unrar and 7z both open a split set
    /// from its first volume, so nothing else in the pipeline has to know the archive was split.
    /// </summary>
    public static string FirstPart(string archive) => FirstPart(archive, File.Exists);

    /// <summary>
    /// The same resolution with the existence check injected, so the split-volume rules can be
    /// exercised (and ported) without creating files on disk.
    /// </summary>
    internal static string FirstPart(string archive, Func<string, bool> exists)
    {
        var dir = Path.GetDirectoryName(archive) ?? ".";
        var name = Path.GetFileName(archive);
        var multi = MultiPart().Match(name);
        if (multi.Success)
        {
            var digits = multi.Groups["n"].Value;
            if (digits.Length > 0 && int.Parse(digits, CultureInfo.InvariantCulture) <= 1)
                return archive;
            var first = Path.Combine(
                dir,
                multi.Groups["base"].Value
                    + ".part"
                    + "1".PadLeft(digits.Length, '0')
                    + "."
                    + multi.Groups["ext"].Value
            );
            return exists(first) ? first : archive;
        }
        var old = OldRarPart().Match(name);
        if (old.Success)
        {
            var rar = Path.Combine(dir, old.Groups["base"].Value + ".rar");
            return exists(rar) ? rar : archive;
        }
        var zip = ZipPart().Match(name);
        if (zip.Success)
        {
            var target = Path.Combine(dir, zip.Groups["base"].Value + ".zip");
            return exists(target) ? target : archive;
        }
        var numeric = NumericSplit().Match(name);
        if (
            numeric.Success
            && int.Parse(numeric.Groups["n"].Value, CultureInfo.InvariantCulture) > 1
        )
        {
            var target = Path.Combine(
                dir,
                numeric.Groups["base"].Value + "." + numeric.Groups["ext"].Value + ".001"
            );
            return File.Exists(target) ? target : archive;
        }
        return archive;
    }

    /// <summary>Every percentage the tool printed in a chunk, in order, without the state the caller keeps.</summary>
    public static IReadOnlyList<int> PercentValues(string chunk) =>
        [
            .. Percent()
                .Matches(chunk)
                .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)),
        ];

    /// <summary>What the first bytes of a file say it is: <c>rar</c>, <c>7z</c>, <c>zip</c>, <c>torrent</c>, <c>html</c>, else null.</summary>
    public static string? SniffKind(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var head = new byte[8];
            var read = fs.Read(head, 0, head.Length);
            return SniffKind(head.AsSpan(0, read));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// What the first bytes say a file is, without touching the disk: the rules are pure, so the
    /// recorder and the Rust port can run the same cases.
    /// </summary>
    public static string? SniffKind(ReadOnlySpan<byte> head)
    {
        if (head.Length < 2)
            return null;
        if (
            head.Length >= 4
            && head[0] == 'R'
            && head[1] == 'a'
            && head[2] == 'r'
            && head[3] == '!'
        )
            return "rar";
        if (head.Length >= 6 && head[0] == 0x37 && head[1] == 0x7A && head[2] == 0xBC)
            return "7z";
        if (head[0] == 'P' && head[1] == 'K')
            return "zip";
        if (head.Length >= 3 && head[0] == 'd' && head[1] == '8' && head[2] == ':')
            return "torrent";
        if (head.Length >= 5 && (head[0] == (byte)'<' || (head[0] == 0xEF && head[1] == 0xBB)))
            return "html";
        return null;
    }

    /// <summary>A file an archive tool can actually open (RAR, 7z, ZIP and their split volumes).</summary>
    public static bool LooksLikeArchive(string path) => SniffKind(path) is "rar" or "7z" or "zip";

    /// <summary>A BitTorrent metadata file, which some hosters serve in place of the archive itself.</summary>
    public static bool IsTorrent(string path) =>
        SniffKind(path) == "torrent"
        || path.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);

    private readonly ILogger _log = logger ?? NullLogger<ArchiveTool>.Instance;

    public string? UnrarPath { get; init; } = Find("unrar");
    public string? SevenZipPath { get; init; } =
        Find("7z") ?? Find("7za") ?? Find("7zz") ?? Find("7zr");

    public string Describe() => $"unrar: {UnrarPath ?? "missing"}, 7z: {SevenZipPath ?? "missing"}";

    /// <summary>
    /// Verifies the archive by decompressing all of it. On a 100 GB game that is minutes of work,
    /// so the percentage is passed through: a stage stuck at 0% looked like a hang.
    /// </summary>
    public async Task<bool> TestAsync(string archive, IProgress<int>? percent, CancellationToken ct)
    {
        archive = FirstPart(archive);
        (string exe, string[] args) tool =
            UnrarPath is not null && archive.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)
                ? (UnrarPath, ["t", "-y", archive])
                : (
                    SevenZipPath ?? throw new InvalidOperationException("No archive tool found"),
                    ["t", archive, "-y", "-bso0", "-bsp1"]
                );
        var code = await RunAsync(tool.exe, tool.args, percent, ct);
        return code == 0;
    }

    public async Task ExtractAsync(
        string archive,
        string destination,
        IProgress<int>? percent,
        CancellationToken ct
    )
    {
        Directory.CreateDirectory(destination);
        archive = FirstPart(archive);
        (string exe, string[] args) tool =
            UnrarPath is not null && archive.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)
                ? (
                    UnrarPath,
                    [
                        "x",
                        "-y",
                        "-o+",
                        archive,
                        destination.TrimEnd('/', '\\') + Path.DirectorySeparatorChar,
                    ]
                )
                : (
                    SevenZipPath ?? throw new InvalidOperationException("No archive tool found"),
                    ["x", archive, "-o" + destination, "-y", "-bsp1", "-bso0"]
                );
        var code = await RunAsync(tool.exe, tool.args, percent, ct);
        if (code != 0)
            throw new InvalidOperationException($"{Path.GetFileName(tool.exe)} exited with {code}");
    }

    private async Task<int> RunAsync(
        string exe,
        string[] args,
        IProgress<int>? percent,
        CancellationToken ct
    )
    {
        var sw = Stopwatch.StartNew();
        _log.LogInformation(
            "Running: {Exe} {Args}",
            exe,
            string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))
        );
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p =
            Process.Start(psi) ?? throw new InvalidOperationException("failed to start " + exe);
        var last = -1;
        var tail = new StringBuilder();
        var reader = Task.Run(
            // ReSharper disable once AccessToDisposedClosure -- awaited before the process is disposed
            async () =>
            {
                var buf = new char[256];
                int n;
                while ((n = await p.StandardOutput.ReadAsync(buf, ct)) > 0)
                {
                    var chunk = new string(buf, 0, n);
                    if (tail.Length < 4000)
                        tail.Append(chunk);
                    foreach (var v in PercentValues(chunk))
                    {
                        if (v == last || v > 100)
                            continue;
                        last = v;
                        percent?.Report(v);
                    }
                }
            },
            ct
        );
        var errTask = p.StandardError.ReadToEndAsync(ct);
        // ReSharper disable once AccessToDisposedClosure -- registration is disposed before the process
        await using var reg = ct.Register(() =>
        {
            try
            {
                p.Kill(true);
            }
            catch
            {
                // already exited between the cancel and the kill
            }
        });
        await p.WaitForExitAsync(ct);
        await Task.WhenAll(reader, errTask);
        var stderr = errTask.Result.Trim();
        if (p.ExitCode == 0)
            _log.LogInformation(
                "{Exe} exited 0 in {Seconds:0.0}s",
                Path.GetFileName(exe),
                sw.Elapsed.TotalSeconds
            );
        else
            _log.LogError(
                "{Exe} exited {Code} in {Seconds:0.0}s\nstderr: {Stderr}\nstdout (tail): {Stdout}",
                Path.GetFileName(exe),
                p.ExitCode,
                sw.Elapsed.TotalSeconds,
                stderr.Length > 2000 ? stderr[^2000..] : stderr,
                tail.Length > 1500 ? tail.ToString()[^1500..] : tail.ToString()
            );
        return p.ExitCode;
    }

    public static string? Find(string name)
    {
        var exeNames = OperatingSystem.IsWindows() ? new[] { name + ".exe", name } : new[] { name };
        List<string> dirs =
        [
            AppContext.BaseDirectory,
            Path.Combine(AppContext.BaseDirectory, "tools"),
            .. (Environment.GetEnvironmentVariable("PATH") ?? "").Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries
            ),
        ];
        if (OperatingSystem.IsWindows())
            dirs.AddRange([
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "7-Zip"
                ),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "7-Zip"
                ),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "WinRAR"
                ),
            ]);
        return dirs.SelectMany(d => exeNames.Select(e => Path.Combine(d, e)))
            .FirstOrDefault(File.Exists);
    }
}
