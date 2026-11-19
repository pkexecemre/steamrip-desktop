//! Parity against the recording of the C# `SteamAppIdResolver` token, trust and search rules.
//!
//! The fixture under `rust/fixtures/app_ids.json` is written by
//! `tests/SteamRip.Core.Tests/GoldenAppIdTests.cs` (run with `SR_GOLDEN_DIR=rust/fixtures`)
//! and holds token sets, `NamesMatch` answers and the `PickSearchMatch` id per case. This test
//! replays every case; a mismatch means the port is wrong, not the fixture.

use serde::Deserialize;
use std::path::PathBuf;
use steamrip_core::app_ids;

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
#[serde(tag = "kind", rename_all = "snake_case")]
enum Case {
    Tokens {
        input: String,
        tokens: Vec<String>,
    },
    NamesMatch {
        game: String,
        steam: String,
        #[serde(rename = "match")]
        matched: bool,
    },
    Pick {
        game: String,
        hits: Vec<Hit>,
        picked: Option<i64>,
    },
}

#[derive(Deserialize)]
struct Hit {
    id: i64,
    name: String,
}

#[test]
fn app_id_rules_match_the_original() {
    let path = fixtures_dir().join("app_ids.json");
    let raw = std::fs::read_to_string(&path)
        .unwrap_or_else(|e| panic!("missing fixture {}: {e}; run: make golden", path.display()));
    let doc: Cases = serde_json::from_str(&raw).expect("fixture is valid json");
    assert!(!doc.cases.is_empty(), "fixture has no cases");

    for case in &doc.cases {
        match case {
            Case::Tokens { input, tokens } => {
                let mut got: Vec<String> = app_ids::tokens(input).into_iter().collect();
                got.sort();
                let mut expected = tokens.clone();
                expected.sort();
                assert_eq!(got, expected, "tokens for {input:?}");
            }
            Case::NamesMatch {
                game,
                steam,
                matched,
            } => {
                assert_eq!(
                    app_ids::names_match(game, steam),
                    *matched,
                    "names_match({game:?}, {steam:?})"
                );
            }
            Case::Pick { game, hits, picked } => {
                let hits: Vec<(i64, &str)> =
                    hits.iter().map(|hit| (hit.id, hit.name.as_str())).collect();
                assert_eq!(
                    app_ids::pick_search_match(game, &hits),
                    *picked,
                    "pick_search_match({game:?})"
                );
            }
        }
    }
}
