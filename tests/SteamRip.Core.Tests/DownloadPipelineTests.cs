using SteamRip.Core.Catalog;
using SteamRip.Core.Downloads;
using SteamRip.Core.Hosters;
using SteamRip.Core.Install;
using SteamRip.Core.Updates;

namespace SteamRip.Core.Tests;

/// <summary>
/// The parts of the download pipeline that decide "is this file the archive?" and "may I reuse it?" — the two
/// questions that made installs retry the same bad bytes forever.
/// </summary>
public sealed class DownloadPipelineTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sr-dl-" + Guid.NewGuid().ToString("N")[..8]
    );

    public DownloadPipelineTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // temp dir, the OS will get it
        }
        catch (UnauthorizedAccessException)
        {
            // same
        }
    }

    private string Write(string name, byte[] head, int size = 16)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[Math.Max(size, head.Length)];
        head.CopyTo(bytes, 0);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Rar5 => [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];
    private static byte[] SevenZip => [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
    private static byte[] Zip => [0x50, 0x4B, 0x03, 0x04];

    [Fact]
    public void SniffKind_reads_the_magic_not_the_extension()
    {
        Assert.Equal("rar", ArchiveTool.SniffKind(Write("game.rar", Rar5)));
        Assert.Equal("7z", ArchiveTool.SniffKind(Write("game.bin", SevenZip)));
        Assert.Equal("zip", ArchiveTool.SniffKind(Write("game.rar", Zip))); // mislabelled
        Assert.Equal(
            "torrent",
            ArchiveTool.SniffKind(Write("archive.rar", "d8:announce"u8.ToArray()))
        );
        Assert.Equal(
            "html",
            ArchiveTool.SniffKind(Write("archive.rar", "<!DOCTYPE html>"u8.ToArray()))
        );
        Assert.Null(ArchiveTool.SniffKind(Write("junk.bin", [0x00, 0x01, 0x02])));
    }

    [Fact]
    public void IsTorrent_recognises_a_torrent_even_when_the_name_lies()
    {
        var torrent = Write("Red.Dead.torrent", "d8:announce"u8.ToArray());
        var lying = Write("Red.Dead.rar", "d8:announce"u8.ToArray());
        Assert.True(ArchiveTool.IsTorrent(torrent));
        Assert.True(ArchiveTool.IsTorrent(lying));
        Assert.False(ArchiveTool.IsTorrent(Write("ok.rar", Rar5)));
    }

    [Fact]
    public void LooksLikeArchive_accepts_archives_only()
    {
        Assert.True(ArchiveTool.LooksLikeArchive(Write("a.rar", Rar5)));
        Assert.False(ArchiveTool.LooksLikeArchive(Write("b.torrent", "d8:announce"u8.ToArray())));
        Assert.False(ArchiveTool.LooksLikeArchive(Write("c.html", "<html>"u8.ToArray())));
    }

    [Fact]
    public void FirstPart_finds_volume_one_of_a_split_set()
    {
        var part1 = Write("Game.part01.rar", Rar5);
        Write("Game.part02.rar", Rar5);
        Assert.Equal(part1, ArchiveTool.FirstPart(Path.Combine(_root, "Game.part02.rar")));
        Assert.Equal(part1, ArchiveTool.FirstPart(part1));

        var rar = Write("Old.rar", Rar5);
        Write("Old.r00", Rar5);
        Assert.Equal(rar, ArchiveTool.FirstPart(Path.Combine(_root, "Old.r00")));

        var seven = Write("Big.7z.001", SevenZip);
        Write("Big.7z.002", SevenZip);
        Assert.Equal(seven, ArchiveTool.FirstPart(Path.Combine(_root, "Big.7z.002")));

        var single = Write("Single.rar", Rar5);
        Assert.Equal(single, ArchiveTool.FirstPart(single));
    }

    [Fact]
    public void Marker_only_trusts_a_file_at_the_size_it_was_marked()
    {
        var archive = Write("game.rar", Rar5, 4096);
        Assert.False(ArchiveMarker.IsVerified(archive));

        ArchiveMarker.Mark(archive);
        Assert.True(ArchiveMarker.IsVerified(archive));

        using (var fs = File.Open(archive, FileMode.Truncate))
            fs.SetLength(1024);
        Assert.False(ArchiveMarker.IsVerified(archive));

        ArchiveMarker.Clear(archive);
        Assert.False(ArchiveMarker.IsVerified(archive));
    }

    [Fact]
    public void PickArchive_prefers_the_biggest_archive_and_skips_the_rest()
    {
        var small = Write("small.rar", Rar5, 512);
        var big = Write("big.rar", Rar5, 64 * 1024);
        var torrent = Write("x.torrent", "d8:announce"u8.ToArray());
        var html = Write("index.html", "<html>"u8.ToArray());

        Assert.Equal(big, UpdateService.PickArchive([torrent, html, small, big]));
        Assert.Null(UpdateService.PickArchive([torrent, html]));
    }

    [Fact]
    public void IsTorrent_is_decided_from_the_resolved_name_or_url()
    {
        static ResolvedDownload R(string url, string? name) =>
            new(Hoster.MegaDb, url, new Dictionary<string, string>(), name);

        Assert.True(Downloader.IsTorrent(R("https://x/file?id=1", "Red.Dead.torrent")));
        Assert.True(Downloader.IsTorrent(R("https://x/Red.Dead.torrent", null)));
        Assert.False(Downloader.IsTorrent(R("https://x/Red.Dead.rar", "Red.Dead.rar")));
    }

    [Fact]
    public void Internal_engine_never_claims_aria2()
    {
        var downloader = new Downloader(new HttpClient(), aria2: null);
        Assert.Equal("http", downloader.EngineName);
    }
}
