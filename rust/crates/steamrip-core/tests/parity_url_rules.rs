//! Parity for the URL rules: replays rust/fixtures/url_rules.json, recorded from the C#.

use serde::Deserialize;
use std::path::PathBuf;

fn fixtures_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("..")
        .join("fixtures")
}

#[derive(Deserialize)]
struct UrlCase {
    href: String,
    page: Option<String>,
    looks_like_archive: bool,
    is_download: bool,
}

#[test]
fn url_rules_match_the_original() {
    let path = fixtures_dir().join("url_rules.json");
    let raw = std::fs::read_to_string(&path)
        .unwrap_or_else(|e| panic!("missing fixture {}: {e}", path.display()));
    let doc: serde_json::Value = serde_json::from_str(&raw).expect("valid json");
    let cases: Vec<UrlCase> = serde_json::from_value(doc["cases"].clone()).expect("cases");
    assert!(!cases.is_empty(), "fixture has no cases");
    for case in cases {
        assert_eq!(
            steamrip_core::url_rules::looks_like_archive(&case.href),
            case.looks_like_archive,
            "looks_like_archive for {:?}",
            case.href
        );
        assert_eq!(
            steamrip_core::url_rules::is_download_link(&case.href, case.page.as_deref()),
            case.is_download,
            "is_download_link for {:?}",
            case.href
        );
    }
}
