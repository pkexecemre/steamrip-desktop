using SteamRip.Core.Downloads;

namespace SteamRip.Core.Tests;

/// <summary>Recordings for the resume math: append or restart, where to start, and the total.</summary>
public class GoldenRangePlanTests
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
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

    [Fact]
    public void Range_plan_recordings_match()
    {
        (int Status, long Have, long? Range, long? Length)[] cases =
        [
            (206, 100, 500, null),
            (206, 100, null, 500),
            (206, 0, 500, 500),
            (200, 100, null, 500),
            (200, 0, null, 500),
            (200, 0, 500, 500),
            (416, 500, null, null),
            (302, 100, null, 500),
            (206, 100, null, null),
            (200, 100, null, null),
        ];

        var recorded = cases
            .Select(c =>
            {
                var plan = Downloader.PlanRange(c.Status, c.Have, c.Range, c.Length);
                return new
                {
                    status = c.Status,
                    have = c.Have,
                    content_range_length = c.Range,
                    content_length = c.Length,
                    append = plan.Append,
                    start = plan.Start,
                    total = plan.Total,
                };
            })
            .ToList();

        var dir = FixtureDirectory();
        var path = Path.Combine(dir, "range_plan.json");
        var payload =
            System.Text.Json.JsonSerializer.Serialize(new { cases = recorded }, Json)
            + Environment.NewLine;
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SR_GOLDEN_DIR")))
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, payload);
            return;
        }

        Assert.True(File.Exists(path), $"missing recording {path}: run make golden");
        Assert.Equal(payload, File.ReadAllText(path));
    }
}
