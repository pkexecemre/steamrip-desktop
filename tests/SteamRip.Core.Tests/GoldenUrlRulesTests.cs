using SteamRip.Core.Solver;

namespace SteamRip.Core.Tests;

/// <summary>
/// Recordings for the two URL decisions a hoster gate makes: does a URL point at an archive, and is it
/// the file rather than the site's own page or its abuse form. Checked in C#, replayed in Rust.
/// </summary>
public class GoldenUrlRulesTests
{
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

    private static readonly System.Text.Json.JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
    };

    private static void RecordOrCheck(string file, object cases)
    {
        var dir = FixtureDirectory();
        var path = Path.Combine(dir, file);
        var payload =
            System.Text.Json.JsonSerializer.Serialize(new { cases }, Json) + Environment.NewLine;

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
    public void Url_rule_recordings_match()
    {
        (string Href, string? Page)[] cases =
        [
            ("https://x/game.rar", null),
            ("https://x/game.rar?token=abc", null),
            ("https://x/game.RAR", null),
            ("https://x/game.rar/", null),
            ("https://x/game.r00", null),
            ("https://x/game.7z.001", null),
            ("https://x/game.zip.001", null),
            ("https://x/signed/MTc5NDAz", null),
            ("https://abuse.foo/game.rar", null),
            ("https://x/abuse/1.rar", null),
            ("https://x/ABUSE/1.rar", null),
            ("https://x/game.rar", "https://x/game.rar"),
            ("https://x/other.rar", "https://x/game.rar"),
            ("files/x.zip", null),
            ("not a url", null),
            ("", null),
        ];

        var recorded = cases
            .Select(c => new
            {
                href = c.Href,
                page = c.Page,
                looks_like_archive = InteractivePage.LooksLikeArchive(c.Href),
                is_download = InteractivePage.IsDownloadLink(c.Href, c.Page),
            })
            .ToList();

        RecordOrCheck("url_rules.json", recorded);
    }
}
