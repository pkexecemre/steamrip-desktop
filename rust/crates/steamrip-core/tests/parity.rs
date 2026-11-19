//! Parity against recordings of the C# original.
//!
//! The fixtures under `rust/fixtures` are written by
//! `tests/SteamRip.Core.Tests/GoldenTests.cs` (run with `SR_GOLDEN_DIR=rust/fixtures`) and
//! describe what the original answers for each case. This test replays every case; a
//! mismatch means the port is wrong, not the fixture.

use serde::Deserialize;
use std::path::PathBuf;

fn fixtures_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("..")
        .join("fixtures")
}

fn load<T: for<'de> Deserialize<'de>>(name: &str) -> Vec<T> {
    let path = fixtures_dir().join(name);
    let raw = std::fs::read_to_string(&path)
        .unwrap_or_else(|e| panic!("missing fixture {}: {e}; run: make golden", path.display()));
    let doc: Cases<T> = serde_json::from_str(&raw).expect("fixture is valid json");
    doc.cases
}

#[derive(Deserialize)]
struct Cases<T> {
    cases: Vec<T>,
}

#[derive(Deserialize)]
struct TitleCase {
    input: String,
    name: String,
    version: Option<String>,
}

#[derive(Deserialize)]
struct SizeCase {
    input: Option<String>,
    bytes: Option<i64>,
}

#[derive(Deserialize)]
struct PrefixCase {
    input: String,
    prefix: Option<String>,
}

#[test]
fn title_parser_matches_the_original() {
    let cases: Vec<TitleCase> = load("title_parser.json");
    assert!(!cases.is_empty(), "fixture has no cases");
    for case in cases {
        let (name, version) = steamrip_core::catalog::title_parser::parse(&case.input);
        assert_eq!(name, case.name, "name for {:?}", case.input);
        assert_eq!(version, case.version, "version for {:?}", case.input);
    }
}

#[test]
fn parse_size_matches_the_original() {
    let cases: Vec<SizeCase> = load("size.json");
    assert!(!cases.is_empty(), "fixture has no cases");
    for case in cases {
        let got = steamrip_core::catalog::size::parse_size(case.input.as_deref());
        assert_eq!(got, case.bytes, "bytes for {:?}", case.input);
    }
}

#[test]
fn title_prefix_matches_the_original() {
    let cases: Vec<PrefixCase> = load("title_prefix.json");
    assert!(!cases.is_empty(), "fixture has no cases");
    for case in cases {
        let got = steamrip_core::catalog::title_prefix::title_prefix(&case.input);
        assert_eq!(got, case.prefix, "prefix for {:?}", case.input);
    }
}
