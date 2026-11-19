namespace SteamRip.Core.Launch;

/// <summary>Picks the game's main executable out of an extracted SteamRIP folder. Heuristic, overridable per game.</summary>
public static class ExeFinder
{
    private const int MaxDepth = 4;

    private static readonly string[] NoiseDirs =
    [
        "_commonredist",
        "_redist",
        "redist",
        "redistributables",
        "prefix",
        "__macosx",
        "_setup",
        "directx",
        "dotnet",
        "vcredist",
        "engine",
        "support",
        "tools",
    ];
    private static readonly string[] NoiseNames =
    [
        "unins",
        "uninstall",
        "setup",
        "install",
        "redist",
        "vcredist",
        "vc_redist",
        "dxsetup",
        "dxwebsetup",
        "dotnet",
        "oalinst",
        "physx",
        "crashhandler",
        "crashreport",
        "crashreportclient",
        "unitycrashhandler",
        "ue4prereqsetup",
        "uecrash",
        "launcherpatch",
        "cleanup",
        "report",
        "eac",
        "easyanticheat",
        "battleye",
        "beservice",
        "steamservice",
        "steam_api",
        "steamclient",
        "python",
        "wine",
        "7z",
        "unrar",
        "rar",
        "touchup",
        "activat",
        "update",
        "patch",
        "cheat",
        "trainer",
        "config",
        "settings",
    ];
    private static readonly char[] TokenSeparators =
    [
        ' ',
        '-',
        '_',
        '.',
        ':',
        '\'',
        '’',
        '(',
        ')',
        '[',
        ']',
    ];

    public sealed record Candidate(string RelativePath, long Size, int Depth, int Score);

    /// <summary>One entry the walk saw, relative to the game root: a directory or a file.</summary>
    public sealed record Entry(
        string RelativePath,
        bool IsDirectory,
        bool IsLink,
        long Size,
        int Depth
    );

    /// <summary>All plausible executables, best first.</summary>
    public static IReadOnlyList<Candidate> Candidates(string gameRoot, string? gameName = null)
    {
        if (!Directory.Exists(gameRoot))
            return [];
        var entries = new List<Entry>();
        Walk(new DirectoryInfo(gameRoot), 0);
        return Rank(entries, gameName);

        void Walk(DirectoryInfo dir, int depth)
        {
            if (depth > MaxDepth)
                return;
            IEnumerable<FileSystemInfo> children;
            try
            {
                children = dir.EnumerateFileSystemInfos();
            }
            catch
            {
                return;
            }
            foreach (var child in children)
            {
                switch (child)
                {
                    case DirectoryInfo d:
                        var isLink = d.LinkTarget is not null;
                        var holdsDriveC = Directory.Exists(Path.Combine(d.FullName, "drive_c"));
                        entries.Add(
                            new Entry(
                                Path.GetRelativePath(gameRoot, d.FullName),
                                true,
                                isLink,
                                0,
                                depth + 1
                            )
                        );
                        if (!SkipDirectory(d.Name, isLink, holdsDriveC))
                            Walk(d, depth + 1);
                        break;
                    case FileInfo f when IsExe(f.Name):
                        entries.Add(
                            new Entry(
                                Path.GetRelativePath(gameRoot, f.FullName),
                                false,
                                false,
                                f.Length,
                                depth
                            )
                        );
                        break;
                }
            }
        }
    }

    public static string? Best(string gameRoot, string? gameName = null)
    {
        var all = Candidates(gameRoot, gameName);
        return all.Count > 0 ? all[0].RelativePath : null;
    }

    /// <summary>
    /// Pure: which of the enumerated entries are plausible executables, best first. Every
    /// directory rule (symlinks, redist/tool folders, wine prefixes, the depth cap) is decided
    /// here, so the filesystem walk only has to enumerate.
    /// </summary>
    public static IReadOnlyList<Candidate> Rank(
        IReadOnlyList<Entry> entries,
        string? gameName = null
    )
    {
        var nameTokens = Tokens(gameName ?? "");
        var nameSquashed = Squash(gameName ?? "");
        var pruned = PrunedDirectories(entries);
        var list = new List<Candidate>();
        foreach (var entry in entries)
        {
            if (entry.IsDirectory || entry.Depth > MaxDepth)
                continue;
            var segments = Segments(entry.RelativePath);
            if (segments.Length == 0 || IsUnderPruned(segments, pruned))
                continue;
            var fileName = segments[^1];
            if (!IsExe(fileName))
                continue;
            var lower = Stem(fileName).ToLowerInvariant();
            if (NoiseNames.Any(lower.Contains))
                continue;
            var score = ScoreFile(
                lower,
                entry.RelativePath,
                entry.Size,
                entry.Depth,
                nameTokens,
                nameSquashed
            );
            list.Add(new Candidate(entry.RelativePath, entry.Size, entry.Depth, score));
        }
        return
        [
            .. list.OrderByDescending(c => c.Score)
                .ThenBy(c => c.Depth)
                .ThenByDescending(c => c.Size),
        ];
    }

    private static bool SkipDirectory(string name, bool isLink, bool holdsDriveC) =>
        isLink || holdsDriveC || NoiseDirs.Contains(name.ToLowerInvariant());

    private static int ScoreFile(
        string lower,
        string relativePath,
        long size,
        int depth,
        HashSet<string> nameTokens,
        string nameSquashed
    )
    {
        var score = 0;
        var fileTokens = Tokens(lower);
        score += 10 * nameTokens.Count(fileTokens.Contains); // name match is the strongest signal
        var squashed = Squash(lower);
        if (
            nameSquashed.Length > 3
            && (
                squashed.Contains(nameSquashed)
                || (nameSquashed.Contains(squashed) && lower.Length > 3)
            )
        )
            score += 10; // "5dchesswithmultiversetimetravel"
        if (lower.Contains("shipping"))
            score += 8; // UE packaged binary
        if (lower.Contains("win64") || lower.Contains("x64"))
            score += 2;
        if (relativePath.Contains("Binaries", StringComparison.OrdinalIgnoreCase))
            score += 3; // UE layout
        if (lower.Contains("launcher"))
            score -= 3; // usually a stub, but sometimes the only way in
        if (depth == 0)
            score += 2; // top-level exe in the collapsed game dir
        if (size > 50L << 20)
            score += 3;
        else if (size < 300L << 10)
            score -= 2; // real games aren't tiny
        return score;
    }

    /// <summary>Directories the walk must not descend into, as root-relative keys joined with '/'.</summary>
    private static HashSet<string> PrunedDirectories(IReadOnlyList<Entry> entries)
    {
        var holdsDriveC = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!entry.IsDirectory)
                continue;
            var segments = Segments(entry.RelativePath);
            if (segments.Length > 0 && segments[^1] == "drive_c")
                holdsDriveC.Add(ParentKey(segments));
        }

        var pruned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!entry.IsDirectory)
                continue;
            var segments = Segments(entry.RelativePath);
            if (segments.Length == 0)
                continue;
            var key = string.Join('/', segments);
            if (
                entry.IsLink
                || holdsDriveC.Contains(key)
                || NoiseDirs.Contains(segments[^1].ToLowerInvariant())
            )
                pruned.Add(key);
        }
        return pruned;
    }

    private static bool IsUnderPruned(string[] segments, HashSet<string> pruned)
    {
        var key = "";
        for (var i = 0; i < segments.Length - 1; i++)
        {
            key = i == 0 ? segments[0] : key + "/" + segments[i];
            if (pruned.Contains(key))
                return true;
        }
        return false;
    }

    private static string[] Segments(string path) =>
        path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

    private static string ParentKey(string[] segments) => string.Join('/', segments[..^1]);

    private static bool IsExe(string name) =>
        Extension(name).Equals(".exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>The .NET rule: the last dot after the last separator, but not a trailing one.</summary>
    private static int DotIndex(string name)
    {
        for (var i = name.Length - 1; i >= 0; i--)
        {
            if (name[i] == '.')
                return i == name.Length - 1 ? -1 : i;
            if (name[i] is '/' or '\\')
                break;
        }
        return -1;
    }

    private static string Extension(string name)
    {
        var dot = DotIndex(name);
        return dot < 0 ? "" : name[dot..];
    }

    private static string Stem(string name)
    {
        var dot = DotIndex(name);
        return dot < 0 ? name : name[..dot];
    }

    private static string Squash(string s) =>
        new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static HashSet<string> Tokens(string s) =>
        [
            .. s.ToLowerInvariant()
                .Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries)
                .Where(t =>
                    t.Length > 1
                    && t
                        is not (
                            "the"
                            or "of"
                            or "and"
                            or "a"
                            or "an"
                            or "win64"
                            or "win32"
                            or "exe"
                        )
                ),
        ];
}
