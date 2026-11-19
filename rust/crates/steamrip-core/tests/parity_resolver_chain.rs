//! Parity for the mirror walk: replays rust/fixtures/resolver_chain.json, recorded from the C#.

use serde::Deserialize;
use std::collections::HashMap;
use std::path::PathBuf;
use steamrip_core::chain::{decide, Outcome};
use steamrip_core::hoster::Hoster;

#[derive(Deserialize)]
struct MirrorRow {
    host: String,
    url: String,
}

#[derive(Deserialize)]
struct OutcomeRow {
    url: String,
    kind: String,
    message: Option<String>,
}

#[derive(Debug, Deserialize)]
struct ChosenRow {
    host: String,
    url: String,
    is_torrent: bool,
}

#[derive(Deserialize)]
struct Case {
    name: String,
    order: Vec<String>,
    mirrors: Vec<MirrorRow>,
    allow_torrents: bool,
    outcomes: Vec<OutcomeRow>,
    chosen: Option<ChosenRow>,
    errors: Vec<String>,
}

fn hoster(name: &str) -> Hoster {
    match name {
        "BuzzHeavier" => Hoster::BuzzHeavier,
        "FileDitch" => Hoster::FileDitch,
        "GoFile" => Hoster::GoFile,
        "MegaDb" => Hoster::MegaDb,
        "Fichier" => Hoster::Fichier,
        "FileCrypt" => Hoster::FileCrypt,
        other => panic!("unknown hoster {other}"),
    }
}

#[test]
fn resolver_chain_matches_the_original() {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("..")
        .join("fixtures")
        .join("resolver_chain.json");
    let raw = std::fs::read_to_string(&path)
        .unwrap_or_else(|e| panic!("missing fixture {}: {e}", path.display()));
    let doc: serde_json::Value = serde_json::from_str(&raw).expect("valid json");
    let cases: Vec<Case> = serde_json::from_value(doc["cases"].clone()).expect("cases");
    assert!(!cases.is_empty(), "fixture has no cases");

    for case in cases {
        let order: Vec<Hoster> = case.order.iter().map(|n| hoster(n)).collect();
        let mirrors: Vec<(Hoster, String)> = case
            .mirrors
            .iter()
            .map(|m| (hoster(&m.host), m.url.clone()))
            .collect();
        let outcomes: HashMap<String, Outcome> = case
            .outcomes
            .iter()
            .map(|o| {
                let outcome = match o.kind.as_str() {
                    "direct" => Outcome::Direct,
                    "torrent" => Outcome::Torrent,
                    _ => Outcome::Failed(o.message.clone().unwrap_or_default()),
                };
                (o.url.clone(), outcome)
            })
            .collect();

        let decision = decide(&order, &mirrors, &outcomes, case.allow_torrents);
        match (&decision.chosen, &case.chosen) {
            (None, None) => {}
            (Some((host, url)), Some(expected)) => {
                assert_eq!(format!("{host:?}"), expected.host, "host for {}", case.name);
                assert_eq!(url, &expected.url, "url for {}", case.name);
                assert_eq!(
                    decision.chosen_is_torrent, expected.is_torrent,
                    "torrent flag for {}",
                    case.name
                );
            }
            (got, expected) => panic!("chosen mismatch for {}: {got:?} vs {expected:?}", case.name),
        }
        assert_eq!(decision.errors, case.errors, "errors for {}", case.name);
    }
}
