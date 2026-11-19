//! Parity against the recording of the C# ExeFinder.
//!
//! The fixture under `rust/fixtures/exe_finder.json` is written by
//! `tests/SteamRip.Core.Tests/GoldenExeFinderTests.cs` (run with `SR_GOLDEN_DIR=rust/fixtures`)
//! and holds the tree snapshots plus the executable ordering the original answers for them.
//! This test replays every case; a mismatch means the port is wrong, not the fixture.

use serde::Deserialize;
use std::path::PathBuf;
use steamrip_core::exe_finder::{rank, Entry};

fn fixtures_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("..")
        .join("fixtures")
}

#[derive(Deserialize)]
struct Cases {
    cases: Vec<Case>,
}

#[derive(Deserialize)]
struct Case {
    name: String,
    game: Option<String>,
    entries: Vec<EntryRow>,
    ranked: Vec<RankedRow>,
}

#[derive(Deserialize)]
struct EntryRow {
    path: String,
    directory: bool,
    link: bool,
    size: i64,
    depth: i32,
}

#[derive(Deserialize)]
struct RankedRow {
    path: String,
    size: i64,
    depth: i32,
    score: i32,
}

#[test]
fn exe_finder_matches_the_original() {
    let path = fixtures_dir().join("exe_finder.json");
    let raw = std::fs::read_to_string(&path)
        .unwrap_or_else(|e| panic!("missing fixture {}: {e}; run: make golden", path.display()));
    let doc: Cases = serde_json::from_str(&raw).expect("fixture is valid json");
    assert!(!doc.cases.is_empty(), "fixture has no cases");

    for case in &doc.cases {
        let entries: Vec<Entry> = case
            .entries
            .iter()
            .map(|row| Entry {
                path: row.path.clone(),
                is_dir: row.directory,
                is_link: row.link,
                size: row.size,
                depth: row.depth,
            })
            .collect();
        let got: Vec<(String, i64, i32, i32)> = rank(&entries, case.game.as_deref())
            .into_iter()
            .map(|candidate| {
                (
                    candidate.relative_path,
                    candidate.size,
                    candidate.depth,
                    candidate.score,
                )
            })
            .collect();
        let expected: Vec<(String, i64, i32, i32)> = case
            .ranked
            .iter()
            .map(|row| (row.path.clone(), row.size, row.depth, row.score))
            .collect();
        assert_eq!(got, expected, "case {:?}", case.name);
    }
}
