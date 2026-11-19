using System.Text.Json;
using System.Text.Json.Serialization;
using SteamRip.Core.Updates;

namespace SteamRip.Core.Tests;

/// <summary>
/// Writes the recording the Rust port of <c>UpdateService.PickArchive</c> and of the
/// download path's destination naming is checked against, and keeps it honest: the same file is
/// read back and compared byte for byte with what the C# implementation answers today, so a
/// behaviour change here fails here first. Set <c>SR_GOLDEN_DIR</c> (<c>make golden</c>) to record.
/// </summary>
public class GoldenPickArchiveTests
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

    private static void RecordOrCheck(string file, object payload)
    {
        var dir = FixtureDirectory();
        var path = Path.Combine(dir, file);
        var text = JsonSerializer.Serialize(payload, Json) + Environment.NewLine;

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SR_GOLDEN_DIR")))
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, text);
            return;
        }

        Assert.True(File.Exists(path), $"missing recording {path}: run make golden");
        Assert.Equal(text, File.ReadAllText(path));
    }

    [Fact]
    public void Pick_archive_and_destination_names_match()
    {
        (
            string Name,
            string[] Files,
            (string Path, bool Exists, long Length, bool Archive)[] Probes
        )[] picks =
        [
            (
                "two rar files: the bigger wins",
                ["dl/Game.rar", "dl/Game.Extra.rar"],
                [("dl/Game.rar", true, 500L, true), ("dl/Game.Extra.rar", true, 900L, true)]
            ),
            (
                "split volumes: the first part wins even when it is smaller",
                ["dl/Game.part02.rar", "dl/Game.part03.rar"],
                [
                    ("dl/Game.part02.rar", true, 900L, true),
                    ("dl/Game.part03.rar", true, 800L, true),
                    ("dl/Game.part01.rar", true, 100L, true),
                ]
            ),
            (
                "split set whose first volume is missing keeps the volume it has",
                ["dl/Game.part02.rar"],
                [("dl/Game.part02.rar", true, 900L, true), ("dl/Game.part01.rar", false, 0L, true)]
            ),
            (
                "html error page in the folder is ignored even when it is bigger",
                ["dl/Game.rar", "dl/index.html"],
                [("dl/Game.rar", true, 300L, true), ("dl/index.html", true, 9_000L, false)]
            ),
            (
                ".part download file is not an archive",
                ["dl/Game.rar.part"],
                [("dl/Game.rar.part", true, 4_096L, false)]
            ),
            (
                "missing files are skipped",
                ["dl/gone.rar", "dl/Game.rar"],
                [("dl/gone.rar", false, 900L, true), ("dl/Game.rar", true, 100L, true)]
            ),
            (
                "only missing files: nothing to pick",
                ["dl/gone.rar", "dl/also-gone.7z"],
                [("dl/gone.rar", false, 900L, true), ("dl/also-gone.7z", false, 800L, true)]
            ),
            ("empty payload has no archive", [], []),
            (
                "torrent metadata is not the archive",
                ["dl/Game.torrent", "dl/Game.rar"],
                [("dl/Game.torrent", true, 80L, false), ("dl/Game.rar", true, 700L, true)]
            ),
            (
                "equal sizes keep input order",
                ["dl/b.rar", "dl/a.rar"],
                [("dl/b.rar", true, 100L, true), ("dl/a.rar", true, 100L, true)]
            ),
            (
                "7z and zip: the biggest archive wins",
                ["dl/a.zip", "dl/b.7z", "dl/c.rar"],
                [
                    ("dl/a.zip", true, 100L, true),
                    ("dl/b.7z", true, 900L, true),
                    ("dl/c.rar", true, 400L, true),
                ]
            ),
        ];

        var pickCases = picks
            .Select(definition =>
            {
                var probes = definition.Probes.ToDictionary(probe => probe.Path);
                var picked = UpdateService.PickArchive(
                    definition.Files,
                    path => probes.TryGetValue(path, out var probe) && probe.Exists,
                    path => probes.TryGetValue(path, out var probe) ? probe.Length : 0,
                    path => probes.TryGetValue(path, out var probe) && probe.Archive
                );
                return new
                {
                    name = definition.Name,
                    files = definition.Files,
                    probes = definition
                        .Probes.Select(probe => new
                        {
                            path = probe.Path,
                            exists = probe.Exists,
                            length = probe.Length,
                            archive = probe.Archive,
                        })
                        .ToList(),
                    picked,
                };
            })
            .ToList();

        (string Name, string? FileName, string Slug)[] names =
        [
            ("null file name falls back to the slug", null, "some-game"),
            ("dotted name counts as having an extension", "Game 1.2", "game"),
            ("name with a slash", "My Games/Final Cut", "game"),
            ("plain name gets the archive extension", "Game", "game"),
            ("name that already has an extension keeps it", "Game.rar", "game"),
            ("torrent extension is matched case-insensitively", "Game.TORRENT", "game"),
            ("trailing dot has no extension", "Game.", "game"),
        ];

        var nameCases = names
            .Select(definition => new
            {
                name = definition.Name,
                file_name = definition.FileName,
                slug = definition.Slug,
                archive = UpdateService.ArchiveFileName(definition.FileName, definition.Slug),
                torrent = UpdateService.TorrentFileName(definition.FileName, definition.Slug),
            })
            .ToList();

        RecordOrCheck("pick_archive.json", new { pick_archive = pickCases, names = nameCases });
    }
}
