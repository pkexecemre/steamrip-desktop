using SteamRip.Core.Launch;

namespace SteamRip.Core.Tests;

/// <summary>
/// The one-line "why did it die" for a launch that failed during startup. The cases are the real Castle Crashers
/// signature (exit 66, an OnlineFix/StubDRM folder, no output) and the shapes it shares with a missing redist or a
/// plain Steamworks game. See <see cref="LaunchHints"/>.
/// </summary>
public sealed class LaunchHintsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sr-hints-" + Guid.NewGuid().ToString("N")
    );

    public LaunchHintsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // the temp dir gets cleaned by the OS
        }
        catch (UnauthorizedAccessException)
        {
            // same
        }
    }

    private static readonly string Exe = Path.Combine(
        Path.GetTempPath(),
        "library",
        "castle",
        "Castle Crashers",
        "castle.exe"
    );

    private static LaunchFailure Failure(
        int code = 66,
        double seconds = 23,
        IReadOnlyList<string>? output = null,
        IReadOnlyList<string>? steam = null,
        bool commonRedist = false,
        bool windows = false
    ) =>
        new(
            code,
            TimeSpan.FromSeconds(seconds),
            Exe,
            "GE-Proton",
            output ?? [],
            steam ?? [],
            commonRedist,
            windows
        );

    [Fact]
    public void A_clean_or_slow_or_windows_exit_is_not_a_launch_failure()
    {
        Assert.Null(LaunchHints.Explain(Failure(code: 0)));
        Assert.Null(LaunchHints.Explain(Failure(seconds: 91)));
        Assert.Null(LaunchHints.Explain(Failure(windows: true)));
        // 90 s is still "during startup": the game has not had its chance yet.
        Assert.NotNull(LaunchHints.Explain(Failure(seconds: 90)));
    }

    [Fact]
    public void The_measured_castle_crashers_signature_names_the_steam_crack()
    {
        var hint = LaunchHints.Explain(
            Failure(steam: ["OnlineFix.dll", "OnlineFix.ini", "StubDRM32.dll", "steam_api.of"])
        );

        Assert.NotNull(hint);
        Assert.Contains("Exited with 66 after 23 s", hint.Card);
        Assert.Contains("Steam/DRM", hint.Card);
        Assert.Contains("start Steam", hint.Card);
        Assert.Contains("OnlineFix.dll", hint.Detail);
        Assert.Contains("exit 66", hint.Detail);
        Assert.Contains("cannot see windows", hint.Detail);
    }

    [Fact]
    public void A_plain_steamworks_game_is_not_called_a_crack()
    {
        var hint = LaunchHints.Explain(Failure(steam: ["steam_api.dll"]));

        Assert.NotNull(hint);
        Assert.Contains("Steamworks init failed", hint.Card);
        Assert.DoesNotContain("crack", hint.Card);
    }

    [Fact]
    public void A_missing_dll_in_the_runner_output_wins_over_the_steam_guess()
    {
        string[] output =
        [
            "wine: using a 32-bit prefix",
            "err:module:import_dll Library d3dx9_43.dll (which is needed by L\"Z:\\games\\castle.exe\") not found",
            "err:module:LdrInitializeThunk Main exe initialization failed, status c0000135",
        ];

        var hint = LaunchHints.Explain(Failure(output: output, steam: ["steam_api.dll"]));

        Assert.NotNull(hint);
        Assert.Contains("d3dx9_43.dll is missing", hint.Card);
        Assert.Contains("_CommonRedist", hint.Card);
        Assert.Contains("DirectX", hint.Detail);
        Assert.DoesNotContain("Steamworks init failed", hint.Card);
    }

    [Fact]
    public void An_unknown_fast_failure_still_says_what_to_try()
    {
        var hint = LaunchHints.Explain(Failure(commonRedist: true));

        Assert.NotNull(hint);
        Assert.Contains("never got going", hint.Card);
        Assert.Contains("_CommonRedist", hint.Detail);
        Assert.Contains("GE-Proton", hint.Detail);
    }

    [Fact]
    public void Steam_files_are_found_next_to_the_exe_and_at_the_release_root()
    {
        var exeDir = Path.Combine(_root, "release", "Castle Crashers");
        var root = Path.Combine(_root, "release");
        Directory.CreateDirectory(exeDir);
        File.WriteAllText(Path.Combine(exeDir, "castle.exe"), "");
        File.WriteAllText(Path.Combine(exeDir, "steam_api.of"), "");
        File.WriteAllText(Path.Combine(exeDir, "OnlineFix.dll"), "");
        File.WriteAllText(Path.Combine(exeDir, "unrelated.dll"), "");
        File.WriteAllText(Path.Combine(root, "steam_appid.txt"), "204360");

        var found = LaunchHints.SteamFiles(Path.Combine(exeDir, "castle.exe"), root);

        Assert.Contains("steam_api.of", found);
        Assert.Contains("OnlineFix.dll", found);
        Assert.Contains("steam_appid.txt", found);
        Assert.DoesNotContain("unrelated.dll", found);
        Assert.True(LaunchHints.IsCrack(found));
        Assert.False(LaunchHints.IsCrack(["steam_api.dll"]));
    }

    [Fact]
    public void Duration_stays_short_enough_for_a_status_line()
    {
        Assert.Equal("23 s", LaunchHints.Duration(TimeSpan.FromSeconds(23)));
        Assert.Equal("1 min 51 s", LaunchHints.Duration(TimeSpan.FromSeconds(111)));
    }
}
