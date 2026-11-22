# The Rust app

`crates/steamrip-app` is the desktop app being ported from C#. The plan is that the app itself
becomes Rust (native window, no web view), while the external tools stay external: aria2 for
transfers, unrar/7z for archives, umu-run + Proton for launchers, and a system Chromium for the
Cloudflare checks.

What works today:

- a native iced window (wgpu, Wayland through winit) in the site's palette;
- the library read from the same profile the C# app writes (SQLite plus the cover cache);
- Play/Stop: the command comes from `steamrip-core`'s `compose()`, the port of the C# rules, so
  gamemoderun, umu-run, GAMEID, PROTONPATH, WINEPREFIX, exe and arguments match what the C# app
  writes to its log;
- playtime, last-played and the events table are written back, in the same ISO format .NET uses.

Diagnostics used to check behaviour without starting Proton:

```sh
cargo run -p steamrip-app -- --dry-run-play 0     # print the composed launch
cargo run -p steamrip-app -- --simulate-play 0 60 # write playtime/events as if it ran a minute
```

Still C#-only, next in line: catalog sync, hoster resolution, download, install, tray and
packaging. `steamrip-core` keeps growing with the pure rules first (checked against the
recordings in `fixtures/`), the app layer second.
