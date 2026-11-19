//! Parity for the launch-options precedence every launch uses: the per-game override, then the
//! user's defaults, then the app defaults. Replays rust/fixtures/effective_launch.json.

use serde::Deserialize;
use std::path::PathBuf;
use steamrip_core::launch_options::{effective_launch, LaunchOptions};

#[derive(Deserialize)]
struct Case {
    game: LaunchOptions,
    user: LaunchOptions,
    effective: LaunchOptions,
}

#[test]
fn effective_launch_matches_the_original() {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("..")
        .join("fixtures")
        .join("effective_launch.json");
    let raw = std::fs::read_to_string(&path)
        .unwrap_or_else(|e| panic!("missing fixture {}: {e}", path.display()));
    let doc: serde_json::Value = serde_json::from_str(&raw).expect("valid json");
    let cases: Vec<Case> = serde_json::from_value(doc["cases"].clone()).expect("cases");
    assert!(!cases.is_empty(), "fixture has no cases");
    for (index, case) in cases.iter().enumerate() {
        let got = effective_launch(&case.game, &case.user);
        assert_eq!(got, case.effective, "case {index}");
    }
}
