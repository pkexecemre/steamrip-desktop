using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SteamRip.Core.Launch;

namespace SteamRip.Core.Install;

/// <summary>What an install needs besides the archive. <c>PrefixDir</c> is the game's prefix, which lives outside the install folder: Goldberg/GSE saves inside it are backed up on update.</summary>
public sealed record InstallOptions(
    string LibraryRoot,
    int KeepBackups = 2,
    string? CustomInstallPath = null,
    string? PrefixDir = null,
    string? VersionKey = null
);

public sealed record InstallResult(
    string InstallPath,
    string? BackupPath,
    IReadOnlyList<string> BackedUpSaves,
    IReadOnlyList<string> PreservedPrefixes
);

/// <summary>
/// Install/update pipeline for one game: test archive → back up saves → extract into staging → swap folders (preserving Wine prefixes) → prune backups.
/// Nothing in here touches the network or the database.
/// </summary>
public sealed class Installer(ArchiveTool archive, ILogger<Installer>? logger = null)
{
    private readonly ILogger _log = logger ?? NullLogger<Installer>.Instance;
    public static readonly string[] NoiseDirs =
    [
        "_commonredist",
        "_redist",
        "redist",
        "__macosx",
        "_setup",
    ];

    public async Task<InstallResult> InstallAsync(
        string archivePath,
        string slug,
        long? steamAppId,
        string? oldVersion,
        InstallOptions opt,
        IProgress<(string Stage, int Percent)>? progress,
        CancellationToken ct
    )
    {
        var target = string.IsNullOrWhiteSpace(opt.CustomInstallPath)
            ? Path.Combine(opt.LibraryRoot, slug)
            : opt.CustomInstallPath;
        var staging = Path.Combine(opt.LibraryRoot, ".staging", slug);
        var backupsRoot = Path.Combine(opt.LibraryRoot, ".backups", slug);

        _log.LogInformation(
            "Install {Slug}: archive {Archive} ({Size} bytes) → {Target}; previous version {Old}",
            slug,
            archivePath,
            new FileInfo(archivePath).Length,
            target,
            oldVersion ?? "none"
        );
        progress?.Report(("Testing archive", 0));
        if (!await archive.TestAsync(archivePath, ct))
        {
            var kind = ArchiveTool.SniffKind(archivePath) ?? "unknown";
            ArchiveMarker.Clear(archivePath);
            _log.LogError(
                "Install {Slug}: {Archive} failed the integrity test (looks like {Kind}); it was not verified, so the job will re-download instead of reusing it",
                slug,
                Path.GetFileName(archivePath),
                kind
            );
            throw new InstallException(
                kind == "torrent"
                    ? "That mirror served a .torrent (BitTorrent metadata), not the game archive"
                    : $"Archive failed integrity test (looks like {kind} — corrupt or truncated download)"
            );
        }
        ArchiveMarker.Mark(archivePath, opt.VersionKey);

        progress?.Report(("Extracting", 0));
        if (Directory.Exists(staging))
            Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        try
        {
            await archive.ExtractAsync(
                archivePath,
                staging,
                new Progress<int>(p => progress?.Report(("Extracting", p))),
                ct
            );
        }
        catch
        {
            // A failed extraction leaves a partial tree (possibly tens of gigabytes) in the library's
            // staging folder; nothing refers to it yet, so drop it before rethrowing.
            try
            {
                Directory.Delete(staging, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(
                    "Install {Slug}: could not clean staging after a failed extraction: {Error}",
                    slug,
                    e.Message
                );
            }
            throw;
        }
        var extractedRoot = CollapseSingleDir(staging);
        _log.LogInformation(
            "Install {Slug}: extracted to {Staging}; root {Root}; top-level [{Entries}]",
            slug,
            staging,
            extractedRoot == staging ? "(flat)" : Path.GetFileName(extractedRoot),
            string.Join(
                ", ",
                Directory.GetFileSystemEntries(extractedRoot).Select(Path.GetFileName).Take(12)
            )
        );

        string? backupPath = null;
        var backedUp = new List<string>();
        var preserved = new List<string>();
        var swapped = false;
        if (Directory.Exists(target))
        {
            progress?.Report(("Backing up saves", 0));
            backupPath = Path.Combine(
                backupsRoot,
                SafeName(oldVersion ?? DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"))
            );
            if (Directory.Exists(backupPath))
                Directory.Delete(backupPath, true);
            foreach (var rel in SaveLocator.FindInGameFolder(target))
            {
                CopyDir(Path.Combine(target, rel), Path.Combine(backupPath, "game", rel));
                backedUp.Add(rel);
                _log.LogInformation("Install {Slug}: backed up in-folder saves {Rel}", slug, rel);
            }
            if (steamAppId is { } appId)
                foreach (
                    var (emuDir, i) in SaveLocator
                        .FindEmulatorSaves(appId, target, opt.PrefixDir)
                        .Select((d, i) => (d, i))
                )
                {
                    var dest = Path.Combine(backupPath, "emu", i.ToString(), appId.ToString());
                    CopyDir(emuDir, dest);
                    await File.WriteAllTextAsync(
                        Path.Combine(backupPath, "emu", i.ToString(), "source.txt"),
                        emuDir,
                        ct
                    );
                    backedUp.Add(emuDir);
                    _log.LogInformation(
                        "Install {Slug}: backed up emulator saves {Dir}",
                        slug,
                        emuDir
                    );
                }
            if (backedUp.Count == 0)
            {
                _log.LogInformation(
                    "Install {Slug}: no save-like directories found outside the Wine prefix",
                    slug
                );
                Directory.CreateDirectory(backupPath);
                await File.WriteAllTextAsync(
                    Path.Combine(backupPath, "no-saves-found.txt"),
                    "No save-like directories were found before this update.",
                    ct
                );
            }

            swapped = true;
            progress?.Report(("Replacing old install", 0));
            // Wine prefixes created by Heroic/Lutris inside the game folder hold the real saves; carry them over to the new tree.
            foreach (var prefix in SaveLocator.FindWinePrefixes(target))
            {
                var dest = Path.Combine(extractedRoot, prefix);
                if (Directory.Exists(dest))
                    Directory.Delete(dest, true);
                Directory.Move(Path.Combine(target, prefix), dest);
                preserved.Add(prefix);
                _log.LogInformation("Install {Slug}: preserved Wine prefix {Prefix}", slug, prefix);
            }
            // Also carry over in-folder saves so the player continues where they left off (the backup is the safety net).
            foreach (var rel in backedUp.Where(b => !Path.IsPathRooted(b)))
            {
                var src = Path.Combine(target, rel);
                var dest = Path.Combine(extractedRoot, rel);
                if (!Directory.Exists(src))
                    continue;
                if (Directory.Exists(dest))
                    Directory.Delete(dest, true);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                Directory.Move(src, dest);
            }
            // Swap on the target volume: rename the old tree aside, move the new one in with a copy
            // fallback (a rename cannot cross filesystems, and a custom install folder often lives on
            // another disk), and only delete the old tree once the new one is in place. A failure puts
            // the old install back instead of leaving the user with nothing.
            var aside =
                target.TrimEnd(Path.DirectorySeparatorChar)
                + ".old-"
                + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            PrefixManager.MoveDirectory(target, aside);
            try
            {
                PrefixManager.MoveDirectory(extractedRoot, target);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                try
                {
                    if (!Directory.Exists(target))
                        PrefixManager.MoveDirectory(aside, target);
                }
                catch (Exception restore)
                    when (restore is IOException or UnauthorizedAccessException)
                {
                    _log.LogError(
                        "Install {Slug}: could not restore the old install from {Aside}: {Error}",
                        slug,
                        aside,
                        restore.Message
                    );
                }
                throw new InstallException(
                    $"Could not move the new install into {target}: {e.Message}"
                );
            }
            try
            {
                Directory.Delete(aside, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(
                    "Install {Slug}: old install left at {Aside} ({Error})",
                    slug,
                    aside,
                    e.Message
                );
            }
            _log.LogInformation(
                "Install {Slug}: old install replaced; backup at {Backup}",
                slug,
                backupPath
            );
            PruneBackups(backupsRoot, opt.KeepBackups, _log);
        }

        if (!swapped)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            PrefixManager.MoveDirectory(extractedRoot, target);
        }
        if (Directory.Exists(staging))
            Directory.Delete(staging, true);
        progress?.Report(("Done", 100));
        _log.LogInformation("Install {Slug}: done → {Target}", slug, target);
        return new InstallResult(target, backupPath, backedUp, preserved);
    }

    /// <summary>If the archive extracted to a single folder (ignoring redist noise), return it; else the staging dir itself.</summary>
    public static string CollapseSingleDir(string staging)
    {
        var entries = Directory.GetFileSystemEntries(staging);
        var dirs = entries.Where(Directory.Exists).ToList();
        var files = entries.Where(File.Exists).ToList();
        var target = CollapseTarget(dirs, files);
        if (target is null)
            return staging;
        // Move noise dirs (redist) into the real dir so nothing is lost, then collapse.
        foreach (var noise in dirs.Where(d => Path.GetFileName(d) != target))
        {
            var dest = Path.Combine(staging, target, Path.GetFileName(noise));
            if (!Directory.Exists(dest))
                Directory.Move(noise, dest);
        }
        return Path.Combine(staging, target);
    }

    /// <summary>
    /// The folder the staging directory should collapse into, or null to keep it as it is: exactly one
    /// real folder and no loose files. Redist noise folders do not count.
    /// </summary>
    public static string? CollapseTarget(
        IReadOnlyList<string> directories,
        IReadOnlyList<string> files
    )
    {
        var realDirs = directories
            .Where(d => !NoiseDirs.Contains(Path.GetFileName(d).ToLowerInvariant()))
            .ToList();
        return files.Count == 0 && realDirs.Count == 1 ? Path.GetFileName(realDirs[0]) : null;
    }

    public static void PruneBackups(string backupsRoot, int keep, ILogger? log = null)
    {
        if (!Directory.Exists(backupsRoot))
            return;
        var dirs = new DirectoryInfo(backupsRoot)
            .GetDirectories()
            .OrderByDescending(d => d.LastWriteTimeUtc)
            .ToList();
        foreach (var old in dirs.Skip(Math.Max(keep, 0)))
        {
            log?.LogInformation("Pruning old save backup {Dir}", old.FullName);
            old.Delete(true);
        }
    }

    public static void CopyDir(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.EnumerateFiles(src))
            File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), true);
        foreach (var d in Directory.EnumerateDirectories(src))
        {
            if (new DirectoryInfo(d).LinkTarget is not null)
                continue;
            CopyDir(d, Path.Combine(dest, Path.GetFileName(d)));
        }
    }

    public static string SafeName(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var cleaned = new string(s.Select(c => bad.Contains(c) || c == ' ' ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "unknown" : cleaned;
    }
}

public sealed class InstallException(string message) : Exception(message);
