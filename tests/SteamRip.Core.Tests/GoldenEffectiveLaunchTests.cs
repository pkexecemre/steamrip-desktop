using SteamRip.Core.Launch;

namespace SteamRip.Core.Tests;

/// <summary>
/// Recordings for the launch-options precedence every launch uses: the per-game override, then the
/// user's defaults, then the app defaults. Checked in C#, replayed in Rust.
/// </summary>
public class GoldenEffectiveLaunchTests
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

    private static object Snapshot(LaunchOptions o) =>
        new
        {
            o.MangoHud,
            o.MangoHudConfig,
            o.GameMode,
            o.Gamescope,
            o.GamescopeArgs,
            o.Hdr,
            o.Wayland,
            o.Fsr,
            o.Fsync,
            o.ProtonLog,
            o.DllOverrides,
            o.Environment,
            o.Wrapper,
        };

    [Fact]
    public void Effective_launch_recordings_match()
    {
        (LaunchOptions Game, LaunchOptions User)[] cases =
        [
            (new LaunchOptions(), new LaunchOptions()),
            (
                new LaunchOptions(MangoHud: true, Wrapper: "mangohud"),
                new LaunchOptions(GameMode: false, Hdr: true)
            ),
            (
                new LaunchOptions(GameMode: true, Environment: "GAME=1"),
                new LaunchOptions(GameMode: false, Environment: "")
            ),
            (
                new LaunchOptions(GamescopeArgs: "-W 1920 -H 1080", DllOverrides: "xinput1_4=n,b"),
                new LaunchOptions(GamescopeArgs: "  ", DllOverrides: "d3d11=n")
            ),
            (
                new LaunchOptions(Fsync: false, ProtonLog: true, Fsr: true),
                new LaunchOptions(Fsync: true, ProtonLog: false)
            ),
        ];

        var recorded = cases
            .Select(c => new
            {
                game = Snapshot(c.Game),
                user = Snapshot(c.User),
                effective = Snapshot(c.Game.MergeOver(c.User).MergeOver(LaunchOptions.AppDefaults)),
            })
            .ToList();

        RecordOrCheck("effective_launch.json", recorded);
    }
}
