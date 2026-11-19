# Decision log

Short ADR-style entries. Newest at the bottom. Each records the context, the choice, what it locks in, and what would make us revisit it.

## D1 — Scope: Linux first, Windows must work, macOS later (Nov 6 2026)

Owner's dev box is CachyOS/KDE/Wayland; a Windows machine exists. Platform shims (autostart, notifications, archive tool, secrets, launcher) are implemented for both behind the smallest interfaces; nothing macOS-specific exists. Revisit when a Mac appears.

## D2 — Single process

Avalonia app + `TrayIcon` + background tasks in one process. A worker/UI split would have doubled autostart, IPC and lifetime handling for no stated need (downloads do not have to outlive the UI; no headless target). Revisit if a NAS/headless worker is wanted.

## D3 — Catalog via WP REST API, not HTML scraping

The site exposes `/wp-json/wp/v2/posts` with `acf.tagmetafield` (Steam build) and `steam_app_id`; posts update in place. HTML parsing is limited to the `shortc-button` anchors and the Game Size line inside `content.rendered`. Sitemap `lastmod` and the A–Z list are documented fallbacks. Politeness: one request in flight, 1.5 s between pages, honour `Retry-After` (429 appears after ~40 pages), incremental checks on the first pages only.

## D4 — Hosters and order: FileDitch, GoFile, MegaDB, BuzzHeavier; no 1fichier/FileCrypt; debrid parked

Coverage from the 100-post sample made FileDitch+GoFile+MegaDB sufficient; BuzzHeavier was conditional on a spike and the spike passed (session + referer hop + HTMX fetch), so it is included last because it is slowest. 1fichier (4 %) and FileCrypt (3 %) always co-occur with a supported mirror. Real-Debrid covers only 1fichier; TorBox covers BuzzHeavier. Revisit debrid only if the site moves hosts.

## D5 — Cloudflare: FlareSolverr first, headed browser only on click

The owner already runs FlareSolverr (smeinecke fork) on the LAN, so it is a sunk dependency and solved FileDitch in ~7 s. It clears landing pages only; all bytes go through `HttpClient` with `Range`. If it is unreachable or fails, the queue item waits in "needs you" with one notification; the Playwright headed browser opens only when the user clicks. The app never pops a browser unprompted. `cf_clearance` is not persisted; re-solving is cheaper than managing IP/UA-bound cookies.

## D6 — Integrity: archive CRC test is the truth

The site publishes no hashes. BuzzHeavier's page hashes match; MegaDB's displayed SHA-256 did **not** match a valid file. So resolvers pass hashes only when known-good, and `Installer` always runs `unrar t`/`7z t` before touching the old install.

## D7 — Updates: fully automatic, full re-download, no previous versions kept

Pre-installed RARs mean an update is a 10–70 GB re-download, not a patch. Flow: detect (`modified` AND build/version differs) → download to `.downloads` → CRC test → extract to `.staging` → back up saves → swap → prune. The old install is intact until the swap. Only the last `KeepBackups` save backups are kept. "New game posted" notifications default off.

## D8 — Data: SQLite + JSON settings + keychain shim

`Microsoft.Data.Sqlite`, hand-written schema with a migrations table (no EF Core). Settings as one flat JSON file. Secrets through `secret-tool`/DPAPI/0600-file; currently unused, kept because adding a token later should not need a redesign.

## D9 — Save backup heuristic is best-effort

Releases use different emulators (Goldberg/GSE, RUNE, online-fix). We back up `GSE Saves`/`Goldberg SteamEmu Saves/<app_id>` from the game's prefix and save-like directories inside the game folder (`Save*`, `Saves`, `SaveGames`, `Profiles`, `Saved`, `UserData`…), skipping engine folders (`Engine`, `Content`, `Binaries`, `drive_c`…). Marked `ponytail:` in code. Revisit with a per-game override if a game's saves are missed.

## D10 — Verification via logs, not screenshots (Nov 6 2026)

Early UI checks used `spectacle` screenshots of the active window; that captured whatever the owner had on screen and proved nothing about app state. Owner: "why tf are we relying on screenshots instead of having proper logging?" Resolution: `Microsoft.Extensions.Logging` throughout Core with an in-house `FileLoggerProvider` (daily files, 7-day prune, live tail in the Activity view, runtime level switch, `--verbose`), signed URLs redacted. Rule in `AGENTS.md`: verify by reading the log and the DB.

## D11 — The app owns and launches games; no Heroic/Lutris prefix scanning (Nov 6 2026)

The plan originally said "launching is Heroic/Lutris; the app installs and stops" and backed up Goldberg saves by scanning configured Heroic/Lutris prefix roots. Owner: "we also install/own and manage the games. I don't want to do any manual work if possible."

Choice: each game gets a Wine prefix at `<library>/<slug>/prefix`, created on first Play by `umu-run` + Proton (`GAMEID=umu-<steam_app_id>`, `STORE=none`, `PROTONPATH` optional). `ExeFinder` picks the executable; the user can override exe, Proton and arguments per game; running state and playtime are tracked; updates wait for a running game and preserve the prefix by moving it into the new tree. `PrefixRoots` was removed from settings and UI; `SaveLocator.FindEmulatorSaves` only looks inside the game's own prefix. Windows runs the exe directly.

Locks in: the prefix is a per-game artifact that lives and dies with the game folder; deleting a game deletes its prefix and saves (after backup). Revisit if the owner wants a shared prefix, Steam shortcuts, or a Heroic sideload export.

Superseded in part by D17 (Nov 18 2026): the prefix is still per game and owned by the app, but it no longer lives inside the game folder, and deleting a game no longer takes it by default.

## D12 — No spikes or scratch files in the repo

Throwaway harnesses live under `/tmp/steamrip-spikes`, test profiles under `/tmp/sr-cfg`, test libraries under `/tmp/sr-lib`. Findings that matter are written into `docs/` and, where parseable, saved as fixtures under `tests/…/Fixtures/`.

## D13 — Native packages per platform, no tarballs, no bundled runtime (Nov 6 2026)

Owner: "I want proper packages: deb, rpm, AppImage, AUR for Linux, exe for Windows. I don't like tarball releases."

Choice: one self-contained single-file `dotnet publish` per OS (compressed, ~90 MB installed), wrapped as `.deb`/`.rpm`/Arch `.pkg.tar.zst` by nfpm from a single `packaging/nfpm.yaml`, as an AppImage by appimagetool, and as a per-user Inno Setup installer on Windows (`%LOCALAPPDATA%\Programs`, no UAC, closes and restarts the tray app on upgrade, removes the HKCU Run value on uninstall). The AUR package is `steamrip-desktop-bin` and repacks the released `.deb`, so there is exactly one Linux binary to debug. Builds are tag-driven and calendar-versioned, `YYYY.M.D` without zero padding (owner: "use a date version system so 2026.11.6 for today"); a further release the same day appends `.1`, `.2`; a `-rc1` suffix marks a pre-release, which nfpm renders as `~rc1` so distro package managers sort it below the final. Published by GitHub Actions with generated notes and checksums.

Rejected: Flatpak (the sandbox breaks shelling out to `unrar`, `umu-run`, `wine`, `secret-tool` and writing into an arbitrary library folder); Snap (same, plus confinement of `~/Games`); MSIX/Store (signing and identity requirements for a single-user app); framework-dependent builds (would make "install .NET 10" the first manual step); code signing (cost; SmartScreen warning accepted and documented).

Locks in: the binary is `/usr/bin/steamrip-desktop` with the icon name `steamrip-desktop` in hicolor, which the app's own autostart `.desktop` relies on; `Autostart.ExecutablePath` prefers `$APPIMAGE`. Revisit if the app grows a self-updater (Velopack would replace the installers) or if a maintainer wants Flatpak badly enough to add portals.

## D14 — Launch tweaks are env vars and wrappers around umu, defaults with per-game overrides (Nov 7 2026)

Owner: "I need more default settings for things like mangohud, gamescope, hdr, other proton variables … default settings in the settings page but also settings per game", and "can't we do any checks about what hardware and software we're running?"

Choice: one `LaunchOptions` record (MangoHud, gamemode, gamescope + args, HDR, Wine Wayland, FSR, fsync, Proton log, DLL overrides, free-form environment, wrapper). `Settings.DefaultLaunch` holds the defaults; `installs.launch_json` holds only the fields a game overrides, resolved as game → defaults → `AppDefaults`. `GameLauncher.Compose` is a pure function from options to command line + environment, so every combination is unit-tested. HDR follows the ArchWiki HDR page exactly: gamescope `--hdr-enabled` or Wine's Wayland driver, both with `DXVK_HDR=1`, never `ENABLE_HDR_WSI` under gamescope. Missing tools are skipped with a log warning, not an error. `HostCapabilities` probes session, per-display HDR state (compositor CLI, EDID fallback), GPU driver, the tools and each Proton build's known env vars; it only produces indicator lines and an advisory sentence next to the HDR box, never a block.

Rejected: esync and NTSync toggles (esync is gone in Proton 10 and NTSync is chosen by wine from `/dev/ntsync`; both are indicator lines); gating env vars on what a build "knows" (unknown vars are inert, so always setting them is simpler and works for the umu default build we cannot inspect); Avalonia `Screens` for the gamescope size (Core stays Avalonia-free; `/sys/class/drm/*/modes` gives the same answer); a separate per-option settings column (JSON blob keeps migrations at one line).

Locks in: `LaunchOptions` property names are the JSON keys in both `settings.json` and `launch_json`; renaming one silently resets that option for existing users, so add, do not rename.

## D15 — GE-Proton is the default, DW-Proton is offered, UMU-Proton never (Nov 7 2026)

Owner: "never enable umu proton by default pls proton 10 is way too old. use ge-proton as default", after a day of HDR debugging in Cyberpunk.

Context: with `PROTONPATH` unset, umu 1.4.3 picks `UMU-Latest` = UMU-Proton-10.0-4, a Proton 10 build without the Wayland driver and without HDR auto-detection. The app used to force `DXVK_HDR=1` + `PROTON_ENABLE_HDR=1`, which on that build (and on Proton-CachyOS) produced PQ output onto what Wine considered an SDR display: grey blacks and dull colours.

Choice: `Settings.DefaultProtonPath` defaults to the keyword `GE-Proton`, which umu downloads and keeps current itself (`PROTONPATH=GE-Proton`); `""` is read as the same. `DW-Proton` (Dawn Winery's Proton-CachyOS + GE patch set) is a second keyword, downloaded by `ProtonDownloader` from the GitHub mirror with SHA-512 verification because umu has no keyword for it. Both appear in the Settings list and the per-game list next to the locally found builds. HDR composition no longer sets any HDR env var; it only chooses the path (gamescope `--hdr-enabled` or Wine Wayland) and leaves detection to Proton, per the GE-Proton 11 documentation.

Rejected: shipping our own umu `PROTONPATH` default of `UMU-Proton` (too old); checking GitHub on every launch (one check per 6 h, offline uses what is installed); a generic "Proton from any GitHub repo" feature (one hard-coded mirror, one archive layout, `// ponytail`).

Locks in: `GE-Proton` and `DW-Proton` are stored verbatim in `settings.json` and `installs.proton`; they are identifiers, do not rename them.

## D16 — Genres are the site's WordPress categories; sort happens in SQL (Nov 10 2026)

Owner: "add sort filters to the browse page. also look into if we can make some kind of genre filter".

Choice: `PostSort` (updated, added, name, size desc/asc) is applied in `Database.SearchPosts` as `ORDER BY`, never on the 150 rows in memory, because the window must follow the key ("largest 150", not "the 150 newest, sorted by size"). Size gets a numeric column parsed from the "Game Size" line. Genre = the post's WordPress `categories`; the site has exactly 20 categories and they are all genres, so no mapping table and no Steam/IGDB lookups. The migration that adds the tables also deletes `catalog_complete`, which the existing sync logic already treats as "do a full pass", so the backfill was one scheduled sync with zero user action.

"Most popular" is the site's `/top-games/` ranking (post ids only, first 42 pages, once a day). It is the only steamrip.com HTML listing the app reads; the catalog itself stays REST.

Rejected: Steam store genres via the Steam API (per-app requests for 5,000 games, rate limited, and the site's categories are good enough); WordPress `tags` (one per post, useless); sorting/filtering client-side; popularity from Steam (`GetMostPlayedGames` is 100 games, 56 of which are on the site) or SteamSpy (`request=all` answered "Too many connections" on every try).

Locks in: `PostSort` values are not persisted; category ids are the site's and may change meaning if the site re-tags, in which case the next full sync rewrites `post_categories`.

## D17 — Prefixes live outside the game folder, with a record, a lease and a reset (Nov 18 2026)

Owner: "make the proton prefix kinda stuff better."

Three things were wrong with `<game>/prefix`: a "delete files" uninstall took the saves with it; every update had to shuffle a multi-hundred-MB tree around the archive swap; and nothing remembered which Proton built it, so switching Proton (or a GE-Proton update landing between two launches) showed up later as a game that suddenly stopped working. Two launches could also start into one prefix and fight over its `wineserver`.

Choice:
- Prefixes at `<library>/.prefixes/<slug>` (`Settings.PrefixRoot` overrides the root). `PrefixManager.Prepare` moves a legacy in-folder prefix there on the next launch or uninstall — `Directory.Move`, so it is a rename, not a copy. A folder that only looks like `prefix` by name is left alone unless it really holds `drive_c`/`system.reg`.
- `.steamrip-prefix.json` inside the prefix records dates, launch count, the Proton setting, the resolved build and the `GAMEID`, so a backed-up or moved prefix keeps its history.
- A changed Proton build triggers `wineboot -u` through the same umu/wine runner before the game starts, logged as "prefix was created with X; switching to Y".
- `PrefixLease` (`flock` on `.steamrip-prefix.lock`) is held for the game's lifetime (and for Winetricks/Run exe), so a second launch says "the prefix is in use" instead of corrupting it. A crashed process releases it with its file descriptor; the lock file is never unlinked, because unlinking lets two processes lock two different inodes.
- Uninstall keeps the prefix unless `DeletePrefixOnUninstall` is set; a delete-files uninstall moves a legacy in-folder prefix out first. Goldberg/GSE saves are now backed up from the out-of-folder prefix (`InstallOptions.PrefixDir`).
- The ⋯ menu gained **Prefix info** (size, build, launches, last use) and **Reset prefix** (moves the tree to `<prefix>.bak-<utc>`, deletes nothing).
- Games without a Steam app id get `umu-steamrip-<slug>` as their `GAMEID` instead of every one of them sharing `umu-default`.

Rejected: one shared prefix for the whole library (one game's DLL overrides and redistributables break the next, which is why Steam keeps per-app compatdata); deleting the prefix on reset; pinning a Proton version into the prefix record and refusing to launch on a mismatch (too strict — a warning plus `wineboot -u` is what Steam does).

Locks in: the prefix moves one way only (nothing migrates back); empty `PrefixRoot` means the library default; D11's rule about not scanning Heroic/Lutris/Steam prefixes for saves still stands — this is about where *our* prefix lives, not about adopting someone else's. Windows has no prefix and the setting is Linux-only.


## D18 — aria2 is the transfer engine, torrents are a mirror type (Nov 18 2026)

Owner: "i cant install shit. make the downloads more reliable + maybe use aria2."

What the log showed for Red Dead Redemption 2: the only workable mirror was a **.torrent**, the pipeline downloaded its 146 KB metadata, ran `7z t` on it, reported "corrupt download", and then — because the leftover file counted as "already verified" — reused those same bytes on every retry. The job could never succeed, and the UI offered only "Retry", which fed the loop.

Choice:
- `Aria2Client` runs aria2c as a child process with JSON-RPC on loopback and drives it: `addUri`/`addTorrent`, `tellStatus` for progress/speed/failure, `forceShutdown` on cancel. No progress-line scraping, no shell quoting, and a stall (3 min HTTP / 10 min torrent with zero speed) is an explicit failure instead of a hung job.
- Settings → Downloads picks the engine (auto / aria2c / built-in); aria2 is skipped when it is not installed, and the built-in ranged loop remains for that case.
- A resolved `.torrent` is a mirror type, not an archive: fetch the metadata, pull the payload with aria2 (web seeds and DHT included), then take the archive the torrent produced (split volumes resolve to their first part).
- `ArchiveMarker` (`.ok` sidecar storing the size) is the only proof that a downloaded file may be reused, so a download the archive tool rejects is discarded and fetched once more instead of being retried unchanged.
- `ArchiveTool` sniffs magic bytes, so a mislabelled torrent or HTML error page is reported as what it is; split volumes (`.part03.rar`, `.r05`, `.7z.004`) resolve to the volume unrar/7z need.

Follow-up the same evening, after the owner pointed out that the site's own links are direct files: a .torrent is a *fallback*, never the default. `Settings.TorrentMirrors` is off, so a mirror that only offers one is skipped with "only offers a .torrent (peer-to-peer), not the file itself", and when a direct mirror resolves it wins. The chain keeps the first torrent aside and only uses it once every direct mirror has failed and the setting allows it.

Also learned the hard way, and worth writing down for whoever fixes the resolvers next: BuzzHeavier's `/download` endpoint answers with `cf-mitigated: challenge` (Cloudflare Turnstile) for anything but a human-driven click, and FileDitch's button is obfuscated and sets its `href` from JS after its WASM check. Both were reachable with the FlareSolverr setup the owner used to run; neither can be faked with a plain HTTP GET.

Rejected: bundling aria2 (licensing and a 4 MB binary to keep patched); a BitTorrent library in-process (a second protocol stack to maintain, and aria2 already does the HTTP side better than our loop); scraping aria2's console readout for progress.

Locks in: `Downloader` stays the only entry point for transfers; anything that needs a torrent goes through `Downloader.TorrentAsync`. aria2 remains optional — the app must still install games without it.


## D19 — The browser solver is the user's Chromium over DevTools, not a bundled one (Nov 18 2026)

Owner: "make sure u keep the app size small ... dont have dependency issues."

Playwright as the challenge solver cost 140 MB of package (its Node driver) and broke in the packaged app with "Driver not found" because `dotnet publish` prunes the driver tree. The package went from 95 MB to **293 MB**, the install to 579 MB — for one feature.

Choice: `CdpBrowser` speaks the DevTools protocol over a WebSocket to a Chromium the user already has (PATH: chromium, chromium-browser, Chrome, Brave, Edge, Vivaldi; last resort the Chromium Playwright left in `~/.cache/ms-playwright`). No bundled runtime, no Node, no package growth: the rpm is **42 MB**, installed 50 MB. If no browser exists the app says so instead of pretending.

Two things learned while making the hosters work:
- `--no-proxy-server`: Chromium obeys the desktop's proxy settings; a broken system proxy turns every hoster into `ERR_PROXY_CONNECTION_FAILED` while curl goes out fine. And distro builds differ — the solver probes `https://steamrip.com/` and moves to the next browser if the page fails.
- `Page.navigate` with a `referrer` is what makes the hosters serve their real page (a bare navigation has no referer and gets bounced to the homepage).

With that, both gates resolve without FlareSolverr: FileDitch sets its link on the main button when the gate's script runs, and BuzzHeavier's `hx-redirect` is read from the browser's network layer (the page's own JS cannot see it). Verified against live mirrors: HTTP 206, `application/vnd.rar`, real bytes.

Rejected: keeping Playwright and shipping the driver (size, and it is only a wrapper around what the system browser already is); a BitTorrent-style client for the browser part; requiring the user to run FlareSolverr (it stays supported when present, as a preference).

## D20 — The core is being ported to Rust, unit by unit, against recordings (Nov 19 2026)

Owner: "the base code is already shit, fix it a lot and do this on the code:
https://github.com/erkamyaman/rust-anything".

The C# app works (107 tests, ReSharper clean) but the owner wants it in Rust. rust-anything's
method is the only defensible way to do that without guessing at parity: record what the
original answers, port, replay every case, and only then move on. Its CLI does not support
.NET, so the loop is applied by hand.

Choice:
- A sibling Cargo workspace under `rust/` (never referenced by the app yet). `crates/steamrip-core`
  mirrors `src/SteamRip.Core` one pure unit at a time.
- `tests/SteamRip.Core.Tests/GoldenTests.cs` is the recorder *and* the guard: with
  `SR_GOLDEN_DIR` set (`make golden`) it writes `rust/fixtures/*.json`; without it, it reads
  those files back and asserts today's C# behaviour still matches. Drift on either side fails.
- `crates/steamrip-core/tests/parity.rs` replays every recorded case. Anything that is not
  byte-identical is a bug in the port.
- First slice: `TitleParser.Parse`, `CatalogParser.ParseSize`, `SteamAppIdResolver.TitlePrefix`.
  `Math.Round`'s banker's rounding is reproduced with `round_ties_even`, because a size line is
  the kind of thing that ships wrong by one byte and never gets noticed.

Locks in: the C# app remains the source of truth until the port answers for itself; a unit is
"ported" only with a recording behind it; nothing in `rust/` may be referenced by the app
during this phase, so a half-finished port can never break a release.

Rejected: a big-bang Rust rewrite (no way to prove behaviour stayed the same, and the launch,
hoster and installer paths have too many learned edge cases); running rust-anything's CLI
against C# (unsupported, and its wrappers assume a dynamic language surface).

