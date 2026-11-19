# rust/ — the port of SteamRip Desktop's core to Rust

The C# app stays the source of truth while this grows. Every unit here is checked against
**recordings of the C# original**, so a port is only "done" when it answers the same thing
for every recorded case. Same loop as [rust-anything](https://github.com/erkamyaman/rust-anything),
applied by hand because its CLI does not target .NET:

    record (C#)  ->  port (Rust)  ->  parity (cargo test)

## The loop

1. **Record.** `make golden` runs `GoldenTests` with `SR_GOLDEN_DIR=rust/fixtures`, which
   writes what the C# implementation answers for a fixed case list into `rust/fixtures/*.json`.
   The same test also *reads* those files back and compares them with current behaviour, so a
   silent change on the C# side fails there first.
2. **Port.** Implement the unit in `crates/steamrip-core/src/…` with no behaviour of its own.
3. **Check.** `make rust-test` replays every recorded case; `make rust-check` adds `cargo fmt`
   and `clippy -D warnings`. A mismatch is a bug in the port, not in the recording.

## What is ported today

| Unit | C# original | Recording |
| --- | --- | --- |
| `catalog::title_parser::parse` | `Catalog/TitleParser.cs` | `fixtures/title_parser.json` |
| `catalog::size::parse_size` | `CatalogParser.ParseSize` | `fixtures/size.json` |
| `catalog::title_prefix::title_prefix` | `SteamAppIdResolver.TitlePrefix` | `fixtures/title_prefix.json` |
| `archive::sniff` | `ArchiveTool.SniffKind(ReadOnlySpan<byte>)` | `fixtures/sniff.json` |
| `archive::first_part` | `ArchiveTool.FirstPart(path, exists)` | `fixtures/first_part.json` |
| `prefix::safe_name` | `PrefixManager.SafeName` | `fixtures/safe_name.json` |
| `prefix::game_id` | `PrefixManager.GameId` | `fixtures/game_id.json` |
| `prefix::format_size` | `PrefixManager.FormatSize` | `fixtures/format_size.json` |
| `prefix::root/dir` | `PrefixManager.Root/Dir` | `fixtures/prefix_paths.json` |
| `launch_options::merge_over` | `LaunchOptions.MergeOver` | `fixtures/merge.json` |
| `launch_options::parse_environment` | `LaunchOptions.ParseEnvironment` | `fixtures/environment.json` |
| `launch::compose` | `GameLauncher.Compose` | `fixtures/compose.json` |
| `hosters::parse_file_ditch` | `HostPages.ParseFileDitch` | `fixtures/host_pages.json` |
| `hosters::parse_bzzhr` | `HostPages.ParseBzzhr` | `fixtures/host_pages.json` |
| `hosters::parse_megadb` | `HostPages.ParseMegaDb` | `fixtures/host_pages.json` |
| `hosters::gofile_website_token` | `GoFileResolver.WebsiteToken` | `fixtures/gofile_token.json` |
| `updates::normalize` | `UpdateDetector.Normalize` | `fixtures/normalize.json` |
| `updates::is_update` | `UpdateDetector.IsUpdate` | `fixtures/is_update.json` |
| `proton::is_keyword/effective` | `ProtonLocator.IsKeyword/Effective` | `fixtures/proton_selection.json` |
| `archive::is_verified` | `ArchiveMarker.IsVerified(length, text)` | `fixtures/archive_marker.json` |
| `archive::percent_values` | `ArchiveTool.PercentValues` | `fixtures/percent_values.json` |
| `save_locator::is_save_dir_name` | `SaveLocator.IsSaveDirName` | `fixtures/save_walk.json` |
| `save_locator::is_skipped_dir` | `SaveLocator.IsSkippedDir` | `fixtures/save_walk.json` |
| `proton_release::parse_latest` | `ProtonDownloader.ParseLatest` | `fixtures/proton_release.json` |
| `proton_release::parse_checksum` | `ProtonDownloader.ParseChecksum` | `fixtures/proton_checksum.json` |
| `catalog::parse_top_games` | `CatalogParser.ParseTopGames` | `fixtures/top_games.json` |
| `hoster::detect/normalize` | `HosterDetector.Detect/Normalize` | `fixtures/mirrors.json` |
| `catalog::mirrors_from_html` | `CatalogParser.MirrorsFromHtml` | `fixtures/mirrors.json` |
| `catalog::game_size_text` | `CatalogParser.GameSizeText` | `fixtures/game_size.json` |
| `proton::version_key/compare_versions` | `ProtonLocator.VersionKey/CompareVersions` | `fixtures/proton_versions.json` |
| `proton::newest_build_name` | `ProtonLocator.NewestBuildName` | `fixtures/proton_versions.json` |
| `proton::display_name` | `ProtonLocator.DisplayName` | `fixtures/proton_display_name.json` |
| `installer::safe_name` | `Installer.SafeName` | `fixtures/installer_safe_name.json` |
| `autostart::desktop_entry` | `Autostart.DesktopEntry` | `fixtures/autostart_entry.json` |
| `prefix::proton_label` | `PrefixManager.ProtonLabel(path, resolve)` | `fixtures/proton_label.json` |
| `prefix::resolve_build` | `PrefixManager.ResolveBuild(path, resolve, exists)` | `fixtures/proton_label.json` |
| `installer::collapse_target` | `Installer.CollapseTarget` | `fixtures/collapse.json` |
| `catalog::parse_categories` | `CatalogParser.ParseCategories` | `fixtures/categories.json` |
| `downloader::engine_name` | `Downloader.EngineNameFor` | `fixtures/torrent_engine.json` |
| `downloader::is_torrent` | `Downloader.IsTorrent` | `fixtures/torrent_engine.json` |
| `redist::select_installers` | `RedistInstaller.SelectVcInstallers` | `fixtures/redist_selection.json` |
| `dates::parse_utc_text` | `CatalogParser.ParseUtcText` | `fixtures/timestamps.json` |
| `prefix::needs_upgrade` | `PrefixManager.NeedsUpgrade` | `fixtures/prefix_upgrade.json` |
| `prefix::aside_path` | `PrefixManager.AsidePath` | `fixtures/prefix_upgrade.json` |
| `chain::effective_order` | `ResolverChain.EffectiveOrder` | `fixtures/hoster_order.json` |
| `settings::default_dir` | `Settings.DefaultDirFor` | `fixtures/settings_rules.json` |
| `settings::hosters_from_names` | `Settings.HostersFromNames` | `fixtures/settings_rules.json` |
| `settings::parse_hoster_order` | `Settings.ParseHosterOrder` | `fixtures/settings_rules.json` |
| `host_caps::edid_has_hdr` | `HostCapabilities.EdidHasHdr` | `fixtures/host_capabilities.json` |
| `host_caps::parse_kscreen` | `HostCapabilities.ParseKScreen` | `fixtures/host_capabilities.json` |
| `host_caps::vendor_name` | `HostCapabilities.VendorName` | `fixtures/host_capabilities.json` |
| `host_caps::proton_env_vars` | `HostCapabilities.ProtonEnvVars` | `fixtures/host_capabilities.json` |
| `host_caps::parse_mutter` | `HostCapabilities.ParseMutter` | `fixtures/host_displays.json` |
| `host_caps::parse_wlr_outputs` | `HostCapabilities.ParseWlrOutputs` | `fixtures/host_displays.json` |
| `host_caps::parse_vulkan_info` | `HostCapabilities.ParseVulkanInfo` | `fixtures/vulkan_screen.json` |
| `host_caps::largest_screen_of` | `HostCapabilities.LargestScreenOf` | `fixtures/vulkan_screen.json` |
| `save_locator::emulator_save_paths` | `SaveLocator.EmulatorSavePaths` | `fixtures/emulator_gofile.json` |
| `hosters::gofile_content_id` | `GoFileResolver.ContentIdOf` | `fixtures/emulator_gofile.json` |
| `launch::split_args` | `GameLauncher.SplitArgs` | `fixtures/compose.json` |
| `url_rules::looks_like_archive` | `InteractivePage.LooksLikeArchive` | `fixtures/url_rules.json` |
| `url_rules::is_download_link` | `InteractivePage.IsDownloadLink` | `fixtures/url_rules.json` |
| `launch_options::effective_launch` | `UpdateService.EffectiveLaunch` | `fixtures/effective_launch.json` |
| `exe_finder::rank/best` | `ExeFinder.Rank` | `fixtures/exe_finder.json` |
| `app_ids::tokens` | `SteamAppIdResolver.Tokens` | `fixtures/app_ids.json` |
| `app_ids::names_match` | `SteamAppIdResolver.NamesMatch` | `fixtures/app_ids.json` |
| `app_ids::pick_search_match` | `SteamAppIdResolver.PickSearchMatch` | `fixtures/app_ids.json` |
| `downloader::plan_range` | `Downloader.PlanRange` | `fixtures/range_plan.json` |

## Adding a unit

1. Add its cases to a `[Fact]` in `tests/SteamRip.Core.Tests/GoldenTests.cs` and run `make golden`.
2. Write the Rust unit and a `#[cfg(test)]` block for the obvious cases.
3. Add a test to `crates/steamrip-core/tests/parity.rs` that loads the fixture and replays it.
4. `make rust-check`.

## Rules

- Never "fix" a recording to make a port pass; change the C# side deliberately, then re-record.
- Units stay pure: strings, numbers, paths. Anything that downloads, writes or spawns a
  process belongs to the C# side until the whole app moves (see `docs/decisions.md` D20).
- The Rust crate is not referenced by the app yet, so a broken port cannot break a release.
