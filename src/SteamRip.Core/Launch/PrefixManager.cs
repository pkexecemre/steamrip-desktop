using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SteamRip.Core.Launch;

/// <summary>What the prefix directory looks like right now.</summary>
public enum PrefixHealth
{
    /// <summary>No directory: the prefix is created on first launch.</summary>
    Missing,

    /// <summary>Directory exists but Wine has not written its registry yet (created by us, never booted).</summary>
    Fresh,

    /// <summary><c>drive_c</c> + <c>system.reg</c> are there: a prefix games can run in.</summary>
    Ready,

    /// <summary>The record says this prefix was used before but the registry is gone: re-booting it starts over.</summary>
    Damaged,
}

/// <summary>
/// What a prefix was built with. Kept inside the prefix directory so moving or backing the folder up moves its
/// history with it. Append fields, never rename them: the file is read back across releases.
/// </summary>
public sealed record PrefixRecord
{
    public int Schema { get; init; } = 1;
    public DateTime CreatedUtc { get; init; }
    public DateTime LastUsedUtc { get; init; }
    public int Launches { get; init; }

    /// <summary>Proton as configured: a build directory or a keyword (<c>GE-Proton</c>, <c>DW-Proton</c>).</summary>
    public string? ProtonPath { get; init; }

    /// <summary>The build directory the launcher actually ran, which is what a "different Proton" warning is about.</summary>
    public string? ProtonBuild { get; init; }

    /// <summary>The umu <c>GAMEID</c> this prefix has been played under.</summary>
    public string? GameId { get; init; }
}

/// <summary>One prefix as the UI and the launcher see it: where it is, how healthy, how big, what built it.</summary>
public sealed record PrefixStatus(
    string Path,
    PrefixHealth Health,
    PrefixRecord? Record,
    long Bytes,
    int Files
)
{
    public string ProtonLabel =>
        PrefixManager.ProtonLabel(Record?.ProtonBuild ?? Record?.ProtonPath);

    /// <summary>One line for the UI: "512 MB · GE-Proton11-3 · 4 launches", or why there is nothing to show.</summary>
    public string Summary =>
        Health switch
        {
            PrefixHealth.Missing => "not created yet (first launch makes it)",
            PrefixHealth.Fresh => "created, not booted yet",
            PrefixHealth.Damaged => "registry missing — the next launch rebuilds it",
            _ when Record is { } r =>
                $"{PrefixManager.FormatSize(Bytes)} · {ProtonLabel} · {r.Launches} launch"
                    + (r.Launches == 1 ? "" : "es"),
            _ => $"{PrefixManager.FormatSize(Bytes)} · ready",
        };
}

/// <summary>
/// Owns everything about a game's Wine prefix: where it lives, what built it, whether another launch is already
/// using it, and how to get out of a broken one. Prefixes live outside the game folder by default
/// (<c>&lt;library&gt;/.prefixes/&lt;slug&gt;</c>) so an install/update swap, an uninstall or a "delete files" can
/// never take the saves with it; old in-folder prefixes are moved there on the next launch.
/// </summary>
public static class PrefixManager
{
    public const string SidecarName = ".steamrip-prefix.json";
    public const string LockName = ".steamrip-prefix.lock";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Default root: a sibling of the game folders, not inside them.</summary>
    public static string DefaultRoot(string libraryRoot) => Path.Combine(libraryRoot, ".prefixes");

    public static string Root(string libraryRoot, string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? DefaultRoot(libraryRoot) : configured.Trim();

    public static string Dir(string prefixRoot, string slug) =>
        Path.Combine(prefixRoot, SafeName(slug));

    /// <summary>A slug that is safe as a single path segment (posts can be titled "Foo/Bar").</summary>
    public static string SafeName(string slug)
    {
        var chars = slug.Trim()
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-')
            .ToArray();
        var name = new string(chars).Trim('.', '-');
        return name.Length == 0 ? "game" : name;
    }

    /// <summary>The pre-fork layout, <c>&lt;game&gt;/prefix</c>, but only when it really is a Wine prefix.</summary>
    public static string? LegacyDir(string gameRoot)
    {
        var dir = Path.Combine(gameRoot, "prefix");
        return Directory.Exists(dir) && LooksLikePrefix(dir) ? dir : null;
    }

    /// <summary>True when a directory has any of the things Wine writes at the root of a prefix.</summary>
    public static bool LooksLikePrefix(string dir) =>
        Directory.Exists(dir)
        && (
            File.Exists(Path.Combine(dir, "system.reg"))
            || File.Exists(Path.Combine(dir, "user.reg"))
            || Directory.Exists(Path.Combine(dir, "drive_c"))
            || Directory.Exists(Path.Combine(dir, "dosdevices"))
        );

    public static PrefixRecord? Read(string prefixDir)
    {
        try
        {
            var path = Path.Combine(prefixDir, SidecarName);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<PrefixRecord>(File.ReadAllText(path), Json)
                : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Write(string prefixDir, PrefixRecord record)
    {
        Directory.CreateDirectory(prefixDir);
        var path = Path.Combine(prefixDir, SidecarName);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(record, Json));
        File.Move(tmp, path, overwrite: true);
    }

    public static PrefixHealth Check(string prefixDir, PrefixRecord? record)
    {
        if (!Directory.Exists(prefixDir))
            return PrefixHealth.Missing;
        var reg = File.Exists(Path.Combine(prefixDir, "system.reg"));
        if (reg)
            return PrefixHealth.Ready;
        return record is { Launches: > 0 } ? PrefixHealth.Damaged : PrefixHealth.Fresh;
    }

    /// <summary>Full status for the UI: health, record, and how much disk the prefix takes.</summary>
    public static PrefixStatus Inspect(string prefixDir)
    {
        var record = Directory.Exists(prefixDir) ? Read(prefixDir) : null;
        var (bytes, files) = Measure(prefixDir);
        return new PrefixStatus(prefixDir, Check(prefixDir, record), record, bytes, files);
    }

    /// <summary>
    /// Resolves the prefix for a game, moving a legacy in-folder prefix out to <paramref name="prefixRoot"/> the
    /// first time it is seen. Never deletes anything: a move that fails leaves the old prefix where it was.
    /// </summary>
    public static string Prepare(
        string gameRoot,
        string prefixRoot,
        string slug,
        ILogger? log = null
    )
    {
        var dir = Dir(prefixRoot, slug);
        var legacy = LegacyDir(gameRoot);
        if (legacy is not null && !string.Equals(legacy, dir, StringComparison.Ordinal))
        {
            if (Directory.Exists(dir))
                log?.LogWarning(
                    "Two prefixes exist ({Legacy} and {New}); using the new one and leaving the old one alone",
                    legacy,
                    dir
                );
            else
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
                    MoveDirectory(legacy, dir);
                    log?.LogInformation(
                        "Moved the Wine prefix out of the game folder: {From} → {To} (an update or uninstall can no longer take it)",
                        legacy,
                        dir
                    );
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    log?.LogWarning(
                        "Could not move {From} to {To} ({Error}); keeping the prefix where it is",
                        legacy,
                        dir,
                        e.Message
                    );
                    return legacy;
                }
            }
        }
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// True when a prefix built by one Proton is about to be used by another (a GE-Proton update counts, because the
    /// build directory changes): Wine then needs <c>wineboot -u</c> to upgrade the prefix instead of confusing the game.
    /// </summary>
    public static bool NeedsUpgrade(string? previousBuild, string? currentBuild) =>
        previousBuild is not null
        && currentBuild is not null
        && !string.Equals(
            Path.GetFileName(previousBuild.TrimEnd(Path.DirectorySeparatorChar)),
            Path.GetFileName(currentBuild.TrimEnd(Path.DirectorySeparatorChar)),
            StringComparison.OrdinalIgnoreCase
        );

    /// <summary>Moves a prefix aside (<c>&lt;prefix&gt;.bak-&lt;utc&gt;</c>) so the next launch builds a clean one. Returns the backup path.</summary>
    public static string? Reset(string prefixDir, int keepBackups = 2)
    {
        if (!Directory.Exists(prefixDir))
            return null;
        var aside = AsidePath(prefixDir, DateTime.UtcNow);
        MoveDirectory(prefixDir, aside);
        PruneAside(prefixDir, keepBackups);
        return aside;
    }

    /// <summary>
    /// Deletes older <c>&lt;prefix&gt;.bak-*</c> siblings, keeping the newest <paramref name="keep"/>: a reset
    /// used to leave every backed-up prefix behind, and a prefix is not small.
    /// </summary>
    public static void PruneAside(string prefixDir, int keep)
    {
        var trimmed = prefixDir.TrimEnd(Path.DirectorySeparatorChar);
        var parent = Path.GetDirectoryName(trimmed);
        if (parent is null || !Directory.Exists(parent))
            return;
        var name = Path.GetFileName(trimmed);
        // The stamps are yyyyMMdd-HHmmss, so ordering by name is ordering by time.
        var old = Directory
            .GetDirectories(parent, name + ".bak-*")
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
            .Skip(Math.Max(keep, 0));
        foreach (var dir in old)
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // a locked file keeps this one; the next reset tries again
            }
        }
    }

    /// <summary>Where a reset prefix is moved to: sibling with a UTC stamp, so two resets never collide.</summary>
    public static string AsidePath(string prefixDir, DateTime utcNow) =>
        prefixDir.TrimEnd(Path.DirectorySeparatorChar)
        + ".bak-"
        + utcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    /// <summary><c>Directory.Move</c>, with a copy fallback for a prefix root on another filesystem.</summary>
    public static void MoveDirectory(string from, string to)
    {
        if (Directory.Exists(to))
            throw new IOException("Target already exists: " + to);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        try
        {
            Directory.Move(from, to);
        }
        catch (IOException)
        {
            CopyDirectory(from, to);
            Directory.Delete(from, true);
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from))
        {
            var target = Path.Combine(to, Path.GetFileName(dir));
            if (new DirectoryInfo(dir).LinkTarget is not null)
                Directory.CreateSymbolicLink(target, new DirectoryInfo(dir).LinkTarget!);
            else
                CopyDirectory(dir, target);
        }
        foreach (var file in Directory.EnumerateFiles(from))
        {
            var target = Path.Combine(to, Path.GetFileName(file));
            if (new FileInfo(file).LinkTarget is not null)
                File.CreateSymbolicLink(target, new FileInfo(file).LinkTarget!);
            else
                File.Copy(file, target);
        }
    }

    /// <summary>Bytes and file count under a prefix; ignores what cannot be read rather than failing.</summary>
    public static (long Bytes, int Files) Measure(string prefixDir)
    {
        if (!Directory.Exists(prefixDir))
            return (0, 0);
        long bytes = 0;
        var files = 0;
        var stack = new Stack<string>();
        stack.Push(prefixDir);
        var skipped = 0;
        while (stack.Count > 0 && skipped < 200)
        {
            var dir = stack.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        // A prefix is full of links into the host (dosdevices/z: -> /); counting through them
                        // reported terabytes for a 300 MB prefix.
                        bytes += info.LinkTarget is null ? info.Length : 0;
                        files++;
                    }
                    catch (IOException)
                    {
                        // vanished while counting (a game booting the prefix); not important enough to fail
                    }
                }
                foreach (var child in Directory.EnumerateDirectories(dir))
                {
                    if (new DirectoryInfo(child).LinkTarget is not null)
                        continue;
                    stack.Push(child);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                skipped++;
            }
        }
        return (bytes, files);
    }

    public static string FormatSize(long bytes) =>
        bytes switch
        {
            >= 1L << 30 => (bytes / (double)(1L << 30)).ToString(
                "0.0",
                CultureInfo.InvariantCulture
            ) + " GB",
            >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0", CultureInfo.InvariantCulture)
                + " MB",
            >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("0", CultureInfo.InvariantCulture)
                + " KB",
            _ => bytes.ToString(CultureInfo.InvariantCulture) + " B",
        };

    /// <summary>A short name for a Proton setting, for logs and the UI: the build directory, or the keyword.</summary>
    public static string ProtonLabel(string? protonPath) =>
        ProtonLabel(protonPath, keyword => ProtonLocator.ResolveInstalled(keyword));

    /// <summary>Same, with keyword resolution injected so the naming rule can be tested without a build on disk.</summary>
    public static string ProtonLabel(string? protonPath, Func<string, string?> resolveKeyword)
    {
        if (string.IsNullOrWhiteSpace(protonPath))
            return "umu default";
        var resolved = ProtonLocator.IsKeyword(protonPath)
            ? resolveKeyword(protonPath)
            : protonPath;
        return resolved is null
            ? protonPath
            : Path.GetFileName(resolved.TrimEnd(Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// The umu <c>GAMEID</c> for a game: Steam's id when the catalog knows it (that is what Proton's per-game
    /// fixes are keyed by), otherwise a stable id of our own so two games never share one.
    /// </summary>
    public static string GameId(long? steamAppId, string? slug) =>
        steamAppId is { } id ? $"umu-{id}"
        : !string.IsNullOrWhiteSpace(slug) ? "umu-steamrip-" + SafeName(slug).ToLowerInvariant()
        : "umu-default";

    /// <summary>
    /// Resolved build directory for a Proton setting (keywords resolve to the newest installed build), or null when
    /// it is not on disk yet. Used to notice that a prefix was created with a different Proton.
    /// </summary>
    public static string? ResolveBuild(string? protonPath) =>
        ResolveBuild(protonPath, keyword => ProtonLocator.ResolveInstalled(keyword), File.Exists);

    /// <summary>Same, with resolution and the proton-file check injected.</summary>
    public static string? ResolveBuild(
        string? protonPath,
        Func<string, string?> resolveKeyword,
        Func<string, bool> fileExists
    )
    {
        if (string.IsNullOrWhiteSpace(protonPath))
            return null;
        var dir = ProtonLocator.IsKeyword(protonPath) ? resolveKeyword(protonPath) : protonPath;
        return dir is not null && fileExists(Path.Combine(dir, "proton")) ? dir : null;
    }
}

/// <summary>
/// Cross-process claim on one prefix. Wine has exactly one <c>wineserver</c> per prefix, so a second game, a
/// redistributable install or a second app instance starting into the same prefix fights the first one: this turns
/// that into a clear "already in use" instead of a mystery. The lock is an advisory <c>flock</c> on
/// <see cref="PrefixManager.LockName"/>, so a crashed process never leaves a stuck prefix.
/// </summary>
public sealed class PrefixLease : IDisposable
{
    private readonly FileStream _stream;

    private PrefixLease(string dir, FileStream stream)
    {
        Dir = dir;
        _stream = stream;
    }

    public string Dir { get; }

    public static PrefixLease Acquire(string prefixDir)
    {
        Directory.CreateDirectory(prefixDir);
        var path = Path.Combine(prefixDir, PrefixManager.LockName);
        FileStream stream;
        try
        {
            stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 128,
                FileOptions.None
            );
        }
        catch (IOException)
        {
            throw new LaunchException(
                $"The Wine prefix {prefixDir} is in use by another launch. Wait for it to exit, or stop it first."
            );
        }
        try
        {
            stream.SetLength(0);
            var who = Encoding.UTF8.GetBytes(
                $"{Environment.ProcessId} {Environment.MachineName} {DateTime.UtcNow:O}\n"
            );
            stream.Write(who);
            stream.Flush();
            return new PrefixLease(prefixDir, stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Drops the flock. The lock file itself stays: deleting it would let two processes lock two different inodes for
    /// the same path and both believe they own the prefix.
    /// </summary>
    public void Dispose() => _stream.Dispose();
}
