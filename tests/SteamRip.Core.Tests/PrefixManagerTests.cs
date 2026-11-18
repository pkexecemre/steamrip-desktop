using SteamRip.Core.Install;
using SteamRip.Core.Launch;

namespace SteamRip.Core.Tests;

/// <summary>Prefix discovery, migration, health, leases and reset. Everything runs in a temp dir, nothing touches a
/// real prefix. See <see cref="PrefixManager"/>.</summary>
public sealed class PrefixManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sr-prefix-" + Guid.NewGuid().ToString("N")
    );

    public PrefixManagerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // a locked file on a slow filesystem; the temp dir gets cleaned by the OS
        }
        catch (UnauthorizedAccessException)
        {
            // same
        }
    }

    private string GameDir(string slug)
    {
        var dir = Path.Combine(_root, "library", slug);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WritePrefix(string dir)
    {
        Directory.CreateDirectory(Path.Combine(dir, "drive_c", "windows"));
        File.WriteAllText(Path.Combine(dir, "system.reg"), "WINE REGISTRY");
        File.WriteAllText(Path.Combine(dir, "user.reg"), "USER REGISTRY");
    }

    private string PrefixRoot => Path.Combine(_root, "library", ".prefixes");

    [Fact]
    public void Prepare_moves_a_legacy_prefix_out_of_the_game_folder()
    {
        var game = GameDir("hades");
        WritePrefix(Path.Combine(game, "prefix"));

        var prefix = PrefixManager.Prepare(game, PrefixRoot, "hades");

        Assert.Equal(Path.Combine(PrefixRoot, "hades"), prefix);
        Assert.True(File.Exists(Path.Combine(prefix, "system.reg")));
        Assert.False(Directory.Exists(Path.Combine(game, "prefix")));
    }

    [Fact]
    public void Prepare_leaves_a_folder_that_is_not_a_prefix_alone()
    {
        var game = GameDir("hades");
        var notAPrefix = Path.Combine(game, "prefix");
        Directory.CreateDirectory(notAPrefix);
        File.WriteAllText(Path.Combine(notAPrefix, "readme.txt"), "not wine");

        var prefix = PrefixManager.Prepare(game, PrefixRoot, "hades");

        Assert.True(Directory.Exists(notAPrefix));
        Assert.True(Directory.Exists(prefix));
        Assert.Empty(Directory.GetFiles(prefix));
    }

    [Fact]
    public void Prepare_is_idempotent_and_keeps_an_existing_prefix()
    {
        var game = GameDir("hades");
        var target = Path.Combine(PrefixRoot, "hades");
        WritePrefix(target);
        var before = File.GetLastWriteTimeUtc(Path.Combine(target, "system.reg"));

        var prefix = PrefixManager.Prepare(game, PrefixRoot, "hades");

        Assert.Equal(target, prefix);
        Assert.Equal(before, File.GetLastWriteTimeUtc(Path.Combine(target, "system.reg")));
    }

    [Fact]
    public void SafeName_strips_separators_and_keeps_dots_inside_the_name()
    {
        Assert.Equal("Half-Life-2", PrefixManager.SafeName("Half-Life 2"));
        Assert.Equal("a-b", PrefixManager.SafeName("a/b"));
        Assert.Equal("game", PrefixManager.SafeName("../"));
        Assert.Equal("hidden", PrefixManager.SafeName("..hidden"));
    }

    [Fact]
    public void Record_round_trips_through_the_sidecar()
    {
        var dir = Path.Combine(_root, "rec");
        var record = new PrefixRecord
        {
            CreatedUtc = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc),
            LastUsedUtc = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc),
            Launches = 7,
            ProtonPath = "GE-Proton",
            ProtonBuild = "/x/GE-Proton11-3",
            GameId = "umu-1091500",
        };
        PrefixManager.Write(dir, record);

        var read = PrefixManager.Read(dir);

        Assert.NotNull(read);
        Assert.Equal(7, read.Launches);
        Assert.Equal("GE-Proton", read.ProtonPath);
        Assert.Equal("/x/GE-Proton11-3", read.ProtonBuild);
        Assert.Equal(record.CreatedUtc, read.CreatedUtc);
    }

    [Fact]
    public void A_corrupt_record_reads_as_null_instead_of_throwing()
    {
        var dir = Path.Combine(_root, "rec");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, PrefixManager.SidecarName), "{ not json");

        Assert.Null(PrefixManager.Read(dir));
    }

    [Fact]
    public void Check_classifies_missing_fresh_ready_and_damaged()
    {
        var dir = Path.Combine(_root, "health");
        Assert.Equal(PrefixHealth.Missing, PrefixManager.Check(dir, null));

        Directory.CreateDirectory(dir);
        Assert.Equal(PrefixHealth.Fresh, PrefixManager.Check(dir, null));

        WritePrefix(dir);
        Assert.Equal(
            PrefixHealth.Ready,
            PrefixManager.Check(dir, new PrefixRecord { Launches = 1 })
        );

        File.Delete(Path.Combine(dir, "system.reg"));
        Assert.Equal(
            PrefixHealth.Damaged,
            PrefixManager.Check(dir, new PrefixRecord { Launches = 3 })
        );
    }

    [Fact]
    public void Reset_moves_the_prefix_aside_instead_of_deleting_it()
    {
        var dir = Path.Combine(_root, "prefixes", "hades");
        WritePrefix(dir);

        var aside = PrefixManager.Reset(dir) ?? throw new InvalidOperationException("no backup");

        Assert.False(Directory.Exists(dir));
        Assert.True(File.Exists(Path.Combine(aside, "system.reg")));
        Assert.StartsWith(dir + ".bak-", aside, StringComparison.Ordinal);
    }

    [Fact]
    public void Reset_of_a_missing_prefix_is_a_no_op()
    {
        Assert.Null(PrefixManager.Reset(Path.Combine(_root, "nope")));
    }

    [Fact]
    public void Measure_counts_files_and_bytes()
    {
        var dir = Path.Combine(_root, "size");
        WritePrefix(dir);

        var (bytes, files) = PrefixManager.Measure(dir);

        Assert.Equal(2, files);
        Assert.True(bytes >= "WINE REGISTRY".Length * 2);
        Assert.Equal("0 B", PrefixManager.FormatSize(0));
        Assert.Equal("1 KB", PrefixManager.FormatSize(1024));
    }

    [Fact]
    public void Measure_does_not_follow_links_out_of_the_prefix()
    {
        // Wine puts links to the host in dosdevices (z: -> /); following them reported terabytes for a small prefix.
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "big.bin"), new byte[1 << 20]);

        var prefix = Path.Combine(_root, "measured");
        Directory.CreateDirectory(Path.Combine(prefix, "dosdevices"));
        File.WriteAllBytes(Path.Combine(prefix, "system.reg"), new byte[1024]);
        Directory.CreateSymbolicLink(Path.Combine(prefix, "dosdevices", "z:"), outside);
        File.CreateSymbolicLink(
            Path.Combine(prefix, "link-to-big"),
            Path.Combine(outside, "big.bin")
        );

        var (bytes, files) = PrefixManager.Measure(prefix);

        Assert.Equal(1024, bytes);
        Assert.Equal(2, files);
    }

    [Fact]
    public void Lease_refuses_a_second_holder_and_releases_cleanly()
    {
        var dir = Path.Combine(_root, "lease");
        Directory.CreateDirectory(dir);

        var first = PrefixLease.Acquire(dir);
        var blocked = Assert.Throws<LaunchException>(() => PrefixLease.Acquire(dir));
        Assert.Contains("in use", blocked.Message, StringComparison.OrdinalIgnoreCase);
        first.Dispose();

        var again = PrefixLease.Acquire(dir);
        Assert.Equal(dir, again.Dir);
        again.Dispose();
        // the lock file stays behind on purpose: unlinking it would let two processes lock different inodes
        Assert.True(File.Exists(Path.Combine(dir, PrefixManager.LockName)));
    }

    [Fact]
    public void GameId_prefers_the_steam_app_id_then_the_slug()
    {
        Assert.Equal("umu-1091500", PrefixManager.GameId(1091500, "hades"));
        Assert.Equal("umu-steamrip-hollow-knight", PrefixManager.GameId(null, "Hollow Knight"));
        Assert.Equal("umu-default", PrefixManager.GameId(null, null));
    }

    [Fact]
    public void ProtonLabel_and_ResolveBuild_read_a_build_directory()
    {
        var build = Path.Combine(_root, "protons", "GE-Proton11-3");
        Directory.CreateDirectory(Path.Combine(build, "files", "bin"));
        File.WriteAllText(Path.Combine(build, "proton"), "#!/bin/sh\n");

        Assert.Equal(build, PrefixManager.ResolveBuild(build));
        Assert.Equal("GE-Proton11-3", PrefixManager.ProtonLabel(build));
        Assert.Equal("umu default", PrefixManager.ProtonLabel(null));
        Assert.Null(PrefixManager.ResolveBuild(Path.Combine(_root, "missing")));
    }

    [Fact]
    public void Emulator_saves_are_found_in_a_prefix_outside_the_game_folder()
    {
        var game = GameDir("hades");
        var prefix = Path.Combine(PrefixRoot, "hades");
        WritePrefix(prefix);
        var goldberg = Path.Combine(
            prefix,
            "drive_c",
            "users",
            "steamuser",
            "AppData",
            "Roaming",
            "Goldberg SteamEmu Saves",
            "1145360"
        );
        Directory.CreateDirectory(goldberg);

        Assert.Empty(SaveLocator.FindEmulatorSaves(1145360, game));
        Assert.Equal([goldberg], SaveLocator.FindEmulatorSaves(1145360, game, prefix));
    }

    [Fact]
    public void NeedsUpgrade_only_fires_when_the_build_actually_changes()
    {
        Assert.True(PrefixManager.NeedsUpgrade("/p/GE-Proton11-3", "/p/GE-Proton11-4"));
        Assert.True(PrefixManager.NeedsUpgrade("/p/GE-Proton11-3/", "/other/GE-Proton11-4"));
        Assert.False(PrefixManager.NeedsUpgrade("/p/GE-Proton11-3", "/p/GE-Proton11-3/"));
        Assert.False(PrefixManager.NeedsUpgrade(null, "/p/GE-Proton11-3"));
        Assert.False(PrefixManager.NeedsUpgrade("/p/GE-Proton11-3", null));
    }

    [Fact]
    public void Root_falls_back_to_the_library_and_honours_an_override()
    {
        Assert.Equal(Path.Combine("/lib", ".prefixes"), PrefixManager.Root("/lib", null));
        Assert.Equal("/mnt/games/prefixes", PrefixManager.Root("/lib", " /mnt/games/prefixes "));
    }
}
