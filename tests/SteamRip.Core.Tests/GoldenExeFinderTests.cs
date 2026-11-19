using System.Text.Json;
using System.Text.Json.Serialization;
using SteamRip.Core.Launch;

namespace SteamRip.Core.Tests;

/// <summary>
/// Writes the recording the Rust port of <see cref="ExeFinder"/> is checked against, and keeps it
/// honest: the same file is read back and compared with what the C# implementation answers today,
/// so a behaviour change here fails here first. Set <c>SR_GOLDEN_DIR</c> (<c>make golden</c>) to record.
/// </summary>
public class GoldenExeFinderTests
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

    private static ExeFinder.Entry FileEntry(string path, long size, int depth) =>
        new(path, false, false, size, depth);

    private static ExeFinder.Entry DirEntry(string path, int depth) =>
        new(path, true, false, 0, depth);

    private static ExeFinder.Entry LinkEntry(string path, int depth) =>
        new(path, true, true, 0, depth);

    [Fact]
    public void Exe_finder_recordings_match()
    {
        (string Name, string? Game, ExeFinder.Entry[] Entries)[] definitions =
        [
            (
                "ue shipping binary wins",
                "Saturn Return",
                [
                    DirEntry("Saturn Return", 1),
                    DirEntry("Saturn Return/Binaries", 2),
                    DirEntry("Saturn Return/Binaries/Win64", 3),
                    FileEntry(
                        "Saturn Return/Binaries/Win64/SaturnReturn-Win64-Shipping.exe",
                        60_000_000,
                        3
                    ),
                    FileEntry("Saturn Return/SaturnReturn.exe", 8_000_000, 1),
                    FileEntry("Saturn Return/Launcher.exe", 5_000_000, 1),
                    FileEntry("Saturn Return/unins000.exe", 1_000_000, 1),
                    FileEntry("Saturn Return/crashhandler.exe", 900_000, 1),
                ]
            ),
            (
                "top-level exe beats a nested tiny helper",
                null,
                [
                    FileEntry("Tiny Top.exe", 100_000, 0),
                    DirEntry("bin", 1),
                    FileEntry("bin/normal.exe", 1_000_000, 1),
                    FileEntry("bin/helper.exe", 400_000, 1),
                ]
            ),
            (
                "launcher penalty",
                "Some Game",
                [
                    DirEntry("Some Game", 1),
                    FileEntry("Some Game/Launcher.exe", 5_000_000, 1),
                    FileEntry("Some Game/Some Game.exe", 5_000_000, 1),
                ]
            ),
            (
                "tiny helper is pushed down",
                "Big Adventure",
                [
                    DirEntry("Big Adventure", 1),
                    FileEntry("Big Adventure/Adventure.exe", 10_000_000, 1),
                    FileEntry("Big Adventure/tiny.exe", 100_000, 1),
                ]
            ),
            (
                "huge blob gets a nudge",
                null,
                [
                    DirEntry("A", 1),
                    FileEntry("A/blob.exe", 60_000_000, 1),
                    FileEntry("A/mid.exe", 10_000_000, 1),
                ]
            ),
            (
                "redist and tool folders are ignored",
                "Neon Drift",
                [
                    DirEntry("Neon Drift", 1),
                    DirEntry("Neon Drift/_commonredist", 2),
                    DirEntry("Neon Drift/_commonredist/vcredist", 3),
                    FileEntry("Neon Drift/_commonredist/vcredist/vcredist_x64.exe", 20_000_000, 3),
                    DirEntry("Neon Drift/_redist", 2),
                    FileEntry("Neon Drift/_redist/dxsetup.exe", 1_000_000, 2),
                    DirEntry("Neon Drift/prefix", 2),
                    DirEntry("Neon Drift/prefix/drive_c", 3),
                    FileEntry("Neon Drift/prefix/drive_c/Neon.exe", 30_000_000, 3),
                    DirEntry("Neon Drift/engine", 2),
                    FileEntry("Neon Drift/engine/engine.exe", 2_000_000, 2),
                    FileEntry("Neon Drift/NeonDrift.exe", 20_000_000, 1),
                ]
            ),
            (
                "a folder that holds drive_c is pruned whole",
                "Some Game",
                [
                    DirEntry("Some Game", 1),
                    DirEntry("Some Game/drive_c", 2),
                    FileEntry("Some Game/drive_c/real.exe", 30_000_000, 2),
                    DirEntry("Some Game/bin", 2),
                    FileEntry("Some Game/bin/real.exe", 30_000_000, 2),
                    FileEntry("Other.exe", 1_000_000, 0),
                ]
            ),
            (
                "drive_c directly under the root is not pruned",
                null,
                [DirEntry("drive_c", 1), FileEntry("drive_c/Tool.exe", 1_000_000, 1)]
            ),
            (
                "symlinked directory is skipped",
                null,
                [
                    LinkEntry("linked", 1),
                    FileEntry("linked/Game.exe", 5_000_000, 1),
                    FileEntry("Real.exe", 1_000_000, 0),
                ]
            ),
            (
                "installer and helper names are filtered",
                null,
                [
                    FileEntry("unins000.exe", 1_000_000, 0),
                    FileEntry("setup.exe", 1_000_000, 0),
                    FileEntry("UnityCrashHandler64.exe", 1_000_000, 0),
                    FileEntry("EasyAntiCheat.exe", 1_000_000, 0),
                    FileEntry("BattlEye.exe", 1_000_000, 0),
                    FileEntry("steam_api64.exe", 1_000_000, 0),
                    FileEntry("dxsetup.exe", 1_000_000, 0),
                    FileEntry("game_update.exe", 1_000_000, 0),
                    FileEntry("GameTrainer.exe", 1_000_000, 0),
                    FileEntry("vcredist_x64.exe", 1_000_000, 0),
                    FileEntry("Real Game.exe", 5_000_000, 0),
                ]
            ),
            (
                "squashed name match",
                "5D Chess With Multiverse Time Travel",
                [
                    FileEntry("5dchesswithmultiversetimetravel.exe", 5_000_000, 0),
                    FileEntry("Chess.exe", 1_000_000, 0),
                ]
            ),
            (
                "tokenizer match only",
                "Lara’s Quest",
                [FileEntry("Lara's Journey.exe", 500_000, 0), FileEntry("Journey.exe", 500_000, 1)]
            ),
            (
                "stop words do not match",
                "The Last of Us",
                [
                    FileEntry("The Last of Us Part II.exe", 2_000_000, 0),
                    FileEntry("Us.exe", 1_000_000, 0),
                    FileEntry("The.exe", 1_000_000, 0),
                ]
            ),
            (
                "one-letter tokens are dropped",
                "D Day",
                [FileEntry("D-Day.exe", 1_000_000, 0), FileEntry("Daybreak.exe", 1_000_000, 1)]
            ),
            (
                "same score: bigger file first, then input order",
                null,
                [
                    DirEntry("z", 1),
                    FileEntry("z/one.exe", 1_000_000, 1),
                    FileEntry("z/two.exe", 2_000_000, 1),
                    FileEntry("z/three.exe", 1_000_000, 1),
                ]
            ),
            (
                "non executables are ignored",
                "Some Game",
                [
                    DirEntry("Some Game", 1),
                    FileEntry("Some Game/readme.txt", 1_000, 1),
                    FileEntry("Some Game/game.EXE", 5_000_000, 1),
                    DirEntry("Some Game/data", 2),
                    FileEntry("Some Game/data/notes.exe.bak", 1_000, 2),
                ]
            ),
            (
                "beyond the depth cap",
                null,
                [
                    DirEntry("a", 1),
                    DirEntry("a/b", 2),
                    DirEntry("a/b/c", 3),
                    DirEntry("a/b/c/d", 4),
                    DirEntry("a/b/c/d/e", 5),
                    FileEntry("a/b/c/d/keep.exe", 1_000_000, 4),
                    FileEntry("a/b/c/d/e/drop.exe", 1_000_000, 5),
                ]
            ),
            ("empty tree", null, []),
        ];

        var cases = definitions
            .Select(definition => new
            {
                name = definition.Name,
                game = definition.Game,
                entries = definition
                    .Entries.Select(entry => new
                    {
                        path = entry.RelativePath,
                        directory = entry.IsDirectory,
                        link = entry.IsLink,
                        size = entry.Size,
                        depth = entry.Depth,
                    })
                    .ToList(),
                ranked = ExeFinder
                    .Rank(definition.Entries, definition.Game)
                    .Select(candidate => new
                    {
                        path = candidate.RelativePath,
                        size = candidate.Size,
                        depth = candidate.Depth,
                        score = candidate.Score,
                    })
                    .ToList(),
            })
            .ToList();

        RecordOrCheck("exe_finder.json", cases);
    }

    [Fact]
    public void Walk_and_rank_agree_on_a_real_folder()
    {
        var dir = Directory.CreateTempSubdirectory("sr-exefinder-");
        try
        {
            var root = dir.FullName;
            var game = Directory.CreateDirectory(Path.Combine(root, "My Game")).FullName;
            var binaries = Directory
                .CreateDirectory(Path.Combine(game, "Binaries", "Win64"))
                .FullName;
            File.WriteAllBytes(Path.Combine(binaries, "MyGame-Win64-Shipping.exe"), new byte[600]);
            File.WriteAllBytes(Path.Combine(game, "Launcher.exe"), new byte[500]);
            File.WriteAllBytes(Path.Combine(game, "unins000.exe"), new byte[400]);
            var redist = Directory.CreateDirectory(Path.Combine(game, "_commonredist")).FullName;
            File.WriteAllBytes(Path.Combine(redist, "vcredist_x64.exe"), new byte[300]);
            var prefix = Directory
                .CreateDirectory(Path.Combine(game, "prefix", "drive_c"))
                .FullName;
            File.WriteAllBytes(Path.Combine(prefix, "game.exe"), new byte[200]);
            File.WriteAllBytes(Path.Combine(root, "Top.exe"), new byte[700]);
            File.WriteAllText(Path.Combine(root, "notes.txt"), "not an executable");

            var expected = ExeFinder.Candidates(root, "My Game");
            var actual = ExeFinder.Rank(Snapshot(root), "My Game");

            Assert.Equal(expected, actual);
            Assert.Equal(
                "My Game/Binaries/Win64/MyGame-Win64-Shipping.exe",
                expected[0].RelativePath
            );
        }
        finally
        {
            dir.Delete(true);
        }
    }

    private static List<ExeFinder.Entry> Snapshot(string root)
    {
        var entries = new List<ExeFinder.Entry>();
        Walk(new DirectoryInfo(root), 0);
        return entries;

        void Walk(DirectoryInfo directory, int depth)
        {
            if (depth > 4)
                return;
            foreach (var child in directory.EnumerateFileSystemInfos())
            {
                if (child is DirectoryInfo subdirectory)
                {
                    entries.Add(
                        new ExeFinder.Entry(
                            Path.GetRelativePath(root, subdirectory.FullName),
                            true,
                            subdirectory.LinkTarget is not null,
                            0,
                            depth + 1
                        )
                    );
                    Walk(subdirectory, depth + 1);
                }
                else if (child is FileInfo file)
                {
                    entries.Add(
                        new ExeFinder.Entry(
                            Path.GetRelativePath(root, file.FullName),
                            false,
                            false,
                            file.Length,
                            depth
                        )
                    );
                }
            }
        }
    }
}
