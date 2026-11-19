using SteamRip.Core.Catalog;
using SteamRip.Core.Hosters;

namespace SteamRip.Core.Tests;

/// <summary>
/// Recordings for the mirror walk: which host wins, when a torrent is kept as a fallback, and the
/// error text shown when nothing works. Fake resolvers keep it offline and deterministic.
/// </summary>
public class GoldenResolverChainTests
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

    private sealed class FakeResolver(Hoster host, Func<string, ResolvedDownload> resolve)
        : IHostResolver
    {
        public Hoster Host => host;

        public Task<ResolvedDownload> ResolveAsync(
            string shareUrl,
            CancellationToken ct = default
        ) => Task.FromResult(resolve(shareUrl));
    }

    private static ResolvedDownload Direct(Hoster host, string url, string? fileName = null) =>
        new(host, url, new Dictionary<string, string>(), fileName);

    [Fact]
    public async Task Resolver_chain_recordings_match()
    {
        var q = (char)34;
        var cases = new List<object>();

        void Case(
            string label,
            Hoster[] order,
            (Hoster Host, string Url)[] mirrors,
            bool allowTorrents,
            Dictionary<string, (string Kind, string? Message, bool Prefixed)> outcomes
        )
        {
            var resolvers = order
                .Select(host =>
                    (IHostResolver)
                        new FakeResolver(
                            host,
                            url =>
                            {
                                if (!outcomes.TryGetValue(url, out var outcome))
                                    throw new HostResolveException(host, "no fake outcome");
                                if (outcome.Kind == "fail")
                                    throw outcome.Prefixed
                                        ? new HostResolveException(
                                            host,
                                            outcome.Message ?? "failed"
                                        )
                                        : new InvalidOperationException(
                                            outcome.Message ?? "failed"
                                        );
                                return Direct(
                                    host,
                                    url,
                                    outcome.Kind == "torrent" ? "game.torrent" : "game.rar"
                                );
                            }
                        )
                )
                .ToArray();

            var chain = new ResolverChain(
                resolvers,
                () => order,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ResolverChain>.Instance,
                () => allowTorrents
            );
            var mirrorList = mirrors.Select(m => new Mirror(m.Host, m.Url)).ToList();
#pragma warning disable CA2007 // the recorder is sync-over-async on purpose
            var (result, errors) = chain.ResolveAsync(mirrorList).GetAwaiter().GetResult();
#pragma warning restore CA2007
            cases.Add(
                new
                {
                    name = label,
                    order = order.Select(h => h.ToString()).ToList(),
                    mirrors = mirrors
                        .Select(m => new { host = m.Host.ToString(), url = m.Url })
                        .ToList(),
                    allow_torrents = allowTorrents,
                    outcomes = outcomes
                        .Select(kv => new
                        {
                            url = kv.Key,
                            kind = kv.Value.Kind,
                            message = kv.Value.Message,
                            prefixed = kv.Value.Prefixed,
                        })
                        .ToList(),
                    chosen = result is null
                        ? null
                        : new
                        {
                            host = result.Host.ToString(),
                            url = result.Url,
                            is_torrent = result.FileName?.EndsWith(
                                ".torrent",
                                StringComparison.OrdinalIgnoreCase
                            ) == true,
                        },
                    errors,
                }
            );
        }

        Case(
            "direct first",
            [Hoster.FileDitch, Hoster.GoFile],
            [(Hoster.FileDitch, "https://fd/a"), (Hoster.GoFile, "https://gf/b")],
            false,
            new()
            {
                ["https://fd/a"] = ("direct", null, false),
                ["https://gf/b"] = ("fail", "down", false),
            }
        );

        Case(
            "second host wins after a plain failure",
            [Hoster.FileDitch, Hoster.GoFile],
            [(Hoster.FileDitch, "https://fd/a"), (Hoster.GoFile, "https://gf/b")],
            false,
            new()
            {
                ["https://fd/a"] = ("fail", "boom", false),
                ["https://gf/b"] = ("direct", null, false),
            }
        );

        Case(
            "host exception keeps its own prefix",
            [Hoster.FileDitch, Hoster.GoFile],
            [(Hoster.FileDitch, "https://fd/a"), (Hoster.GoFile, "https://gf/b")],
            false,
            new()
            {
                ["https://fd/a"] = ("fail", "link expired", true),
                ["https://gf/b"] = ("direct", null, false),
            }
        );

        Case(
            "torrent only, torrents off",
            [Hoster.FileDitch, Hoster.MegaDb],
            [(Hoster.MegaDb, "https://md/a")],
            false,
            new() { ["https://md/a"] = ("torrent", null, false) }
        );

        Case(
            "torrent only, torrents on",
            [Hoster.FileDitch, Hoster.MegaDb],
            [(Hoster.MegaDb, "https://md/a")],
            true,
            new() { ["https://md/a"] = ("torrent", null, false) }
        );

        Case(
            "direct beats an earlier torrent",
            [Hoster.MegaDb, Hoster.FileDitch],
            [(Hoster.MegaDb, "https://md/a"), (Hoster.FileDitch, "https://fd/b")],
            true,
            new()
            {
                ["https://md/a"] = ("torrent", null, false),
                ["https://fd/b"] = ("direct", null, false),
            }
        );

        Case(
            "all fail",
            [Hoster.FileDitch, Hoster.GoFile],
            [(Hoster.FileDitch, "https://fd/a"), (Hoster.GoFile, "https://gf/b")],
            false,
            new()
            {
                ["https://fd/a"] = ("fail", "one", false),
                ["https://gf/b"] = ("fail", "two", false),
            }
        );

        Case(
            "no supported mirror",
            [Hoster.FileDitch],
            [(Hoster.MegaDb, "https://md/a"), (Hoster.MegaDb, "https://md/b")],
            false,
            new()
        );

        var dir = FixtureDirectory();
        var path = Path.Combine(dir, "resolver_chain.json");
        var payload =
            System.Text.Json.JsonSerializer.Serialize(new { cases }, Json) + Environment.NewLine;
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SR_GOLDEN_DIR")))
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, payload);
            return;
        }

        Assert.True(File.Exists(path), q + "missing recording: run make golden" + q);
        Assert.Equal(payload, File.ReadAllText(path));
    }
}
