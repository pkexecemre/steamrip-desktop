//! Parity for the save-folder walk: replays rust/fixtures/save_walk_tree.json, recorded from the
//! C# SaveLocator.SelectInGameFolder. A mismatch means the port is wrong, not the fixture.

use serde::Deserialize;
use std::path::PathBuf;
use steamrip_core::save_locator::{find_in_game_folder, TreeEntry};

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
    max_depth: i32,
    entries: Vec<EntryRow>,
    found: Vec<String>,
}

#[derive(Deserialize)]
struct EntryRow {
    path: String,
    directory: bool,
    link: bool,
}

#[test]
fn save_walk_matches_the_original() {
    let path = fixtures_dir().join("save_walk_tree.json");
    let raw = std::fs::read_to_string(&path)
        .unwrap_or_else(|e| panic!("missing fixture {}: {e}", path.display()));
    let doc: Cases = serde_json::from_str(&raw).expect("fixture is valid json");
    assert!(!doc.cases.is_empty(), "fixture has no cases");

    for case in &doc.cases {
        let entries: Vec<TreeEntry> = case
            .entries
            .iter()
            .map(|row| TreeEntry {
                path: row.path.clone(),
                is_dir: row.directory,
                is_link: row.link,
            })
            .collect();
        let got = find_in_game_folder(&entries, case.max_depth);
        assert_eq!(got, case.found, "case {:?}", case.name);
    }
}
