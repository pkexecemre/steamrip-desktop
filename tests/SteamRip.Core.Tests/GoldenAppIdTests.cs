using System.Text.Json;
using System.Text.Json.Serialization;
using SteamRip.Core.Catalog;

namespace SteamRip.Core.Tests;

/// <summary>
/// Writes the recording the Rust port of <see cref="SteamAppIdResolver.Tokens"/>,
/// <see cref="SteamAppIdResolver.NamesMatch"/> and <see cref="SteamAppIdResolver.PickSearchMatch"/>
/// is checked against, and keeps it honest: the same file is read back and compared with what the
/// C# implementation answers today, so a behaviour change here fails here first. Set
/// <c>SR_GOLDEN_DIR</c> (<c>make golden</c>) to record.
/// </summary>
public class GoldenAppIdTests
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

    [Fact]
    public void App_id_recordings_match()
    {
        var cases = new List<object>();

        // Token rules: stopwords, short tokens, punctuation, case, non-ASCII letters.
        string[] tokenInputs =
        [
            "The Last of Us Part II",
            "Cyberpunk 2077: Phantom Liberty",
            "Lara’s Quest",
            "Lara's Quest",
            "Tom & Jerry",
            "Türkçe Oyun Adı",
        ];
        foreach (var input in tokenInputs)
        {
            cases.Add(
                new
                {
                    kind = "tokens",
                    input,
                    tokens = SteamAppIdResolver
                        .Tokens(input)
                        .OrderBy(token => token, StringComparer.Ordinal)
                        .ToList(),
                }
            );
        }

        // Claimed-id validation: true means the site's app id is trusted, false sends it to search.
        (string Game, string Steam)[] names =
        [
            ("The Last of Us Part II", "The Last of Us Part II Remastered"),
            ("The Witcher 3: Wild Hunt", "The Witcher 3: Wild Hunt - Complete Edition"),
            ("Cyberpunk 2077", "Cyberpunk 2077: Phantom Liberty"),
            ("Cyberpunk 2077", "The Witcher 3: Wild Hunt"),
            ("Fallout 4 GOTY", "Fallout 4"),
            ("Hades", "Hades Deluxe Edition"),
            ("Alpha Beta Gamma", "Alpha Beta Delta"),
            ("Alpha Beta Gamma", "Alpha Delta Epsilon"),
            ("Alpha Beta Gamma Delta", "Alpha Beta Zeta Epsilon"),
            ("The Game", "Prey"),
            ("", "Prey"),
        ];
        foreach (var (game, steam) in names)
        {
            cases.Add(
                new
                {
                    kind = "names_match",
                    game,
                    steam,
                    match = SteamAppIdResolver.NamesMatch(game, steam),
                }
            );
        }

        // Store search: the base game, a DLC and a loose sibling, ties and the score order.
        (string Game, (long Id, string Name)[] Hits)[] picks =
        [
            (
                "Elden Ring",
                [
                    (101, "Elden Ring: Shadow of the Erdtree"),
                    (102, "Elden Ring"),
                    (103, "Elden Ring Nightreign"),
                ]
            ),
            ("Doom", [(201, "Doom 2"), (202, "Doom")]),
            ("Doom", [(202, "Doom"), (201, "Doom 2")]),
            ("Control", [(301, "Control Deluxe Edition")]),
            ("Hollow Knight", [(401, "Hollow Knight: Silksong")]),
            ("Alpha Beta Gamma Delta", [(501, "Alpha Beta")]),
            ("The GOTY Edition", [(601, "Prey")]),
            (
                "Alpha Beta Gamma",
                [
                    (701, "Alpha Beta Gamma Delta"),
                    (702, "Alpha Beta Gamma"),
                    (703, "Alpha Beta Gamma Epsilon Zeta"),
                ]
            ),
        ];
        foreach (var (game, hits) in picks)
        {
            cases.Add(
                new
                {
                    kind = "pick",
                    game,
                    hits = hits.Select(hit => new { id = hit.Id, name = hit.Name }).ToList(),
                    picked = SteamAppIdResolver.PickSearchMatch(game, hits),
                }
            );
        }

        RecordOrCheck("app_ids.json", cases);
    }
}
