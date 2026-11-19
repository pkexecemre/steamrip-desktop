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

#[derive(Deserialize)]
struct SniffCase {
    head_hex: String,
    kind: Option<String>,
}

#[derive(Deserialize)]
struct FirstPartCase {
    input: String,
    existing: Vec<String>,
    expected: String,
}

fn hex_bytes(hex: &str) -> Vec<u8> {
    (0..hex.len())
        .step_by(2)
        .map(|i| u8::from_str_radix(&hex[i..i + 2], 16).expect("hex fixture"))
        .collect()
}

#[test]
fn sniff_matches_the_original() {
    let cases: Vec<SniffCase> = load("sniff.json");
    assert!(!cases.is_empty(), "fixture has no cases");
    for case in cases {
        let head = hex_bytes(&case.head_hex);
        let got = steamrip_core::archive::sniff(&head).map(|k| k.to_string());
        assert_eq!(got, case.kind, "kind for {:?}", case.head_hex);
    }
}

#[test]
fn first_part_matches_the_original() {
    let cases: Vec<FirstPartCase> = load("first_part.json");
    assert!(!cases.is_empty(), "fixture has no cases");
    for case in cases {
        let dir = std::path::Path::new("games");
        let archive = dir.join(&case.input);
        let existing = case.existing.clone();
        let picked = steamrip_core::archive::first_part(&archive, |p| {
            p.file_name()
                .map(|n| existing.iter().any(|e| e == n.to_string_lossy().as_ref()))
                .unwrap_or(false)
        });
        let name = picked
            .file_name()
            .map(|n| n.to_string_lossy().into_owned())
            .unwrap_or_default();
        assert_eq!(name, case.expected, "first part for {:?}", case.input);
    }
}

#[derive(Deserialize)]
struct SafeNameCase {
    input: String,
    safe: String,
}

#[derive(Deserialize)]
struct GameIdCase {
    appid: Option<i64>,
    slug: Option<String>,
    id: String,
}

#[derive(Deserialize)]
struct SizeTextCase {
    bytes: i64,
    text: String,
}

#[derive(Deserialize)]
struct PrefixPathCase {
    library: String,
    configured: Option<String>,
    root: String,
    dir: String,
}

#[derive(Deserialize)]
struct EnvCase {
    input: Option<String>,
    entries: Vec<Vec<String>>,
}

#[derive(Deserialize)]
struct MergeCase {
    mine: serde_json::Value,
    defaults: serde_json::Value,
    merged: serde_json::Value,
}

#[test]
fn safe_name_matches_the_original() {
    let cases: Vec<SafeNameCase> = load("safe_name.json");
    assert!(!cases.is_empty(), "fixture has no cases");
    for case in cases {
        let got = steamrip_core::prefix::safe_name(&case.input);
        assert_eq!(got, case.safe, "safe name for {:?}", case.input);
    }
}

#[test]
fn game_id_matches_the_original() {
    let cases: Vec<GameIdCase> = load("game_id.json");
    assert!(!cases.is_empty(), "fixture has no cases");
    for case in cases {
        let got = steamrip_core::prefix::game_id(case.appid, case.slug.as_deref());
        assert_eq!(got, case.id, "game id for {:?}", case.slug);
    }
}

#[test]
fn format_size_matches_the_original() {
    let cases: Vec<SizeTextCase> = load("format_size.json");
    assert!(!cases.is_empty(), "fixture has no cases");
    for case in cases {
        let got = steamrip_core::prefix::format_size(case.bytes);
        assert_eq!(got, case.text, "size text for {}", case.bytes);
    }
}

#[test]
fn prefix_paths_match_the_original() {
    let cases: Vec<PrefixPathCase> = load("prefix_paths.json");
    assert!(!cases.is_empty(), "fixture has no cases");
    for case in cases {
        let library = std::path::Path::new(&case.library);
        let root = steamrip_core::prefix::root(library, case.configured.as_deref());
        assert_eq!(
            root.to_string_lossy(),
            case.root,
            "root for {:?}",
            case.configured
        );
        let dir = steamrip_core::prefix::dir(&root, "Some Game");
        assert_eq!(
            dir.to_string_lossy(),
            case.dir,
            "dir for {:?}",
            case.configured
        );
    }
}

#[test]
fn environment_matches_the_original() {
    let cases: Vec<EnvCase> = load("environment.json");
    assert!(!cases.is_empty(), "fixture has no cases");
    for case in cases {
        let got: Vec<Vec<String>> =
            steamrip_core::launch_options::parse_environment(case.input.as_deref())
                .into_iter()
                .map(|(k, v)| vec![k, v])
                .collect();
        assert_eq!(got, case.entries, "entries for {:?}", case.input);
    }
}

#[test]
fn merge_matches_the_original() {
    let cases: Vec<MergeCase> = load("merge.json");
    assert!(!cases.is_empty(), "fixture has no cases");
    for case in cases {
        let mine: steamrip_core::launch_options::LaunchOptions =
            serde_json::from_value(case.mine).expect("mine");
        let defaults: steamrip_core::launch_options::LaunchOptions =
            serde_json::from_value(case.defaults).expect("defaults");
        let merged = serde_json::to_value(mine.merge_over(&defaults)).expect("serialize");
        assert_eq!(merged, case.merged, "merged options");
    }
}

#[derive(Deserialize)]
struct ComposeSpec {
    steam_app_id: Option<i64>,
    slug: Option<String>,
    proton_path: Option<String>,
    arguments: Option<String>,
    options: serde_json::Value,
}

#[derive(Deserialize)]
struct ComposeScreen {
    width: u32,
    height: u32,
}

#[derive(Deserialize)]
struct ComposeCase {
    name: String,
    runner: Option<String>,
    tools: Vec<String>,
    screen: Option<ComposeScreen>,
    spec: ComposeSpec,
    command: Vec<String>,
    environment: Vec<Vec<String>>,
    warnings: Vec<String>,
}

#[test]
fn compose_matches_the_original() {
    let cases: Vec<ComposeCase> = load("compose.json");
    assert!(!cases.is_empty(), "fixture has no cases");
    for case in cases {
        let options: steamrip_core::launch_options::LaunchOptions =
            serde_json::from_value(case.spec.options).expect("options");
        let spec = steamrip_core::launch::LaunchSpec {
            steam_app_id: case.spec.steam_app_id,
            slug: case.spec.slug.clone(),
            proton_path: case.spec.proton_path.clone(),
            arguments: case.spec.arguments.clone(),
            options: Some(options),
        };
        let tools = case.tools.clone();
        let composed = steamrip_core::launch::compose(
            &spec,
            case.runner.as_deref(),
            "/games/Test Game/game.exe",
            Some("/prefixes/Test Game"),
            |tool| {
                tools
                    .iter()
                    .any(|t| t == tool)
                    .then(|| format!("/usr/bin/{tool}"))
            },
            case.screen.as_ref().map(|s| (s.width, s.height)),
        );
        assert_eq!(composed.command, case.command, "command for {}", case.name);
        let env: Vec<Vec<String>> = composed
            .environment
            .iter()
            .map(|(k, v)| vec![k.clone(), v.clone()])
            .collect();
        assert_eq!(env, case.environment, "environment for {}", case.name);
        assert_eq!(
            composed.warnings, case.warnings,
            "warnings for {}",
            case.name
        );
    }
}
