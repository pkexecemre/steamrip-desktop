using System.Globalization;

namespace SteamRip.Core.Install;

/// <summary>Finds save-like directories: inside the game folder (heuristic names) and in Goldberg/GSE emulator dirs under Wine prefixes keyed by Steam app id.</summary>
public static class SaveLocator
{
    public static readonly string[] SaveDirNames =
    [
        "Save",
        "Saves",
        "SaveGames",
        "SaveGame",
        "SaveData",
        "Savegames",
        "Profiles",
        "Saved",
        "UserData",
        "Savedata",
    ];
    public static readonly string[] EmuSaveDirNames = ["GSE Saves", "Goldberg SteamEmu Saves"];
    private static readonly string[] SkipDirs =
    [
        "_CommonRedist",
        "_Redist",
        "redist",
        "drive_c",
        "Engine",
        "Content",
        "Binaries",
        "Shaders",
        "Win64",
        "Win32",
    ];
    private static readonly string[] SkipBelowRoot = ["Data", "data", "Resources", "Plugins"];

    /// <summary>One entry of a game-folder snapshot: path relative to the root, and what it is.</summary>
    public readonly record struct TreeEntry(string RelativePath, bool IsDirectory, bool IsSymlink);

    /// <summary>Save-like dirs inside <paramref name="gameRoot"/> (relative paths). Shallow walk (depth ≤ 4), skipping Wine prefixes and bulky engine dirs.</summary>
    public static IReadOnlyList<string> FindInGameFolder(string gameRoot, int maxDepth = 4)
    {
        if (!Directory.Exists(gameRoot))
            return [];
        return SelectInGameFolder(Snapshot(gameRoot, maxDepth), maxDepth);
    }

    /// <summary>
    /// The walk's decision over a snapshot of the tree, in the order the directories are listed.
    /// Save-named directories are selected before the depth cutoff, so a save folder on the cap still counts.
    /// </summary>
    public static IReadOnlyList<string> SelectInGameFolder(
        IReadOnlyList<TreeEntry> entries,
        int maxDepth = 4
    )
    {
        var results = new List<string>();
        Walk("", 0);
        return results;

        void Walk(string parent, int depth)
        {
            foreach (var child in Children(entries, parent))
            {
                if (!child.IsDirectory || child.IsSymlink)
                    continue;
                var name = LeafName(child.RelativePath);
                if (IsSaveDirName(name))
                {
                    results.Add(child.RelativePath);
                    continue;
                }
                if (depth + 1 >= maxDepth)
                    continue;
                if (IsSkippedDir(name, depth))
                    continue;
                if (IsWinePrefixIn(entries, child.RelativePath))
                    continue;
                Walk(child.RelativePath, depth + 1);
            }
        }
    }

    /// <summary>
    /// Bounded snapshot of the directories under <paramref name="gameRoot"/>, plus the system.reg files
    /// the Wine-prefix check needs. It stops at <paramref name="maxDepth"/> without applying the walk's
    /// pruning, so the snapshot stays depth-bounded even inside a prefix.
    /// </summary>
    private static List<TreeEntry> Snapshot(string gameRoot, int maxDepth)
    {
        var entries = new List<TreeEntry>();
        Walk(new DirectoryInfo(gameRoot), "", 0);
        return entries;

        void Walk(DirectoryInfo dir, string relative, int depth)
        {
            if (depth >= Math.Max(maxDepth, 1))
                return;
            // Enumerate eagerly: the enumeration itself is what throws for an unreadable directory, and a
            // lazy EnumerateDirectories() would move that failure into the loop below, where nothing catches
            // it and the whole update dies on one bad folder.
            List<DirectoryInfo> children;
            try
            {
                children = [.. dir.EnumerateDirectories()];
            }
            catch (Exception e)
                when (e
                        is IOException
                            or UnauthorizedAccessException
                            or System.Security.SecurityException
                )
            {
                return;
            }
            if (File.Exists(Path.Combine(dir.FullName, "system.reg")))
                entries.Add(new TreeEntry(Path.Combine(relative, "system.reg"), false, false));
            foreach (var child in children)
            {
                var childRelative = Path.Combine(relative, child.Name);
                entries.Add(new TreeEntry(childRelative, true, child.LinkTarget is not null));
                Walk(child, childRelative, depth + 1);
            }
        }
    }

    /// <summary>True for a directory name that looks like a save folder (exact list, or any Save prefix).</summary>
    public static bool IsSaveDirName(string name) =>
        SaveDirNames.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase))
        || name.StartsWith("Save", StringComparison.OrdinalIgnoreCase);

    /// <summary>Directories the save walk does not descend into: engine/redist bulk, and the deep-only ones.</summary>
    public static bool IsSkippedDir(string name, int depth) =>
        SkipDirs.Any(s => s.Equals(name, StringComparison.OrdinalIgnoreCase))
        || (
            depth > 0 && SkipBelowRoot.Any(s => s.Equals(name, StringComparison.OrdinalIgnoreCase))
        );

    public static bool IsWinePrefix(string dir) =>
        Directory.Exists(Path.Combine(dir, "drive_c"))
        || File.Exists(Path.Combine(dir, "system.reg"));

    private static readonly char[] PathSeparators = ['/', '\\'];

    /// <summary>Entries directly inside <paramref name="parent"/>, in snapshot order.</summary>
    private static IEnumerable<TreeEntry> Children(
        IReadOnlyList<TreeEntry> entries,
        string parent
    ) => entries.Where(entry => ParentPath(entry.RelativePath) == parent);

    private static string LeafName(string path)
    {
        var index = path.LastIndexOfAny(PathSeparators);
        return index < 0 ? path : path[(index + 1)..];
    }

    private static string ParentPath(string path)
    {
        var index = path.LastIndexOfAny(PathSeparators);
        return index < 0 ? "" : path[..index];
    }

    /// <summary>The snapshot's answer to <see cref="IsWinePrefix"/>: drive_c as a directory or system.reg as a file.</summary>
    private static bool IsWinePrefixIn(IReadOnlyList<TreeEntry> entries, string dir) =>
        Children(entries, dir)
            .Any(entry =>
                (entry.IsDirectory && LeafName(entry.RelativePath) == "drive_c")
                || (!entry.IsDirectory && LeafName(entry.RelativePath) == "system.reg")
            );

    /// <summary>Directories inside <paramref name="gameRoot"/> that are Wine prefixes. These must survive a reinstall.</summary>
    public static IReadOnlyList<string> FindWinePrefixes(string gameRoot)
    {
        if (!Directory.Exists(gameRoot))
            return [];
        return
        [
            .. Directory
                .EnumerateDirectories(gameRoot)
                .Where(IsWinePrefix)
                .Select(d => Path.GetFileName(d)),
        ];
    }

    /// <summary>
    /// Goldberg/GSE save dirs for <paramref name="steamAppId"/> inside the game's own Wine prefix(es) plus
    /// <paramref name="extraPrefix"/>, which is where the app keeps the prefix now; on Windows, the real user profile.
    /// </summary>
    public static IReadOnlyList<string> FindEmulatorSaves(
        long steamAppId,
        string gameRoot,
        string? extraPrefix = null
    )
    {
        var prefixes = FindWinePrefixes(gameRoot)
            .Select(prefix => Path.Combine(gameRoot, prefix))
            .ToList();
        if (extraPrefix is not null && IsWinePrefix(extraPrefix))
            prefixes.Add(extraPrefix);
        var userDirs = prefixes
            .Select(prefix => Path.Combine(prefix, "drive_c", "users"))
            .Where(Directory.Exists)
            .SelectMany(Directory.EnumerateDirectories)
            .ToList();
        if (OperatingSystem.IsWindows())
            userDirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        return [.. EmulatorSavePaths(userDirs, steamAppId).Where(Directory.Exists)];
    }

    /// <summary>
    /// Where Goldberg/GSE keep their saves for one app id, for each user directory, in the order
    /// they are probed. Kept pure so the layout can be checked without a prefix.
    /// </summary>
    public static IReadOnlyList<string> EmulatorSavePaths(
        IEnumerable<string> userDirs,
        long steamAppId
    ) =>
        [
            .. userDirs
                .Distinct()
                .SelectMany(user =>
                    EmuSaveDirNames.Select(emu =>
                        Path.Combine(
                            user,
                            "AppData",
                            "Roaming",
                            emu,
                            steamAppId.ToString(CultureInfo.InvariantCulture)
                        )
                    )
                ),
        ];
}
