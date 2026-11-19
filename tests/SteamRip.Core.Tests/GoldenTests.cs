using System.Text.Json;
using System.Text.Json.Serialization;
using SteamRip.Core.Catalog;
using SteamRip.Core.Data;
using SteamRip.Core.Downloads;
using SteamRip.Core.Hosters;
using SteamRip.Core.Install;
using SteamRip.Core.Launch;
using SteamRip.Core.Platform;
using SteamRip.Core.Updates;

namespace SteamRip.Core.Tests;

/// <summary>
/// Writes the recordings the Rust port is checked against, and keeps them honest: the same
/// file is read back and compared byte for byte with what the C# implementation answers today,
/// so a behaviour change here fails here first. Set <c>SR_GOLDEN_DIR</c> (<c>make golden</c>) to record.
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

    [Fact]
    public void Sniff_recordings_match()
    {
        string[] heads =
        [
            "52617221", // Rar!
            "52617221" + "1A0700",
            "377abcaf271c", // 7z signature
            "377abc", // three bytes: not enough for the 7z rule
            "504b0304", // PK.. zip
            "504b", // PK only
            "64383a", // d8:
            "64383a616e6e6f756e6365", // d8:announce
            "6462", // d8 without the colon
            "3c68746d6c3e", // <html>
            "3c", // one byte
            "efbbbf", // utf-8 bom only
            "00",
            "",
            "5261", // Ra
            "526172", // Rar
        ];

        var cases = heads
            .Select(hex =>
            {
                var bytes = Convert.FromHexString(hex);
                return new { head_hex = hex, kind = ArchiveTool.SniffKind(bytes) };
            })
            .ToList();

        RecordOrCheck("sniff.json", cases);
    }

    [Fact]
    public void First_part_recordings_match()
    {
        (string Input, string[] Existing)[] cases =
        [
            ("Foo.part03.rar", ["Foo.part01.rar"]),
            ("Foo.part03.rar", []),
            ("Foo.part001.rar", ["Foo.part001.rar"]),
            ("Foo.part02.rar", ["Foo.part01.rar", "Foo.part02.rar"]),
            ("Foo.r05", ["Foo.rar"]),
            ("Foo.r05", []),
            ("Foo.z02", ["Foo.zip"]),
            ("Foo.7z.004", ["Foo.7z.001"]),
            ("Foo.7z.001", ["Foo.7z.001"]),
            ("Foo.zip", []),
            ("Bar.PART02.RAR", ["Bar.part01.RAR"]),
            ("plain.rar", []),
        ];

        var recorded = new List<object>();
        foreach (var (input, existing) in cases)
        {
            var dir = Directory.CreateTempSubdirectory("sr-firstpart-");
            try
            {
                foreach (var name in existing)
                    File.WriteAllBytes(Path.Combine(dir.FullName, name), []);

                var got = ArchiveTool.FirstPart(Path.Combine(dir.FullName, input));
                recorded.Add(
                    new
                    {
                        input,
                        existing,
                        expected = Path.GetFileName(got),
                    }
                );
            }
            finally
            {
                dir.Delete(true);
            }
        }

        RecordOrCheck("first_part.json", recorded);
    }

    [Fact]
    public void Safe_name_recordings_match()
    {
        string[] slugs =
        [
            "Foo/Bar",
            "  Foo  ",
            "ÄÖÜ ç",
            "",
            "...",
            "---",
            "a.b",
            "Game: Deluxe",
            "Über/Game",
            "Already_Ok-1.2",
            "Çok  Özel / Oyun",
        ];

        var cases = slugs
            .Select(slug => new { input = slug, safe = PrefixManager.SafeName(slug) })
            .ToList();

        RecordOrCheck("safe_name.json", cases);
    }

    [Fact]
    public void Game_id_recordings_match()
    {
        (long? AppId, string? Slug)[] cases =
        [
            (2483190, "forza-horizon-6"),
            (null, "My Game!"),
            (null, null),
            (null, ""),
            (null, "   "),
            (100, null),
        ];

        var recorded = cases
            .Select(c => new
            {
                appid = c.AppId,
                slug = c.Slug,
                id = PrefixManager.GameId(c.AppId, c.Slug),
            })
            .ToList();

        RecordOrCheck("game_id.json", recorded);
    }

    [Fact]
    public void Format_size_recordings_match()
    {
        long[] sizes =
        [
            0,
            999,
            1023,
            1024,
            1536,
            1024 * 1024,
            1024 * 1024 + 512 * 1024,
            5 * 1024 * 1024,
            1024L * 1024 * 1024,
            (long)(1.25 * 1024 * 1024 * 1024),
            (long)(2.5 * 1024 * 1024),
            1536L * 1024 * 1024,
        ];

        var cases = sizes
            .Select(bytes => new { bytes, text = PrefixManager.FormatSize(bytes) })
            .ToList();

        RecordOrCheck("format_size.json", cases);
    }

    [Fact]
    public void Prefix_path_recordings_match()
    {
        (string Library, string? Configured)[] cases =
        [
            ("/games/SteamRIP", null),
            ("/games/SteamRIP", ""),
            ("/games/SteamRIP", "   "),
            ("/games/SteamRIP", "/mnt/prefixes"),
            ("/games/SteamRIP", "  /mnt/prefixes  "),
        ];

        var recorded = cases
            .Select(c => new
            {
                library = c.Library,
                configured = c.Configured,
                root = PrefixManager.Root(c.Library, c.Configured),
                dir = PrefixManager.Dir(PrefixManager.Root(c.Library, c.Configured), "Some Game"),
            })
            .ToList();

        RecordOrCheck("prefix_paths.json", recorded);
    }

    [Fact]
    public void Environment_recordings_match()
    {
        string?[] texts =
        [
            null,
            "",
            "A=1\nB=2",
            "export A=1\n# comment\nexport B = two words ",
            "A=1\r\nB=2",
            "bad key=1\nOK=2",
            "=empty\nQUOTED=\"say hi\"",
            "NOEQUALS\nX=  1  ",
            "E=\"\"",
        ];

        var recorded = texts
            .Select(text => new
            {
                input = text,
                entries = LaunchOptions
                    .ParseEnvironment(text)
                    .Select(kv => new[] { kv.Key, kv.Value })
                    .ToList(),
            })
            .ToList();

        RecordOrCheck("environment.json", recorded);
    }

    [Fact]
    public void Merge_recordings_match()
    {
        (LaunchOptions Mine, LaunchOptions Defaults)[] cases =
        [
            (new LaunchOptions(), LaunchOptions.AppDefaults),
            (new LaunchOptions(MangoHud: true), LaunchOptions.AppDefaults),
            (new LaunchOptions(GameMode: false), LaunchOptions.AppDefaults),
            (new LaunchOptions(GameMode: null, MangoHudConfig: "  "), LaunchOptions.AppDefaults),
            (
                new LaunchOptions(
                    Gamescope: true,
                    GamescopeArgs: "-W 2560 -H 1440",
                    Environment: "A=1",
                    Wrapper: "mangohud"
                ),
                new LaunchOptions(GameMode: false, GamescopeArgs: "-f")
            ),
        ];

        var recorded = cases
            .Select(c => new
            {
                mine = Snapshot(c.Mine),
                defaults = Snapshot(c.Defaults),
                merged = Snapshot(c.Mine.MergeOver(c.Defaults)),
            })
            .ToList();

        RecordOrCheck("merge.json", recorded);
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
    public void Compose_recordings_match()
    {
        LaunchSpec Spec(
            long? appId,
            string? slug,
            string? proton,
            string? args,
            LaunchOptions? options
        ) =>
            new(
                1,
                "Test Game",
                "/games/Test Game",
                "game.exe",
                proton,
                args,
                appId,
                options,
                "/prefixes/Test Game",
                slug
            );

        LaunchOptions With(
            bool? gameMode = null,
            bool? gamescope = null,
            string? gamescopeArgs = null,
            bool? mangoHud = null,
            string? mangoHudConfig = null,
            bool? hdr = null,
            bool? wayland = null,
            bool? fsr = null,
            bool? fsync = null,
            bool? protonLog = null,
            string? dllOverrides = null,
            string? environment = null,
            string? wrapper = null
        ) =>
            new(
                mangoHud,
                mangoHudConfig,
                gameMode,
                gamescope,
                gamescopeArgs,
                hdr,
                wayland,
                fsr,
                fsync,
                protonLog,
                dllOverrides,
                environment,
                wrapper
            );

        (
            string Name,
            LaunchSpec Spec,
            string? Runner,
            string[] Tools,
            (int Width, int Height)? Screen
        )[] cases =
        [
            ("direct, no runner", Spec(null, null, null, "--dry-run", null), null, [], null),
            (
                "umu + defaults, gamemode missing",
                Spec(2483190, "test-game", "GE-Proton11-12", null, With(gameMode: true)),
                "/usr/bin/umu-run",
                [],
                null
            ),
            (
                "umu + gamemoderun",
                Spec(2483190, "test-game", "GE-Proton11-12", null, With(gameMode: true)),
                "/usr/bin/umu-run",
                ["gamemoderun"],
                null
            ),
            (
                "gamescope with screen and hdr and mangohud",
                Spec(
                    2483190,
                    "test-game",
                    "GE-Proton11-12",
                    "\"hello world\" --flag",
                    With(
                        gameMode: false,
                        gamescope: true,
                        gamescopeArgs: "-f --adaptive-sync",
                        mangoHud: true,
                        mangoHudConfig: "gpu_stats",
                        hdr: true
                    )
                ),
                "/usr/bin/umu-run",
                ["gamescope"],
                (2560, 1440)
            ),
            (
                "gamescope enabled but missing",
                Spec(null, "sluggy", null, null, With(gamescope: true, gamescopeArgs: "-f")),
                "/usr/bin/umu-run",
                [],
                null
            ),
            (
                "hdr without gamescope keeps wayland on",
                Spec(null, null, null, null, With(hdr: true)),
                "/usr/bin/umu-run",
                [],
                null
            ),
            (
                "hdr without gamescope and wayland already set",
                Spec(null, null, null, null, With(hdr: true, wayland: true)),
                "/usr/bin/umu-run",
                [],
                null
            ),
            (
                "hdr with gamescope and ENABLE_HDR_WSI in env",
                Spec(
                    null,
                    null,
                    null,
                    null,
                    With(gamescope: true, hdr: true, environment: "ENABLE_HDR_WSI=1")
                ),
                "/usr/bin/umu-run",
                ["gamescope"],
                null
            ),
            (
                "forced hdr env warns and still applies",
                Spec(
                    5,
                    "s",
                    "/opt/proton",
                    null,
                    With(environment: "DXVK_HDR=1\nPROTON_USE_HDR=1\nOWN=kept")
                ),
                "/usr/bin/umu-run",
                [],
                null
            ),
            (
                "wrapper, fsr, no fsync, proton log, dll overrides",
                Spec(
                    5,
                    null,
                    null,
                    null,
                    With(
                        gameMode: false,
                        fsr: true,
                        fsync: false,
                        protonLog: true,
                        dllOverrides: "  xinput1_4=n,b  ",
                        wrapper: "mangohud --dlsym"
                    )
                ),
                "/usr/bin/umu-run",
                [],
                null
            ),
        ];

        var recorded = cases
            .Select(c =>
            {
                var compose = GameLauncher.Compose(
                    c.Spec,
                    c.Runner,
                    "/games/Test Game/game.exe",
                    "/prefixes/Test Game",
                    tool => c.Tools.Contains(tool) ? "/usr/bin/" + tool : null,
                    c.Screen
                );
                return new
                {
                    name = c.Name,
                    runner = c.Runner,
                    tools = c.Tools,
                    screen = c.Screen is { } s ? new { width = s.Width, height = s.Height } : null,
                    spec = new
                    {
                        steam_app_id = c.Spec.SteamAppId,
                        slug = c.Spec.Slug,
                        proton_path = c.Spec.ProtonPath,
                        arguments = c.Spec.Arguments,
                        options = Snapshot(c.Spec.Options ?? LaunchOptions.AppDefaults),
                    },
                    command = compose.Command.ToList(),
                    environment = compose
                        .Environment.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                        .Select(kv => new[] { kv.Key, kv.Value })
                        .ToList(),
                    warnings = compose.Warnings.ToList(),
                };
            })
            .ToList();

        RecordOrCheck("compose.json", recorded);
    }

    [Fact]
    public void Host_page_recordings_match()
    {
        var cases = new List<object>();

        void FileDitch(string? file, string? html)
        {
            try
            {
                var (url, expires, fileName) = HostPages.ParseFileDitch(
                    html ?? Fixture.Read(file!)
                );
                cases.Add(
                    new
                    {
                        kind = "fileditch",
                        html_file = file,
                        html,
                        url,
                        expires = expires is { } e
                            ? new DateTimeOffset(e, TimeSpan.Zero).ToUnixTimeSeconds()
                            : (long?)null,
                        file_name = fileName,
                        error = (string?)null,
                    }
                );
            }
            catch (FormatException ex)
            {
                cases.Add(
                    new
                    {
                        kind = "fileditch",
                        html_file = file,
                        html,
                        url = (string?)null,
                        expires = (long?)null,
                        file_name = (string?)null,
                        error = ex.Message,
                    }
                );
            }
        }

        void Bzzhr(string? file, string? html)
        {
            try
            {
                var (hx, md5, sha, fileName) = HostPages.ParseBzzhr(html ?? Fixture.Read(file!));
                cases.Add(
                    new
                    {
                        kind = "bzzhr",
                        html_file = file,
                        html,
                        hx,
                        md5,
                        sha256 = sha,
                        file_name = fileName,
                        error = (string?)null,
                    }
                );
            }
            catch (FormatException ex)
            {
                cases.Add(
                    new
                    {
                        kind = "bzzhr",
                        html_file = file,
                        html,
                        hx = (string?)null,
                        md5 = (string?)null,
                        sha256 = (string?)null,
                        file_name = (string?)null,
                        error = ex.Message,
                    }
                );
            }
        }

        void MegaDb(string? file, string? html)
        {
            try
            {
                var (pt, wait, hash, fileName) = HostPages.ParseMegaDb(html ?? Fixture.Read(file!));
                cases.Add(
                    new
                    {
                        kind = "megadb",
                        html_file = file,
                        html,
                        url = pt,
                        wait,
                        hash,
                        file_name = fileName,
                        error = (string?)null,
                    }
                );
            }
            catch (FormatException ex)
            {
                cases.Add(
                    new
                    {
                        kind = "megadb",
                        html_file = file,
                        html,
                        url = (string?)null,
                        wait = (int?)null,
                        hash = (string?)null,
                        file_name = (string?)null,
                        error = ex.Message,
                    }
                );
            }
        }

        FileDitch("fileditch_solved.html", null);
        FileDitch("fileditch_interstitial.html", null);
        FileDitch(null, "<html><head><title>Nothing</title></head><body></body></html>");
        Bzzhr("bzzhr_solved.html", null);
        Bzzhr(null, "<html><head><title>Nothing</title></head></html>");
        MegaDb("megadb_landing.html", null);
        MegaDb(null, "<html><head><title>Nothing</title></head></html>");

        RecordOrCheck("host_pages.json", cases);
    }

    [Fact]
    public void Gofile_token_recordings_match()
    {
        (string Token, long Seconds)[] cases =
        [
            ("tok", 14400L * 1000),
            ("tok", 14400L * 1000 + 60),
            ("tok", 14400L * 1001),
            ("", 0),
            ("account-token-with-dashes", 1794000000),
        ];

        var recorded = cases
            .Select(c => new
            {
                token = c.Token,
                unix_seconds = c.Seconds,
                expected = GoFileResolver.WebsiteToken(
                    c.Token,
                    DateTimeOffset.FromUnixTimeSeconds(c.Seconds)
                ),
            })
            .ToList();

        RecordOrCheck("gofile_token.json", recorded);
    }

    [Fact]
    public void Normalize_recordings_match()
    {
        string?[] versionKeys =
        [
            null,
            "",
            "   ",
            "v1.0",
            " Build 26315250 ",
            "build 12",
            "BUILD 7 ",
            "Build Build 5",
            "  v1.0 ",
        ];

        var cases = versionKeys
            .Select(v => new { input = v, normalized = UpdateDetector.Normalize(v) })
            .ToList();

        RecordOrCheck("normalize.json", cases);
    }

    [Fact]
    public void Is_update_recordings_match()
    {
        (string? Installed, string? Key)[] keys =
        [
            ("v1", "v1"),
            ("v1", "V1"),
            ("v1", "v2"),
            (null, "v1"),
            ("v1", null),
            ("v1", " Build v1 "),
            ("", "x"),
            (null, null),
        ];

        var post = CatalogParser.Parse(Fixture.Read("posts_page1.json"))[0];
        var installed = new InstalledGame(
            post.Id,
            post.Slug,
            post.Name,
            null,
            "/x",
            DateTime.UtcNow,
            true,
            InstallState.Installed,
            null
        );

        var cases = keys.Select(k => new
            {
                installed = k.Installed,
                version_key = k.Key,
                is_update = UpdateDetector.IsUpdate(
                    installed with
                    {
                        InstalledVersion = k.Installed,
                    },
                    post with
                    {
                        Build = k.Key,
                        TitleVersion = null,
                    }
                ),
            })
            .ToList();

        RecordOrCheck("is_update.json", cases);
    }

    [Fact]
    public void Proton_selection_recordings_match()
    {
        string?[] stored =
        [
            null,
            "",
            "GE-Proton",
            "DW-Proton",
            "/opt/proton",
            "ge-proton",
            "Proton-CachyOS",
        ];

        var cases = stored
            .Select(value => new
            {
                stored = value,
                effective = ProtonLocator.Effective(value),
                is_keyword = ProtonLocator.IsKeyword(value),
            })
            .ToList();

        RecordOrCheck("proton_selection.json", cases);
    }

    [Fact]
    public void Archive_marker_recordings_match()
    {
        (long Length, string? Marker)[] cases =
        [
            (100, "100\n2026-11-19T00:00:00Z"),
            (100, "100\r\n2026-11-19T00:00:00Z"),
            (100, "99\n2026-11-19T00:00:00Z"),
            (100, null),
            (0, "0"),
            (100, ""),
            (100, " 100 "),
            (100, "100abc"),
        ];

        var recorded = cases
            .Select(c => new
            {
                length = c.Length,
                marker = c.Marker,
                verified = ArchiveMarker.IsVerified(c.Length, c.Marker),
            })
            .ToList();

        RecordOrCheck("archive_marker.json", recorded);
    }

    [Fact]
    public void Host_capabilities_recordings_match()
    {
        var cases = new List<object>();

        foreach (var file in new[] { "edid_hdr.bin", "edid_nohdr.bin" })
        {
            var bytes = File.ReadAllBytes(Fixture.Path(file));
            cases.Add(
                new
                {
                    kind = "edid",
                    input = Convert.ToHexString(bytes).ToLowerInvariant(),
                    has_hdr = HostCapabilities.EdidHasHdr(bytes),
                    vendors = (object?)null,
                    env_vars = (object?)null,
                    displays = (object?)null,
                }
            );
        }

        string[] vendors = ["0x1002", "0x10DE", "0x8086", "0x9999", " 0x1002 ", "amd"];
        foreach (var vendor in vendors)
        {
            cases.Add(
                new
                {
                    kind = "vendor",
                    input = vendor,
                    has_hdr = (bool?)null,
                    vendors = (object?)HostCapabilities.VendorName(vendor),
                    env_vars = (object?)null,
                    displays = (object?)null,
                }
            );
        }

        string[] scripts =
        [
            "",
            "PROTON_ENABLE_HDR=1 DXVK_HDR=1 WINE_FULLSCREEN_FSR=1",
            "export PROTON_LOG=1\n# not a var: PROTON\nOTHER=1 PROTON_LOG=2",
            "PROTON_NO_ESYNC WINE_FULLSCREEN_FSR_2 PROTON_A_B_C",
        ];
        foreach (var script in scripts)
        {
            cases.Add(
                new
                {
                    kind = "env",
                    input = script,
                    has_hdr = (bool?)null,
                    vendors = (object?)null,
                    env_vars = (object?)
                        HostCapabilities
                            .ProtonEnvVars(script)
                            .OrderBy(v => v, StringComparer.Ordinal)
                            .ToList(),
                    displays = (object?)null,
                }
            );
        }

        var kscreenTexts = new[]
        {
            Fixture.Read("kscreen_outputs.txt"),
            "Output: 1 HDMI-A-2\nHDR: incapable\nGeometry: 1920,1080 0 0",
            "",
        };
        foreach (var text in kscreenTexts)
        {
            cases.Add(
                new
                {
                    kind = "kscreen",
                    input = text,
                    has_hdr = (bool?)null,
                    vendors = (object?)null,
                    env_vars = (object?)null,
                    displays = (object?)
                        HostCapabilities
                            .ParseKScreen(text)
                            .Select(d => new
                            {
                                name = d.Name,
                                hdr = d.Hdr.ToString().ToLowerInvariant(),
                                width = d.Width,
                                height = d.Height,
                                sdr_nits = d.SdrNits,
                                peak_nits = d.PeakNits,
                            })
                            .ToList(),
                }
            );
        }

        RecordOrCheck("host_capabilities.json", cases);
    }

    [Fact]
    public void Host_displays_recordings_match()
    {
        var cases = new List<object>();

        void Display(string kind, string? file, string? text)
        {
            var input = text ?? Fixture.Read(file!);
            var displays =
                kind == "mutter"
                    ? HostCapabilities.ParseMutter(input)
                    : HostCapabilities.ParseWlrOutputs(input);
            cases.Add(
                new
                {
                    kind,
                    html_file = file,
                    html = text,
                    displays = displays
                        .Select(d => new
                        {
                            name = d.Name,
                            hdr = d.Hdr.ToString().ToLowerInvariant(),
                            width = d.Width,
                            height = d.Height,
                            sdr_nits = d.SdrNits,
                            peak_nits = d.PeakNits,
                        })
                        .ToList(),
                }
            );
        }

        Display("mutter", "mutter_current_state.txt", null);
        Display("mutter", null, "");
        Display("wlr", "sway_outputs.json", null);
        Display("wlr", "hyprctl_monitors.json", null);
        Display("wlr", null, "");
        Display("wlr", null, "{\"a\":1}");

        RecordOrCheck("host_displays.json", cases);
    }

    [Fact]
    public void Save_walk_recordings_match()
    {
        string[] names =
        [
            "Save",
            "save",
            "SAVES",
            "SaveGames",
            "UserData",
            "Saves_old",
            "Saved",
            "SaveData",
            "MySave",
            "Saver",
            "savepoint",
            "Profiles",
            "Profile",
            "Data",
            "resources",
            "",
            "_CommonRedist",
            "_Redist",
            "redist",
            "drive_c",
            "Engine",
            "Content",
            "Binaries",
            "Shaders",
            "Win64",
            "Win32",
            "resources",
            "Plugins",
            "SavesDir",
        ];
        int[] depths = [0, 1, 3];

        var cases = names
            .SelectMany(name =>
                depths.Select(depth => new
                {
                    name,
                    depth,
                    is_save = SaveLocator.IsSaveDirName(name),
                    is_skipped = SaveLocator.IsSkippedDir(name, depth),
                })
            )
            .ToList();

        RecordOrCheck("save_walk.json", cases);
    }

    [Fact]
    public void Percent_values_recordings_match()
    {
        string[] chunks =
        [
            "",
            " 45%",
            "100% 101% 7%",
            "no numbers here",
            "150%",
            "0%",
            "99%done 45% 46% 47%",
            "Extracting  3%",
        ];

        var cases = chunks
            .Select(chunk => new { input = chunk, values = ArchiveTool.PercentValues(chunk) })
            .ToList();

        RecordOrCheck("percent_values.json", cases);
    }

    [Fact]
    public void Proton_release_recordings_match()
    {
        var fixture = Fixture.Read("dwproton_release.json");
        var noAsset =
            "{\"tag_name\":\"v1\",\"assets\":[{\"name\":\"other.zip\",\"browser_download_url\":\"https://x/y.zip\"}]}";
        var noTag = "{\"assets\":[]}";

        var cases = new[] { fixture, noAsset, noTag }
            .Select(json =>
            {
                var release = ProtonDownloader.ParseLatest(json);
                return new
                {
                    parsed = release is null
                        ? null
                        : new
                        {
                            tag = release.Tag,
                            archive = release.ArchiveUrl,
                            archive_name = release.ArchiveName,
                            checksum = release.ChecksumUrl,
                        },
                };
            })
            .ToList();

        RecordOrCheck("proton_release.json", cases);
    }

    [Fact]
    public void Proton_checksum_recordings_match()
    {
        var text =
            "abc123  dwproton-11.0-16-x86_64.tar.xz"
            + "\n"
            + "def456 *dwproton-other.tar.xz"
            + "\n"
            + "short"
            + "\n"
            + "fedcba  dwproton-11.0-16-x86_64.sha512sum"
            + "\n";

        string[] files =
        [
            "dwproton-11.0-16-x86_64.tar.xz",
            "dwproton-other.tar.xz",
            "missing-file.tar.xz",
            "",
        ];

        var cases = files
            .Select(file => new { file, hash = ProtonDownloader.ParseChecksum(text, file) })
            .ToList();

        RecordOrCheck("proton_checksum.json", cases);
    }

    [Fact]
    public void Top_games_recordings_match()
    {
        string[] pages =
        [
            "",
            "<div class=\"post-item post-57136 x\"></div><div class=\"post-item post-42\"></div>",
            "<div class=\"post-item post-7\"></div><div class=\"post-item post-7\"></div><div class=\"post-item post-9\"></div>",
            "<div class=\"post-item-post-5\"></div>",
        ];

        var cases = pages
            .Select(html => new { input = html, ids = CatalogParser.ParseTopGames(html) })
            .ToList();

        RecordOrCheck("top_games.json", cases);
    }

    [Fact]
    public void Mirrors_recordings_match()
    {
        string[] bodies =
        [
            "",
            "<a class=\"shortc-button\" href=\"https://fileditch.com/f/x.rar\"></a>",
            "<a class=\"shortc-button\" href=\"https://fileditch.com/f/x.rar\"></a><a class=\"shortc-button\" href=\"https://fileditch.com/f/x.rar\"></a>",
            "<a class=\"shortc-button\" href=\"//bzzhr.co/abc\"></a>",
            "<a class=\"shortc-button\" href=\"https://filecrypt.cc/Container/ABC.html\"></a>",
            "<a class=\"shortc-button\" href=\"https://unknown.example/x\"></a>",
            "<a class='shortc-button' href='https://megadb.net/x?pt=y&amp;z=1'></a>",
            "<a class=\"shortc-button\" href=\"https://gofile.io/d/abc\"></a><a class=\"shortc-button\" href=\"https://1fichier.com/?abc\"></a>",
        ];

        var cases = bodies
            .Select(content => new
            {
                input = content,
                mirrors = CatalogParser
                    .MirrorsFromHtml(content)
                    .Select(m => new { host = m.Host.ToString(), url = m.Url })
                    .ToList(),
            })
            .ToList();

        RecordOrCheck("mirrors.json", cases);
    }

    [Fact]
    public void Game_size_recordings_match()
    {
        string[] bodies =
        [
            "",
            "<p>Game Size: </strong>12.3 GB</p>",
            "Game Size:</strong>  1,5 TB  ",
            "game size: </strong>750 MB",
            "Game Size: 12.3 GB",
            "<p>Game Size:</strong> unknown</p>",
        ];

        var cases = bodies
            .Select(content => new { input = content, size = CatalogParser.GameSizeText(content) })
            .ToList();

        RecordOrCheck("game_size.json", cases);
    }

    [Fact]
    public void Proton_versions_recordings_match()
    {
        string[] names =
        [
            "GE-Proton11-3",
            "GE-Proton9-27",
            "dwproton-11.0-16",
            "dwproton-11.0-9",
            "Proton-CachyOS-11.0",
            "no digits",
        ];

        var keys = names
            .Select(name => new { name, key = ProtonLocator.VersionKey(name) })
            .ToList();

        var comparisons = names
            .SelectMany(a =>
                names.Select(b => new
                {
                    a,
                    b,
                    result = Math.Sign(
                        ProtonLocator.CompareVersions(
                            ProtonLocator.VersionKey(a),
                            ProtonLocator.VersionKey(b)
                        )
                    ),
                })
            )
            .ToList();

        RecordOrCheck(
            "proton_versions.json",
            new[]
            {
                new
                {
                    keys,
                    comparisons,
                    newest = ProtonLocator.NewestBuildName(names),
                },
            }
        );
    }

    [Fact]
    public void Proton_display_name_recordings_match()
    {
        var q = (char)34;
        string[] vdfs =
        [
            "nothing here",
            q + "display_name" + q + " " + q + "GE-Proton11-12" + q,
            q + "display_name" + q + "   " + q + "Proton-CachyOS-11.0" + q,
            q + "display_name" + q,
        ];

        var cases = vdfs.Select(vdf => new { input = vdf, name = ProtonLocator.DisplayName(vdf) })
            .ToList();

        RecordOrCheck("proton_display_name.json", cases);
    }

    [Fact]
    public void Installer_safe_name_recordings_match()
    {
        string[] inputs = ["a b", "a/b", "", "   ", "ok-name", "Çok Özel", "a" + (char)0 + "b"];

        var cases = inputs
            .Select(input => new { input, safe = Installer.SafeName(input) })
            .ToList();

        RecordOrCheck("installer_safe_name.json", cases);
    }

    [Fact]
    public void Autostart_entry_recordings_match()
    {
        string[] paths =
        [
            "/usr/bin/steamrip-desktop",
            "/home/emre/Apps/SteamRIP Desktop.appimage",
            "",
        ];

        var cases = paths
            .Select(path => new { path, entry = Autostart.DesktopEntry(path) })
            .ToList();

        RecordOrCheck("autostart_entry.json", cases);
    }

    [Fact]
    public void Proton_label_recordings_match()
    {
        (string? Stored, string? Resolved)[] cases =
        [
            (null, null),
            ("", null),
            ("GE-Proton", "/x/GE-Proton11-12"),
            ("DW-Proton", null),
            ("/opt/my-proton/", null),
            ("/opt/my-proton", null),
        ];

        var recorded = cases
            .Select(c =>
            {
                var stored = c.Stored;
                var resolved = c.Resolved;
                return new
                {
                    stored,
                    resolved,
                    label = PrefixManager.ProtonLabel(stored, _ => resolved),
                    build = PrefixManager.ResolveBuild(
                        stored,
                        _ => resolved,
                        path =>
                            resolved is not null
                            && path.StartsWith(resolved, StringComparison.Ordinal)
                    ),
                };
            })
            .ToList();

        RecordOrCheck("proton_label.json", recorded);
    }

    [Fact]
    public void Collapse_recordings_match()
    {
        (string[] Dirs, string[] Files)[] cases =
        [
            ([], []),
            (["/s/Game"], []),
            (["/s/Game/", "_CommonRedist"], []),
            (["Game", "_CommonRedist", "_Redist"], ["readme.txt"]),
            ([], ["file.bin"]),
            (["A", "B"], []),
            (["_CommonRedist"], []),
            (["__MACOSX"], []),
            (["Game"], []),
            (["game"], ["setup.exe"]),
            (["/s/A", "/s/B", "/s/_Setup"], []),
        ];

        var recorded = cases
            .Select(c => new
            {
                dirs = c.Dirs,
                files = c.Files,
                target = Installer.CollapseTarget(c.Dirs, c.Files),
            })
            .ToList();

        RecordOrCheck("collapse.json", recorded);
    }

    [Fact]
    public void Categories_recordings_match()
    {
        var q = (char)34;
        string[] payloads =
        [
            Fixture.Read("categories.json"),
            "[]",
            "[{" + q + "id" + q + ":1," + q + "name" + q + ":" + q + "A &amp; B" + q + "}]",
            "[{"
                + q
                + "id"
                + q
                + ":2,"
                + q
                + "name"
                + q
                + ":"
                + q
                + "X"
                + q
                + ","
                + q
                + "count"
                + q
                + ":0}]",
        ];

        var cases = payloads
            .Select(json => new
            {
                input = json.Length < 200 ? json : "<fixture>",
                categories = CatalogParser
                    .ParseCategories(json)
                    .Select(c => new
                    {
                        id = c.Id,
                        name = c.Name,
                        count = c.Count,
                    })
                    .ToList(),
            })
            .ToList();

        RecordOrCheck("categories.json", cases);
    }

    [Fact]
    public void Torrent_and_engine_recordings_match()
    {
        (string? FileName, string Url)[] links =
        [
            ("game.rar", "https://x/game.rar"),
            ("game.torrent", "https://x/game.rar"),
            (null, "https://x/game.torrent"),
            (null, "https://x/GAME.TORRENT"),
            ("game.rar", "https://x/game.rar?t=1"),
        ];

        var torrents = links
            .Select(l => new
            {
                file_name = l.FileName,
                url = l.Url,
                is_torrent = Downloader.IsTorrent(
                    new ResolvedDownload(
                        Hoster.FileDitch,
                        l.Url,
                        new Dictionary<string, string>(),
                        l.FileName
                    )
                ),
            })
            .ToList();

        (bool Available, bool Prefer)[] engines =
        [
            (true, true),
            (true, false),
            (false, true),
            (false, false),
        ];

        var engineNames = engines
            .Select(e => new
            {
                available = e.Available,
                prefer = e.Prefer,
                name = Downloader.EngineNameFor(e.Available, e.Prefer),
            })
            .ToList();

        RecordOrCheck("torrent_engine.json", new[] { new { torrents, engineNames } });
    }

    [Fact]
    public void Redist_selection_recordings_match()
    {
        string[][] lists =
        [
            [],
            ["readme.txt"],
            ["/r/VisualCppRedist_AIO_x86_x64.exe", "/r/vcredist_x64.exe"],
            ["/r/VisualCppRedist_AIO_x86_x64.exe", "/r/VisualCppRedist_AIO_b.exe"],
            ["/r/vcredist_x64.exe", "/r/vcredist_x86.exe"],
            ["/r/vcredist_x86.exe", "/r/vcredist_x64.exe"],
            ["/r/vcredist_2019_x64.exe", "/r/vcredist_x64.exe"],
            ["/r/vc_redist.x64.exe", "/r/other.exe"],
            ["/r/vcredist_x64.EXE"],
            ["/r/VC_REDIST_X86.EXE", "/r/vcredist_x64.exe"],
        ];

        var cases = lists
            .Select(files => new
            {
                files,
                selected = RedistInstaller
                    .SelectVcInstallers(files)
                    .Select(v => new { file = v.FilePath, args = v.Args })
                    .ToList(),
            })
            .ToList();

        RecordOrCheck("redist_selection.json", cases);
    }

    [Fact]
    public void Timestamp_recordings_match()
    {
        string?[] texts =
        [
            null,
            "",
            "   ",
            "not a date",
            "2026-11-19T12:34:56",
            "2026-11-19T12:34:56Z",
            "2026-11-19T12:34:56+03:00",
            "2026-11-19T12:34:56-05:00",
            "2026-11-19T12:34:56.789Z",
            "2026-11-19",
            "2026-11-19 12:34:56",
            "11/19/2026 12:34:56",
            "1970-01-01T00:00:00Z",
            "2026-13-45T99:99:99",
        ];

        var cases = texts
            .Select(text => new { input = text, unix = CatalogParser.ParseUtcText(text) })
            .ToList();

        RecordOrCheck("timestamps.json", cases);
    }

    [Fact]
    public void Prefix_upgrade_recordings_match()
    {
        (string? Previous, string? Current)[] pairs =
        [
            (null, null),
            (null, "/x/GE-Proton11-12"),
            ("/x/GE-Proton11-12", null),
            ("/x/GE-Proton11-12", "/x/GE-Proton11-12"),
            ("/x/GE-Proton11-12/", "/x/GE-Proton11-12"),
            ("/x/GE-Proton11-12", "/y/GE-Proton11-12"),
            ("/x/GE-Proton11-12", "/x/ge-proton11-12"),
            ("/x/GE-Proton11-12", "/x/GE-Proton11-13"),
            ("", "/x/GE-Proton11-12"),
        ];

        var upgrades = pairs
            .Select(p => new
            {
                previous = p.Previous,
                current = p.Current,
                needs_upgrade = PrefixManager.NeedsUpgrade(p.Previous, p.Current),
            })
            .ToList();

        string[] dirs = ["/prefixes/game", "/prefixes/game/", "/prefixes/game.bak-20260101-000000"];

        var aside = dirs.Select(dir => new
            {
                dir,
                path = PrefixManager.AsidePath(
                    dir,
                    new DateTime(2026, 11, 19, 20, 30, 45, DateTimeKind.Utc)
                ),
            })
            .ToList();

        var payload = new[] { new { upgrades, aside } };
        RecordOrCheck("prefix_upgrade.json", payload);
    }

    [Fact]
    public void Hoster_order_recordings_match()
    {
        Hoster[][] configured =
        [
            [],
            [Hoster.GoFile],
            [Hoster.BuzzHeavier, Hoster.FileDitch],
            [Hoster.FileDitch, Hoster.GoFile, Hoster.MegaDb, Hoster.BuzzHeavier],
        ];

        var cases = configured
            .Select(order => new
            {
                configured = order.Select(h => h.ToString()).ToList(),
                effective = ResolverChain.EffectiveOrder(order).Select(h => h.ToString()).ToList(),
            })
            .ToList();

        RecordOrCheck("hoster_order.json", cases);
    }

    [Fact]
    public void Settings_recordings_match()
    {
        (bool Windows, string AppData, string? Xdg, string Home)[] dirs =
        [
            (false, "/appdata", null, "/home/emre"),
            (false, "/appdata", "", "/home/emre"),
            (false, "/appdata", "/xdg", "/home/emre"),
            (true, "C:/Users/x/AppData/Roaming", "/xdg", "C:/Users/x"),
            (true, "C:/Users/x/AppData/Roaming", null, "C:/Users/x"),
        ];

        var defaultDirs = dirs.Select(d => new
            {
                windows = d.Windows,
                app_data = d.AppData,
                xdg = d.Xdg,
                home = d.Home,
                dir = Settings.DefaultDirFor(d.Windows, d.AppData, d.Xdg, d.Home),
            })
            .ToList();

        string[][] nameLists =
        [
            [],
            ["FileDitch", "GoFile"],
            ["fileditch", "BUZZHEAVIER"],
            ["Nope", "MegaDb"],
            ["Unknown"],
        ];

        var hosters = nameLists
            .Select(names => new
            {
                names,
                parsed = Settings.HostersFromNames(names).Select(h => h.ToString()).ToList(),
            })
            .ToList();

        string[] orderTexts =
        [
            "",
            "FileDitch, GoFile",
            " FileDitch ,, GoFile , ",
            "FileDitch,GoFile,MegaDb,BuzzHeavier",
        ];

        var orders = orderTexts
            .Select(text => new { input = text, names = Settings.ParseHosterOrder(text) })
            .ToList();

        var payload = new[]
        {
            new
            {
                defaultDirs,
                hosters,
                orders,
            },
        };
        RecordOrCheck("settings_rules.json", payload);
    }

    [Fact]
    public void Vulkan_and_screen_recordings_match()
    {
        string[] vulkanTexts =
        [
            "",
            "   ",
            "GPU0:\n\tvendorID = 0x1002\n\tdriverName = radv\n\tdriverInfo = Mesa 26\n\tdriverVersion = 26.2.6\n\tdeviceType = PHYSICAL_DEVICE_TYPE_DISCRETE_GPU",
            "GPU0:\n\tvendorID = 0x8086\n\tdeviceType = PHYSICAL_DEVICE_TYPE_INTEGRATED_GPU\nGPU1:\n\tvendorID = 0x1002\n\tdriverName = radv\n\tdeviceType = PHYSICAL_DEVICE_TYPE_DISCRETE_GPU",
            "GPU0:\n\tdeviceType = PHYSICAL_DEVICE_TYPE_CPU",
            "no fields at all",
        ];

        var vulkan = vulkanTexts
            .Select(text =>
            {
                var info = HostCapabilities.ParseVulkanInfo(text);
                return new
                {
                    input = text,
                    gpu = info is null
                        ? null
                        : new
                        {
                            vendor = info.Vendor,
                            driver = info.Driver,
                            driver_info = info.DriverInfo,
                            driver_version = info.DriverVersion,
                        },
                };
            })
            .ToList();

        string[][] modeSets =
        [
            [],
            [""],
            ["1920x1080\n1280x720"],
            ["1920x1080", "3440x1440"],
            ["3440x1440", "1920x1080"],
            ["3840x2160", "3440x1440", "1920x1080"],
            ["nonsense"],
        ];

        var screens = modeSets
            .Select(modes => new
            {
                modes,
                largest = HostCapabilities.LargestScreenOf(modes) is { } s
                    ? new { width = s.Width, height = s.Height }
                    : null,
            })
            .ToList();

        var payload = new[] { new { vulkan, screens } };
        RecordOrCheck("vulkan_screen.json", payload);
    }

    [Fact]
    public void Emulator_and_gofile_recordings_match()
    {
        string[][] userSets =
        [
            [],
            ["/p/drive_c/users/steamuser"],
            ["/p/drive_c/users/steamuser", "/p/drive_c/users/steamuser"],
            ["/p/drive_c/users/steamuser", "/p/drive_c/users/Public"],
            ["C:/Users/x"],
        ];

        var paths = userSets
            .Select(users => new { users, paths = SaveLocator.EmulatorSavePaths(users, 2483190) })
            .ToList();

        string[] links =
        [
            "https://gofile.io/d/AbC123",
            "https://gofile.io/d/abc-123",
            "https://gofile.io/d/",
            "https://example.com/x",
            "",
        ];

        var gofile = links
            .Select(link => new { link, id = GoFileResolver.ContentIdOf(link) })
            .ToList();

        var payload = new[] { new { paths, gofile } };
        RecordOrCheck("emulator_gofile.json", payload);
    }
}
