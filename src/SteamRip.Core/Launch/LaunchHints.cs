using System.Text.RegularExpressions;

namespace SteamRip.Core.Launch;

/// <summary>What a game that died on its own was holding: the exit code, how long it lasted and what it printed.</summary>
public sealed record LaunchFailure(
    int Code,
    TimeSpan Elapsed,
    string ExePath,
    string? ProtonPath,
    IReadOnlyList<string> Output,
    IReadOnlyList<string> SteamFiles,
    bool CommonRedist,
    bool Windows = false
);

/// <summary>One line for the library card plus the paragraph that goes to the log.</summary>
public sealed record LaunchHint(string Card, string Detail);

/// <summary>
/// A game that exits nonzero within <see cref="FastFailure"/> never got to its main loop, and its own output is the
/// only thing it tells us. Measured on a real OnlineFix/StubDRM release (Castle Crashers, STEAMRIP, app id 204360):
/// exit 66 after 23–111 s, no game window, and a modal "Initialization error 1" dialog owned by the game process —
/// i.e. a startup failure, not a crash in play. The launcher cannot see windows, so the trigger is the fast nonzero
/// exit; this class maps what the game printed and the files the release shipped onto the handful of causes that
/// actually produce that signature under umu-run + Proton.
/// </summary>
public static partial class LaunchHints
{
    /// <summary>A game still running after this has had its chance to put a window up; past it a nonzero exit is a crash, not a bad launch.</summary>
    public static readonly TimeSpan FastFailure = TimeSpan.FromSeconds(90);

    /// <summary>Steam-layer files: the Steamworks SDK itself or a crack that replaces it. Both need a Steam client.</summary>
    private static readonly string[] SteamNames =
    [
        "steam_api.dll",
        "steam_api64.dll",
        "steam_api.of",
        "steam_api64.of",
        "steam_appid.txt",
        "steamclient.dll",
        "steamclient64.dll",
        "onlinefix.dll",
        "onlinefix64.dll",
        "onlinefix.ini",
        "stubdrm32.dll",
        "stubdrm64.dll",
        "cream_api.ini",
        "steam_settings",
    ];

    /// <summary>The subset that is a crack, not Valve's SDK: these initialise against the running Steam client.</summary>
    private static readonly string[] CrackNames =
    [
        "steam_api.of",
        "steam_api64.of",
        "onlinefix",
        "stubdrm",
        "cream_api",
        "steam_settings",
    ];

    /// <summary>
    /// Names of Steam-layer files shipped next to the exe or at the release root. Cheap on purpose: two directory
    /// listings, no walk, because this runs while the card is being refreshed.
    /// </summary>
    public static IReadOnlyList<string> SteamFiles(string exePath, string? gameRoot)
    {
        var found = new List<string>();
        foreach (var dir in new[] { Path.GetDirectoryName(exePath), gameRoot })
        {
            if (string.IsNullOrEmpty(dir))
                continue;
            try
            {
                if (!Directory.Exists(dir))
                    continue;
                foreach (var path in Directory.EnumerateFileSystemEntries(dir))
                {
                    var name = Path.GetFileName(path);
                    var lower = name.ToLowerInvariant();
                    if (!SteamNames.Contains(lower))
                        continue;
                    if (!found.Contains(name, StringComparer.OrdinalIgnoreCase))
                        found.Add(name);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // a folder we cannot read is not evidence of anything; carry on with what we have
            }
        }
        return found;
    }

    /// <summary>True when one of the found names is a crack rather than Valve's own steam_api.</summary>
    public static bool IsCrack(IEnumerable<string> steamFiles) =>
        steamFiles.Any(n => CrackNames.Any(c => n.Contains(c, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// The hint for a failed launch, or null when the exit is none of the launcher's business (clean exit, Windows,
    /// or a run long enough that the startup path obviously worked).
    /// </summary>
    public static LaunchHint? Explain(LaunchFailure failure)
    {
        if (failure.Windows || failure.Code == 0 || failure.Elapsed > FastFailure)
            return null;

        var text = string.Join('\n', failure.Output);
        var missing = MissingDll().Match(text) is { Success: true } m ? m.Groups[1].Value : null;
        var steam = failure.SteamFiles;
        var crack = IsCrack(steam);
        var ran = Duration(failure.Elapsed);
        var card =
            missing is not null
                ? $"{missing} is missing in the prefix — run the release's _CommonRedist"
            : crack ? "Steam/DRM check failed — start Steam, sign in, then Play again"
            : steam.Count > 0 ? "Steamworks init failed — start Steam and sign in, then Play again"
            : "it never got going — try another Proton or another exe in launch options";

        var detail = new List<string>
        {
            $"{Path.GetFileName(failure.ExePath)} exited with {failure.Code} after {ran}, inside the {FastFailure.TotalSeconds:0} s startup window; the launcher cannot see windows, so a fast nonzero exit is the signal.",
        };
        if (missing is not null)
            detail.Add(
                $"- {missing} is not in the prefix: Wine printed \"import_dll Library {missing} … not found\". The release's _CommonRedist installers (DirectX, VC++, XNA, OpenAL) install it; the game's menu has 'Run exe…' for them."
            );
        if (steam.Count > 0)
        {
            var what = crack
                ? "a Steam crack that initialises against the Steam client"
                : "the Steamworks SDK";
            detail.Add(
                $"- The game folder ships {what}: {string.Join(", ", steam)}. That path needs a Steam client it can reach before the game starts; with none, the game's own check fails before it can open a window (measured here: a modal 'Initialization error 1' dialog from OnlineFix, then exit 66; Steam was running on this desktop and it still failed, because umu-run cannot hand Proton the desktop Steam client)."
            );
        }
        if (failure.CommonRedist && missing is null)
            detail.Add(
                "- The release carries _CommonRedist (VC++/DirectX/XNA/OpenAL installers). If none of them ran for this prefix, install them from the game's menu ('Run exe…')."
            );
        detail.Add(
            $"- Proton: {failure.ProtonPath ?? "umu default (UMU-Proton)"} is what this launch used; a build that this title is known to need can be picked per game in launch options (GE-Proton is the default)."
        );
        detail.Add("- Activity has the full runner output; 'Stop' then Play again after the fix.");
        return new LaunchHint(
            $"Exited with {failure.Code} after {ran} · {card}",
            string.Join('\n', detail)
        );
    }

    /// <summary>"44 s", "3 min 12 s" — short enough for the card's status line.</summary>
    public static string Duration(TimeSpan elapsed) =>
        elapsed.TotalSeconds < 60
            ? $"{elapsed.TotalSeconds:0} s"
            : $"{(int)elapsed.TotalMinutes} min {elapsed.Seconds:00} s";

    /// <summary>Wine's missing-DLL line: <c>err:module:import_dll Library d3dx9_43.dll … not found</c>.</summary>
    [GeneratedRegex(@"import_dll Library (\S+?\.dll)", RegexOptions.IgnoreCase)]
    private static partial Regex MissingDll();
}
