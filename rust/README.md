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
| `launch::split_args` | `GameLauncher.SplitArgs` | `fixtures/compose.json` |

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
