# Troubleshooting

Symptom first, fix second. The log is the source of truth: `<config>/logs/steamrip-YYYYMMDD.log`, also tailed in Activity.

## A game exits at once with a nonzero code (66 and friends)

**Symptom**: Play returns to the library in seconds, no window. Log:

```text
<Name> exited with 66 after 0.4s; last output:
...
```

OnlineFix/Steam DRM titles do this when Steam is not running or the game was not started from a Steam shortcut.

**Fix**: start Steam and sign in. If it still exits, add the game's `.exe` as a non-Steam game and launch it from that shortcut; the warning carries the last 40 captured output lines. A code other than 66 is usually a missing runtime or the wrong exe (change it under Launch options).

The app runs the exe through `umu-run`/Proton (`GAMEID=umu-<steam_app_id>` or `umu-steamrip-<slug>`), logs the code and tail, and adds playtime. It does not start Steam, create a Steam/non-Steam shortcut (its own shortcuts rerun the app with `--play <id>`), patch DRM, retry, or notify. The warning appears for any nonzero code or a run under 10 s; read it under Activity → **Open log folder**.

## The first Play sits for a while

**Symptom**: no game window for a minute or more. Log:

```text
Installing 2 VC redistributable(s) from _CommonRedist into <prefix>
Running vcredist_x64.exe
Still running vcredist_x64.exe (30s)…
```

`EnsureVcRedistAsync` runs the `_CommonRedist` installers under Wine before the game starts; most show no window. Each is capped at 90 s; a hung one is killed, logged, and the game starts anyway.

**Fix**: wait once. Success or failure writes `<prefix>/.steamrip-vc-redist`, so later launches skip it. To skip before the first Play (`<prefix>` is `<library>/.prefixes/<slug>/` unless `PrefixRoot` is set):

```sh
mkdir -p "<prefix>"
touch "<prefix>/.steamrip-vc-redist"
```

## A hoster keeps failing

**Symptom**: the item sits on `Resolving download link…`, then ends red with `No mirror could be resolved:` and one line per hoster.

FileDitch and BuzzHeavier gates need a browser. FlareSolverr is optional and not shipped; the default `http://localhost:8191` usually has nothing behind it. The log then says:

```text
FlareSolverr does not answer at http://localhost:8191; using the built-in headless browser for challenge pages
```

The gate runs in the system Chromium over DevTools, headless, no window. GoFile and MegaDB need neither.

**Fix**: install a Chromium-family browser on `PATH` (`chromium`, `google-chrome`, `brave-browser`, `microsoft-edge`, `vivaldi-stable`), or run FlareSolverr and set Settings → **Mirrors and Cloudflare** → **FlareSolverr URL**. The next job probes again; no restart.

"Needs you" is `NeedsAttention`: no mirror resolved (or torrent-only with torrents off), so the scheduler will not retry. Item red, notification `<Name> needs you`, waits for **Retry**.

**Solve in browser** shows on that item, enabled only when the post has a FileDitch or BuzzHeavier mirror. Pass the check in the visible window; the app takes the link and re-queues. It opens BuzzHeavier if present, else FileDitch. Failure: `The BuzzHeavier check was not passed in the window; nothing was downloaded.` or `FileDitch never produced a download link in the window; nothing was downloaded.`

## No Chromium, or a browser window is required

**Symptom**: gates fail with no window, or **Solve in browser** fails at once.

```text
no Chromium-family browser found: install chromium (or Google Chrome) to let the app pass hoster checks
No Chromium-family browser is installed; install chromium to use the browser fallback.
```

**Fix**: install a Chromium-family browser and put it on `PATH`. Nothing is bundled or downloaded; only installed browsers are driven. If one exists but cannot reach the site: `no browser could reach the site (…)` — check network/DNS, then install another Chromium build; a distro build can fail TLS/HTTP2 where another works. Automatic solving is headless; the only browser window the app opens is **Solve in browser**, kept open until you pass the check.

## A download will not start, or keeps restarting

**Symptom**: the job sits at 0%, or progress drops to 0 and the same bytes fail on every Retry.

Downloads live in `<downloads>/<slug>/`, default `~/Games/SteamRIP/.downloads/<slug>/` (`Settings.DownloadsRoot` overrides). Resume: the HTTP loop writes `<archive>.part` and sends `Range: bytes=<have>-`. `206` appends; `416` treats it as complete; `200` means the server ignored the range, logs `Server ignored the Range header` and restarts from 0. Early close: `Connection closed early: …; .part kept for resume`. aria2 resumes too, with a `.aria2` control file. A checksum mismatch deletes the partial; a file is reused only when its `.ok` marker says the archive test passed.

**Fix**: Retry resumes the `.part` when the server honours the range. Same bytes keep failing? Cancel, delete the slug folder, Retry:

```sh
rm -rf "<downloads>/<slug>"
```

That drops the `.part`, the `.ok` marker and any archive; the job resolves fresh and starts at zero.

## The prefix: where it is, resetting it

Default `<library>/.prefixes/<slug>/`; `Settings.PrefixRoot` overrides. An old in-folder `<game>/prefix` is moved there on the next launch.

**Symptom**: the game broke after a Proton/tweak change, or the prefix is wedged. **Fix**: game card → `⋯` → **Reset prefix (moves it aside)**. It refuses while running (`stop the game before resetting its prefix`). Moves the prefix to `<slug>.bak-<yyyyMMdd-HHmmss>` beside it, keeps the newest two `.bak-*`, and the next Play builds clean. Nothing is deleted at reset; remove `.bak-*` yourself to reclaim disk.

**Loses**: the working prefix — registry, redistributables, winetricks changes, `.steamrip-vc-redist` (the next Play runs VC redist again), and saves kept inside `drive_c`. Game files and game-folder saves are untouched. The aside copy holds the old data until pruned; after two more resets it is gone.

## Updates do not appear

**Symptom**: the site has a newer build, the library still says installed. Checked 5 s after start, then every `CheckIntervalMinutes` (default 60, minimum 5); changing it wakes the wait, and **Check for updates now** in the tray checks immediately. An update queues only when the build/version is newer, the game's auto-update is on, and global auto-updates are not paused.

**Fix**: **Check for updates now**; per-game toggle (card → `⋯` → **Auto-update on/off**); global auto-updates (tray **Pause auto-updates**, Queue header toggle). `NeedsAttention`/`Failed` games are skipped by the scheduler until retried, so no update appears for them.

`NeedsAttention`: a person must act (no mirror, torrent-only with torrents off). `Failed`: the attempt failed for a reason that may not go away (broken mirror, unwritable folder). Both notify (`<Name> needs you` / `<Name> failed`) and wait for **Retry**; neither retries hourly.

## Packaging oddities

**AppImage**: no installer; make it executable:

```sh
chmod +x steamrip-desktop-<v>-x86_64.AppImage
./steamrip-desktop-<v>-x86_64.AppImage
```

**.deb / .rpm**: binary `/usr/bin/steamrip-desktop`, menu `/usr/share/applications/steamrip-desktop.desktop`, icon `/usr/share/icons/hicolor/256x256/apps/steamrip-desktop.png`. The packages do not touch settings, library or logs.

| | Linux | Windows |
| --- | --- | --- |
| Settings, DB, logs, covers | `~/.config/steamrip-desktop/` (`settings.json`, `steamrip.db`, `logs/`, `covers/`) | `%APPDATA%\steamrip-desktop\` |
| Library (default) | `~/Games/SteamRIP/<slug>/` | `%USERPROFILE%\Games\SteamRIP\<slug>\` |

Logs are `logs/steamrip-YYYYMMDD.log`, 7 days kept; `$XDG_CONFIG_HOME` moves the Linux config dir. **From the app**: Settings → **On this machine** (config and log dirs, each with **Open**); Activity → **Open log folder**; game card → `⋯` → **Game folder** / **Prefix folder**.
