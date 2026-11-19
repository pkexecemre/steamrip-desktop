using System.Text.Json;
using System.Text.Json.Serialization;
using SteamRip.Core.Install;

namespace SteamRip.Core.Tests;

/// <summary>
/// Writes the recording the Rust port of <see cref="SaveLocator.SelectInGameFolder"/> is checked
/// against, and keeps it honest: the same file is read back and compared with what the C# walk
/// answers today, so a behaviour change here fails here first. Set <c>SR_GOLDEN_DIR</c> to record.
/// </summary>
public class GoldenSaveWalkTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static string FixtureDirectory()
    {
        var fromEnv = Environment.GetEnvironmentVariable("SR_GOLDEN_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return Path.GetFullPath(fromEnv);

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SteamRip.slnx")))
            dir = dir.Parent;
        return Path.Combine(
            dir?.FullName ?? throw new InvalidOperationException("repository root not found"),
            "rust",
            "fixtures"
        );
    }

    private static void RecordOrCheck(string file, object cases)
    {
        var dir = FixtureDirectory();
        var path = Path.Combine(dir, file);
        var payload = JsonSerializer.Serialize(new { cases }, Json) + Environment.NewLine;

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SR_GOLDEN_DIR")))
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, payload);
            return;
        }

        Assert.True(File.Exists(path), $"missing recording {path}: run make golden");
        Assert.Equal(payload, File.ReadAllText(path));
    }

    private static SaveLocator.TreeEntry DirEntry(string path) => new(path, true, false);

    private static SaveLocator.TreeEntry FileEntry(string path) => new(path, false, false);

    private static SaveLocator.TreeEntry LinkEntry(string path) => new(path, true, true);

    [Fact]
    public void Save_walk_snapshot_recordings_match()
    {
        (string Name, int MaxDepth, SaveLocator.TreeEntry[] Entries)[] definitions =
        [
            (
                "a save folder on the depth cap is still selected",
                4,
                [
                    DirEntry("a"),
                    DirEntry("a/b"),
                    DirEntry("a/b/c"),
                    DirEntry("a/b/c/Saves"),
                    DirEntry("a/b/c/d"),
                    DirEntry("a/b/c/d/Profiles"),
                ]
            ),
            (
                "max depth zero selects the root's own saves and stops",
                0,
                [
                    DirEntry("Saves"),
                    DirEntry("Saves/Profiles"),
                    DirEntry("deep"),
                    DirEntry("deep/Saves"),
                ]
            ),
            (
                "max depth one behaves like max depth zero",
                1,
                [
                    DirEntry("Saves"),
                    DirEntry("Saves/Profiles"),
                    DirEntry("deep"),
                    DirEntry("deep/Saves"),
                ]
            ),
            (
                "max depth four reaches the cap but not past it",
                4,
                [
                    DirEntry("a"),
                    DirEntry("a/b"),
                    DirEntry("a/b/c"),
                    DirEntry("a/b/c/Profiles"),
                    DirEntry("a/b/c/d"),
                    DirEntry("a/b/c/d/Saves"),
                ]
            ),
            (
                "a wine prefix is not descended into",
                4,
                [
                    DirEntry("prefix"),
                    DirEntry("prefix/drive_c"),
                    DirEntry("prefix/drive_c/Saves"),
                    DirEntry("prefix/Saves"),
                    DirEntry("other"),
                    DirEntry("other/Saves"),
                ]
            ),
            (
                "system.reg also marks a wine prefix",
                4,
                [
                    DirEntry("gog"),
                    FileEntry("gog/system.reg"),
                    DirEntry("gog/Saves"),
                    DirEntry("plain"),
                    DirEntry("plain/Saves"),
                ]
            ),
            (
                "a file named drive_c is not a prefix",
                4,
                [DirEntry("tools"), FileEntry("tools/drive_c"), DirEntry("tools/Saves")]
            ),
            (
                "a directory named system.reg is not a prefix",
                4,
                [DirEntry("tools2"), DirEntry("tools2/system.reg"), DirEntry("tools2/Saves")]
            ),
            (
                "symlinked directories are skipped",
                4,
                [
                    LinkEntry("Saves"),
                    DirEntry("Saves/Profiles"),
                    LinkEntry("linked"),
                    DirEntry("linked/Saves"),
                    DirEntry("real"),
                    DirEntry("real/Saves"),
                ]
            ),
            (
                "data, resources and plugins are walked at the root",
                4,
                [
                    DirEntry("Data"),
                    DirEntry("Data/Saves"),
                    DirEntry("data"),
                    DirEntry("data/Saves"),
                    DirEntry("Resources"),
                    DirEntry("Resources/Profiles"),
                    DirEntry("Plugins"),
                    DirEntry("Plugins/SaveData"),
                ]
            ),
            (
                "data, resources and plugins are skipped below the root",
                4,
                [
                    DirEntry("game"),
                    DirEntry("game/Data"),
                    DirEntry("game/Data/Saves"),
                    DirEntry("game/data"),
                    DirEntry("game/data/Saves"),
                    DirEntry("game/Resources"),
                    DirEntry("game/Resources/Saves"),
                    DirEntry("game/Plugins"),
                    DirEntry("game/Plugins/SaveData"),
                ]
            ),
            (
                "redist and engine dirs are skipped at any depth",
                4,
                [
                    DirEntry("_CommonRedist"),
                    DirEntry("_CommonRedist/Saves"),
                    DirEntry("_Redist"),
                    DirEntry("_Redist/Saves"),
                    DirEntry("redist"),
                    DirEntry("redist/Saves"),
                    DirEntry("Engine"),
                    DirEntry("Engine/Saves"),
                    DirEntry("Content"),
                    DirEntry("Content/Saves"),
                    DirEntry("Binaries"),
                    DirEntry("Binaries/Saves"),
                    DirEntry("Shaders"),
                    DirEntry("Shaders/Saves"),
                    DirEntry("Win64"),
                    DirEntry("Win64/Saves"),
                    DirEntry("Win32"),
                    DirEntry("Win32/Saves"),
                    DirEntry("x"),
                    DirEntry("x/Engine"),
                    DirEntry("x/Engine/Saves"),
                    DirEntry("keep"),
                    DirEntry("keep/Saves"),
                ]
            ),
            (
                "save-like names are selected, look-alikes are not",
                4,
                [
                    DirEntry("Save"),
                    DirEntry("SAVES"),
                    DirEntry("SaveGames"),
                    DirEntry("MySave"),
                    DirEntry("savepoint"),
                    DirEntry("Saves_old"),
                    DirEntry("SavesDir"),
                    DirEntry("Saver"),
                    DirEntry("Saved"),
                    DirEntry("Profiles"),
                    DirEntry("Profile"),
                    DirEntry("Profiles2"),
                    DirEntry("UserData"),
                    DirEntry("Statistics"),
                ]
            ),
            (
                "a selected save dir is not descended into",
                4,
                [
                    DirEntry("Saves"),
                    DirEntry("Saves/Profiles"),
                    DirEntry("Saves/Saves"),
                    DirEntry("other"),
                    DirEntry("other/Saves"),
                ]
            ),
            (
                "no save dirs returns nothing",
                4,
                [DirEntry("bin"), FileEntry("bin/game.bin"), DirEntry("bin/x")]
            ),
            (
                "prefix detection happens below the root",
                4,
                [
                    DirEntry("a"),
                    DirEntry("a/prefix"),
                    DirEntry("a/prefix/drive_c"),
                    DirEntry("a/prefix/drive_c/Saves"),
                    DirEntry("a/prefix/Saves"),
                    DirEntry("a/ok"),
                    DirEntry("a/ok/Saves"),
                ]
            ),
            ("empty snapshot returns nothing", 4, []),
        ];

        var cases = definitions
            .Select(definition => new
            {
                name = definition.Name,
                max_depth = definition.MaxDepth,
                entries = definition
                    .Entries.Select(entry => new
                    {
                        path = entry.RelativePath,
                        directory = entry.IsDirectory,
                        link = entry.IsSymlink,
                    })
                    .ToList(),
                found = SaveLocator
                    .SelectInGameFolder(definition.Entries, definition.MaxDepth)
                    .ToList(),
            })
            .ToList();

        RecordOrCheck("save_walk_tree.json", cases);
    }

    [Fact]
    public void Walk_and_select_agree_on_a_real_folder()
    {
        var dir = Directory.CreateTempSubdirectory("sr-savewalk-");
        try
        {
            var root = dir.FullName;
            Directory.CreateDirectory(Path.Combine(root, "SaveGames"));
            Directory.CreateDirectory(Path.Combine(root, "Profile", "UserData"));
            Directory.CreateDirectory(Path.Combine(root, "Engine", "Saved"));
            Directory.CreateDirectory(Path.Combine(root, "prefix", "drive_c", "Saves"));
            Directory.CreateDirectory(Path.Combine(root, "prefix", "Saves"));
            Directory.CreateDirectory(Path.Combine(root, "Data", "Saves"));
            Directory.CreateDirectory(Path.Combine(root, "deep", "Data", "Saves"));
            File.WriteAllText(Path.Combine(root, "prefix", "system.reg"), "prefix");

            var expected = SaveLocator.FindInGameFolder(root);
            var actual = SaveLocator.SelectInGameFolder(Snapshot(root));

            Assert.Equal(expected, actual);
            string[] wanted = ["Data/Saves", "Profile/UserData", "SaveGames"];
            Assert.Equal(
                wanted,
                expected
                    .Select(relative => relative.Replace('\\', '/'))
                    .Order(StringComparer.Ordinal)
                    .ToList()
            );
        }
        finally
        {
            dir.Delete(true);
        }
    }

    /// <summary>An independent snapshot of a real tree, so the production wrapper has a witness.</summary>
    private static List<SaveLocator.TreeEntry> Snapshot(string root)
    {
        var entries = new List<SaveLocator.TreeEntry>();
        Walk(root, "", 0);
        return entries;

        void Walk(string dir, string relative, int depth)
        {
            if (depth >= 4)
                return;
            if (File.Exists(Path.Combine(dir, "system.reg")))
                entries.Add(
                    new SaveLocator.TreeEntry(Path.Combine(relative, "system.reg"), false, false)
                );
            foreach (var child in Directory.EnumerateDirectories(dir))
            {
                var childRelative = Path.Combine(relative, Path.GetFileName(child));
                entries.Add(
                    new SaveLocator.TreeEntry(
                        childRelative,
                        true,
                        new DirectoryInfo(child).LinkTarget is not null
                    )
                );
                Walk(child, childRelative, depth + 1);
            }
        }
    }
}
