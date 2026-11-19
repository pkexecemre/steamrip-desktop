using System.Text.Json;
using System.Text.Json.Serialization;
using SteamRip.Core.Catalog;

namespace SteamRip.Core.Tests;

/// <summary>
/// Writes the recordings the Rust port is checked against, and keeps them honest: the same
/// file is read back and compared with what the C# implementation answers today, so a
/// behaviour change here fails here first. Set <c>SR_GOLDEN_DIR</c> (<c>make golden</c>) to record.
/// </summary>
public class GoldenTests
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

    private static void RecordOrCheck<T>(string file, IReadOnlyList<T> cases)
    {
        var dir = FixtureDirectory();
        var path = Path.Combine(dir, file);
        var payload = JsonSerializer.Serialize(new Fixture<T> { Cases = cases }, Json);

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SR_GOLDEN_DIR")))
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, payload + Environment.NewLine);
            return;
        }

        Assert.True(File.Exists(path), $"missing recording {path}: run make golden");
        var recorded = JsonSerializer.Deserialize<Fixture<T>>(File.ReadAllText(path));
        Assert.NotNull(recorded);
        Assert.Equal(payload, JsonSerializer.Serialize(recorded, Json));
    }

    [Fact]
    public void Title_parser_recordings_match()
    {
        string[] titles =
        [
            "Mortal Shell II Free Download (v98314+1943)",
            "Hull Rupture Free Download",
            "Victoria 3 Free Download (v1.15.0)",
            "Some Game Free Download (v1.2 + ALL DLCs)",
            "R&#8217;s Quest Free Download (v2)",
            "Dual Pack [v1.0 & v1.1]",
            "  Spaced Out Free Download ( v9 )  ",
            "Tom &amp; Jerry Free Download",
            "No Version Download Free Download",
            "Türkçe Oyun Adı Free Download (v3.1 + DLC)",
            "Game Free Download (nested (parens))",
            "",
        ];

        var cases = titles
            .Select(title =>
            {
                var (name, version) = TitleParser.Parse(title);
                return new
                {
                    input = title,
                    name,
                    version,
                };
            })
            .ToList();

        RecordOrCheck("title_parser.json", cases);
    }

    [Fact]
    public void Size_recordings_match()
    {
        string?[] sizes =
        [
            "12.3 GB",
            "750 MB",
            "1,5 TB",
            "900 KB",
            "0.5 KB",
            "2048 GB",
            "  7 GB  ",
            "12.34 GB",
            "unknown",
            "12 GB extra",
            null,
        ];

        var cases = sizes
            .Select(text => new { input = text, bytes = CatalogParser.ParseSize(text) })
            .ToList();

        RecordOrCheck("size.json", cases);
    }

    [Fact]
    public void Title_prefix_recordings_match()
    {
        string[] names =
        [
            "LEGO Batman: Legacy of the Dark Knight",
            "Cool Game - Ultimate Edition",
            "NoSubtitle",
            "Ab: cd",
            "Series: The Third One - Deluxe",
            "  Padded: Name  ",
            "Türkçe: Oyun",
        ];

        var cases = names
            .Select(name => new { input = name, prefix = SteamAppIdResolver.TitlePrefix(name) })
            .ToList();

        RecordOrCheck("title_prefix.json", cases);
    }

    private sealed class Fixture<T>
    {
        [JsonPropertyName("cases")]
        public required IReadOnlyList<T> Cases { get; init; }
    }
}
