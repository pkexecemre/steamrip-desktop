//! Parity for the resume math: replays rust/fixtures/range_plan.json, recorded from the C#.

use serde::Deserialize;
use std::path::PathBuf;

#[derive(Deserialize)]
struct Case {
    status: u16,
    have: i64,
    content_range_length: Option<i64>,
    content_length: Option<i64>,
    append: bool,
    start: i64,
    total: Option<i64>,
}

#[test]
fn range_plan_matches_the_original() {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("..")
        .join("fixtures")
        .join("range_plan.json");
    let raw = std::fs::read_to_string(&path)
        .unwrap_or_else(|e| panic!("missing fixture {}: {e}", path.display()));
    let doc: serde_json::Value = serde_json::from_str(&raw).expect("valid json");
    let cases: Vec<Case> = serde_json::from_value(doc["cases"].clone()).expect("cases");
    assert!(!cases.is_empty(), "fixture has no cases");
    for case in cases {
        let plan = steamrip_core::downloader::plan_range(
            case.status,
            case.have,
            case.content_range_length,
            case.content_length,
        );
        assert_eq!(plan.append, case.append, "append for {:?}", case.status);
        assert_eq!(plan.start, case.start, "start for {:?}", case.status);
        assert_eq!(plan.total, case.total, "total for {:?}", case.status);
    }
}
