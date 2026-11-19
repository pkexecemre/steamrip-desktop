using System.Text.Json;
using System.Text.Json.Serialization;
using SteamRip.Core.Catalog;

namespace SteamRip.Core.Tests;

/// <summary>
/// Writes the recording the Rust port of <see cref="CatalogParser.ParseOne"/> is checked against,
/// and keeps it honest: the same file is read back and compared byte for byte with what the C#
/// implementation answers today, so a behaviour change here fails here first. Set
/// <c>SR_GOLDEN_DIR</c> (<c>make golden</c>) to record.
/// </summary>
public class GoldenParseOneTests
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

    private static JsonElement Post(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>Unix seconds for the stored UTC value; DateTime.MinValue (ticks 0) is -62135596800.</summary>
    private static long Unix(DateTime value) =>
        new DateTimeOffset(value, TimeSpan.Zero).ToUnixTimeSeconds();

    private static object Snapshot(CatalogPost post) =>
        new
        {
            id = post.Id,
            slug = post.Slug,
            url = post.Url,
            title = post.Title,
            name = post.Name,
            title_version = post.TitleVersion,
            build = post.Build,
            steam_app_id = post.SteamAppId,
            modified_utc = Unix(post.ModifiedUtc),
            published_utc = Unix(post.PublishedUtc),
            size_text = post.SizeText,
            mirrors = post
                .Mirrors.Select(m => new { host = m.Host.ToString(), url = m.Url })
                .ToList(),
            size_bytes = post.SizeBytes,
            category_ids = post.CategoryIds.ToList(),
        };

    /// <summary>
    /// One post through the original. A KeyNotFoundException is part of the recorded behaviour:
    /// ParseOne reads link with GetProperty, so a post without it throws instead of becoming null.
    /// </summary>
    private static object Case(string name, JsonElement post)
    {
        CatalogPost? parsed;
        try
        {
            parsed = CatalogParser.ParseOne(post);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            return new
            {
                name,
                post,
                parsed = (object?)null,
                error = ex.GetType().Name,
            };
        }

        return new
        {
            name,
            post,
            parsed = parsed is null ? null : Snapshot(parsed),
            error = (string?)null,
        };
    }

    [Fact]
    public void Parse_one_recordings_match()
    {
        var cases = new List<object>();

        // Every post of the saved first REST page, replayed with the real body, acf and dates.
        using (var doc = JsonDocument.Parse(Fixture.Read("posts_page1.json")))
        {
            foreach (var post in doc.RootElement.EnumerateArray())
            {
                var name = $"real post {post.GetProperty("id").GetInt64()}";
                cases.Add(Case(name, post.Clone()));
            }
        }

        cases.Add(
            Case(
                "acf absent",
                Post(
                    """
                    {
                      "id": 101,
                      "slug": "acf-absent",
                      "link": "https://steamrip.com/acf-absent/",
                      "title": { "rendered": "Acf Absent Free Download (v1.0)" },
                      "content": { "rendered": "<p>No mirrors here.</p>" },
                      "categories": [26, 33],
                      "modified_gmt": "2026-01-02T03:04:05",
                      "date_gmt": "2025-12-31T23:59:59"
                    }
                    """
                )
            )
        );

        cases.Add(
            Case(
                "acf empty object",
                Post(
                    """
                    {
                      "id": 102,
                      "slug": "acf-empty",
                      "link": "https://steamrip.com/acf-empty/",
                      "title": { "rendered": "Acf Empty Free Download (v1.0)" },
                      "content": { "rendered": "" },
                      "acf": {},
                      "categories": [],
                      "modified_gmt": "2026-01-02T03:04:05",
                      "date_gmt": "2025-12-31T23:59:59"
                    }
                    """
                )
            )
        );

        cases.Add(
            Case(
                "acf null",
                Post(
                    """
                    {
                      "id": 103,
                      "slug": "acf-null",
                      "link": "https://steamrip.com/acf-null/",
                      "title": { "rendered": "Acf Null Free Download" },
                      "content": { "rendered": "" },
                      "acf": null,
                      "categories": [],
                      "modified_gmt": "2026-01-02T03:04:05",
                      "date_gmt": "2025-12-31T23:59:59"
                    }
                    """
                )
            )
        );

        foreach (
            var (label, appId) in new[]
            {
                ("app id as number", "730"),
                ("app id as string", "\"730\""),
                ("app id string padded", "\"  730  \""),
                ("app id zero", "0"),
                ("app id negative", "-1"),
                ("app id negative string", "\"-42\""),
                ("app id junk string", "\"not-a-number\""),
                ("app id float", "1.5"),
                ("app id boolean", "true"),
                ("app id null", "null"),
                ("app id string overflow", "\"99999999999999999999\""),
            }
        )
        {
            cases.Add(
                Case(
                    label,
                    Post(
                        $$"""
                        {
                          "id": 104,
                          "slug": "app-id",
                          "link": "https://steamrip.com/app-id/",
                          "title": { "rendered": "App Id Free Download (v1.0)" },
                          "content": { "rendered": "" },
                          "acf": { "tagmetafield": "Build 9", "steam_app_id": {{appId}} },
                          "categories": [26],
                          "modified_gmt": "2026-01-02T03:04:05",
                          "date_gmt": "2025-12-31T23:59:59"
                        }
                        """
                    )
                )
            );
        }

        foreach (
            var (label, tagmetafield) in new[]
            {
                ("tagmetafield empty", "\"\""),
                ("tagmetafield whitespace", "\"   \""),
                ("tagmetafield padded", "\"  Build 1  \""),
                ("tagmetafield null", "null"),
                ("tagmetafield number", "42"),
            }
        )
        {
            cases.Add(
                Case(
                    label,
                    Post(
                        $$"""
                        {
                          "id": 105,
                          "slug": "tagmetafield",
                          "link": "https://steamrip.com/tagmetafield/",
                          "title": { "rendered": "Tag Metafield Free Download (v1.0)" },
                          "content": { "rendered": "" },
                          "acf": { "tagmetafield": {{tagmetafield}}, "steam_app_id": 730 },
                          "categories": [26],
                          "modified_gmt": "2026-01-02T03:04:05",
                          "date_gmt": "2025-12-31T23:59:59"
                        }
                        """
                    )
                )
            );
        }

        cases.Add(
            Case(
                "no content property",
                Post(
                    """
                    {
                      "id": 106,
                      "slug": "no-content",
                      "link": "https://steamrip.com/no-content/",
                      "title": { "rendered": "No Content Free Download (v1.0)" },
                      "categories": [26],
                      "modified_gmt": "2026-01-02T03:04:05",
                      "date_gmt": "2025-12-31T23:59:59"
                    }
                    """
                )
            )
        );

        cases.Add(
            Case(
                "content without rendered",
                Post(
                    """
                    {
                      "id": 107,
                      "slug": "content-empty",
                      "link": "https://steamrip.com/content-empty/",
                      "title": { "rendered": "Content Empty Free Download" },
                      "content": {},
                      "categories": [26],
                      "modified_gmt": "2026-01-02T03:04:05",
                      "date_gmt": "2025-12-31T23:59:59"
                    }
                    """
                )
            )
        );

        cases.Add(
            Case(
                "content with size and mirrors",
                Post(
                    """
                    {
                      "id": 108,
                      "slug": "content-with-mirrors",
                      "link": "https://steamrip.com/content-with-mirrors/",
                      "title": { "rendered": "Content With Mirrors Free Download (v2.1)" },
                      "content": {
                        "rendered": "<p><strong>Game Size:</strong> 12.3 GB</p><a class=\"shortc-button\" href=\"https://fileditch.com/f/game.rar\">FD</a><a class=\"shortc-button\" href=\"https://gofile.io/d/abc\">GF</a>"
                      },
                      "categories": [26, 33],
                      "modified_gmt": "2026-01-02T03:04:05",
                      "date_gmt": "2025-12-31T23:59:59"
                    }
                    """
                )
            )
        );

        cases.Add(
            Case(
                "categories null",
                Post(
                    """
                    {
                      "id": 109,
                      "slug": "categories-null",
                      "link": "https://steamrip.com/categories-null/",
                      "title": { "rendered": "Categories Null Free Download" },
                      "content": { "rendered": "" },
                      "categories": null,
                      "modified_gmt": "2026-01-02T03:04:05",
                      "date_gmt": "2025-12-31T23:59:59"
                    }
                    """
                )
            )
        );

        cases.Add(
            Case(
                "categories absent",
                Post(
                    """
                    {
                      "id": 110,
                      "slug": "categories-absent",
                      "link": "https://steamrip.com/categories-absent/",
                      "title": { "rendered": "Categories Absent Free Download" },
                      "content": { "rendered": "" },
                      "modified_gmt": "2026-01-02T03:04:05",
                      "date_gmt": "2025-12-31T23:59:59"
                    }
                    """
                )
            )
        );

        cases.Add(
            Case(
                "categories skip non-integral numbers",
                Post(
                    """
                    {
                      "id": 111,
                      "slug": "categories-numbers",
                      "link": "https://steamrip.com/categories-numbers/",
                      "title": { "rendered": "Categories Numbers Free Download" },
                      "content": { "rendered": "" },
                      "categories": [26, 33.0, 4.5, 63, 99999999999999999999],
                      "modified_gmt": "2026-01-02T03:04:05",
                      "date_gmt": "2025-12-31T23:59:59"
                    }
                    """
                )
            )
        );

        cases.Add(
            Case(
                "categories with a string entry throws",
                Post(
                    """
                    {
                      "id": 121,
                      "slug": "categories-string",
                      "link": "https://steamrip.com/categories-string/",
                      "title": { "rendered": "Categories String Free Download" },
                      "content": { "rendered": "" },
                      "categories": [26, "27", 63],
                      "modified_gmt": "2026-01-02T03:04:05",
                      "date_gmt": "2025-12-31T23:59:59"
                    }
                    """
                )
            )
        );

        cases.Add(
            Case(
                "categories with a null entry throws",
                Post(
                    """
                    {
                      "id": 122,
                      "slug": "categories-null-entry",
                      "link": "https://steamrip.com/categories-null-entry/",
                      "title": { "rendered": "Categories Null Entry Free Download" },
                      "content": { "rendered": "" },
                      "categories": [26, null, 63],
                      "modified_gmt": "2026-01-02T03:04:05",
                      "date_gmt": "2025-12-31T23:59:59"
                    }
                    """
                )
            )
        );

        cases.Add(
            Case(
                "slug absent",
                Post(
                    """
                    {
                      "id": 112,
                      "link": "https://steamrip.com/slug-absent/",
                      "title": { "rendered": "Slug Absent Free Download" },
                      "content": { "rendered": "" },
                      "categories": [],
                      "modified_gmt": "2026-01-02T03:04:05",
                      "date_gmt": "2025-12-31T23:59:59"
                    }
                    """
                )
            )
        );

        cases.Add(
            Case(
                "slug null",
                Post(
                    """
                    {
                      "id": 113,
                      "slug": null,
                      "link": "https://steamrip.com/slug-null/",
                      "title": { "rendered": "Slug Null Free Download" },
                      "content": { "rendered": "" },
                      "categories": [],
                      "modified_gmt": "2026-01-02T03:04:05",
                      "date_gmt": "2025-12-31T23:59:59"
                    }
                    """
                )
            )
        );

        cases.Add(
            Case(
                "HTML encoded title with named entity",
                Post(
                    """
                    {
                      "id": 114,
                      "slug": "html-title-amp",
                      "link": "https://steamrip.com/html-title-amp/",
                      "title": { "rendered": "Tom &amp; Jerry Free Download (v1.0)" },
                      "content": { "rendered": "" },
                      "categories": [],
                      "modified_gmt": "2026-01-02T03:04:05",
                      "date_gmt": "2025-12-31T23:59:59"
                    }
                    """
                )
            )
        );

        cases.Add(
            Case(
                "HTML encoded title with numeric entity",
                Post(
                    """
                    {
                      "id": 115,
                      "slug": "html-title-numeric",
                      "link": "https://steamrip.com/html-title-numeric/",
                      "title": { "rendered": "R&#8217;s Quest Free Download (v2)" },
                      "content": { "rendered": "" },
                      "categories": [],
                      "modified_gmt": "2026-01-02T03:04:05",
                      "date_gmt": "2025-12-31T23:59:59"
                    }
                    """
                )
            )
        );

        cases.Add(
            Case(
                "title without the download suffix",
                Post(
                    """
                    {
                      "id": 116,
                      "slug": "plain-title",
                      "link": "https://steamrip.com/plain-title/",
                      "title": { "rendered": "Plain Title Without Suffix" },
                      "content": { "rendered": "" },
                      "categories": [],
                      "modified_gmt": "2026-01-02T03:04:05",
                      "date_gmt": "2025-12-31T23:59:59"
                    }
                    """
                )
            )
        );

        cases.Add(
            Case(
                "unreadable dates become DateTime.MinValue",
                Post(
                    """
                    {
                      "id": 117,
                      "slug": "unreadable-dates",
                      "link": "https://steamrip.com/unreadable-dates/",
                      "title": { "rendered": "Unreadable Dates Free Download" },
                      "content": { "rendered": "" },
                      "categories": [],
                      "modified_gmt": "not a date",
                      "date_gmt": ""
                    }
                    """
                )
            )
        );

        cases.Add(
            Case(
                "dates fall back from gmt to the non-gmt field",
                Post(
                    """
                    {
                      "id": 118,
                      "slug": "date-fallback",
                      "link": "https://steamrip.com/date-fallback/",
                      "title": { "rendered": "Date Fallback Free Download" },
                      "content": { "rendered": "" },
                      "categories": [],
                      "modified_gmt": "not a date",
                      "modified": "2026-02-03T04:05:06",
                      "date_gmt": 123,
                      "date": "2026-01-01T00:00:00Z"
                    }
                    """
                )
            )
        );

        cases.Add(
            Case(
                "dates absent",
                Post(
                    """
                    {
                      "id": 119,
                      "slug": "dates-absent",
                      "link": "https://steamrip.com/dates-absent/",
                      "title": { "rendered": "Dates Absent Free Download" },
                      "content": { "rendered": "" },
                      "categories": []
                    }
                    """
                )
            )
        );

        cases.Add(
            Case(
                "missing id returns null",
                Post(
                    """
                    {
                      "slug": "missing-id",
                      "link": "https://steamrip.com/missing-id/",
                      "title": { "rendered": "Missing Id Free Download" },
                      "content": { "rendered": "" },
                      "categories": []
                    }
                    """
                )
            )
        );

        cases.Add(
            Case(
                "missing link throws",
                Post(
                    """
                    {
                      "id": 120,
                      "slug": "missing-link",
                      "title": { "rendered": "Missing Link Free Download" },
                      "content": { "rendered": "" },
                      "categories": []
                    }
                    """
                )
            )
        );

        RecordOrCheck("parse_one.json", cases);
    }
}
