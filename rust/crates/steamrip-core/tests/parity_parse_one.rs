//! Parity against the recording of the C# `CatalogParser.ParseOne`.
//!
//! The fixture under `rust/fixtures/parse_one.json` is written by
//! `tests/SteamRip.Core.Tests/GoldenParseOneTests.cs` (run with `SR_GOLDEN_DIR=rust/fixtures`)
//! and holds every post of the saved REST page plus the small edge posts, together with the
//! `CatalogPost` fields the original builds from each (or the exception type it throws).
//! This test replays every case; a mismatch means the port is wrong, not the fixture.

use serde::Deserialize;
use serde_json::Value;
use std::path::PathBuf;
use steamrip_core::catalog::parse_one::{parse_one, CatalogPost, ParseOneError};

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
    post: Value,
    parsed: Option<CatalogPost>,
    error: Option<String>,
}

/// The C# exception a case's post provokes maps onto the field-level error the port names.
fn expected_error(exception: &str, name: &str) -> ParseOneError {
    match exception {
        "KeyNotFoundException" => ParseOneError::MissingLink,
        "InvalidOperationException" => ParseOneError::InvalidCategory,
        other => panic!("unmapped C# exception {other} in case {name:?}"),
    }
}

#[test]
fn parse_one_matches_the_original() {
    let path = fixtures_dir().join("parse_one.json");
    let raw = std::fs::read_to_string(&path)
        .unwrap_or_else(|e| panic!("missing fixture {}: {e}; run: make golden", path.display()));
    let doc: Cases = serde_json::from_str(&raw).expect("fixture is valid json");
    assert!(!doc.cases.is_empty(), "fixture has no cases");

    let mut real_posts = 0;
    for case in &doc.cases {
        if case.name.starts_with("real post ") {
            real_posts += 1;
        }
        match (parse_one(&case.post), &case.parsed, case.error.as_deref()) {
            (Ok(Some(got)), Some(expected), None) => {
                assert_eq!(&got, expected, "case {:?}", case.name);
            }
            (Ok(None), None, None) => {}
            (Err(err), None, Some(exception)) => {
                let expected = expected_error(exception, &case.name);
                assert_eq!(err, expected, "case {:?}", case.name);
            }
            (got, expected, error) => panic!(
                "shape mismatch for {:?}: got {got:?}, expected {expected:?}, error {error:?}",
                case.name
            ),
        }
    }

    assert_eq!(
        real_posts, 40,
        "the fixture replays every real post of the saved REST page"
    );
}
