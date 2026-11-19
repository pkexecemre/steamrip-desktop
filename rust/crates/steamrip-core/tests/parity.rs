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

#[derive(Deserialize)]
struct HostPageCase {
    kind: String,
    html_file: Option<String>,
    html: Option<String>,
    url: Option<String>,
    hx: Option<String>,
    expires: Option<i64>,
    md5: Option<String>,
    sha256: Option<String>,
    wait: Option<u32>,
    hash: Option<String>,
    file_name: Option<String>,
    error: Option<String>,
}

fn page_html(case: &HostPageCase) -> String {
    match &case.html_file {
        Some(file) => {
            let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
                .join("../../../tests/SteamRip.Core.Tests/Fixtures")
                .join(file);
            std::fs::read_to_string(&path)
                .unwrap_or_else(|e| panic!("fixture {}: {e}", path.display()))
        }
        None => case.html.clone().unwrap_or_default(),
    }
}

#[test]
fn host_pages_match_the_original() {
    let cases: Vec<HostPageCase> = load("host_pages.json");
    assert!(!cases.is_empty(), "fixture has no cases");
    for case in cases {
        let html = page_html(&case);
        match case.kind.as_str() {
            "fileditch" => match steamrip_core::hosters::parse_file_ditch(&html) {
                Ok(page) => {
                    assert!(case.error.is_none(), "unexpected success: {}", case.kind);
                    assert_eq!(Some(page.url), case.url, "url");
                    assert_eq!(page.expires, case.expires, "expires");
                    assert_eq!(page.file_name, case.file_name, "file name");
                }
                Err(err) => assert_eq!(Some(err), case.error, "error"),
            },
            "bzzhr" => match steamrip_core::hosters::parse_bzzhr(&html) {
                Ok(page) => {
                    assert!(case.error.is_none(), "unexpected success");
                    assert_eq!(Some(page.hx_path), case.hx, "hx");
                    assert_eq!(page.md5, case.md5, "md5");
                    assert_eq!(page.sha256, case.sha256, "sha256");
                    assert_eq!(page.file_name, case.file_name, "file name");
                }
                Err(err) => assert_eq!(Some(err), case.error, "error"),
            },
            "megadb" => match steamrip_core::hosters::parse_megadb(&html) {
                Ok(page) => {
                    assert!(case.error.is_none(), "unexpected success");
                    assert_eq!(Some(page.pt_url), case.url, "url");
                    assert_eq!(Some(page.wait_seconds), case.wait, "wait");
                    assert_eq!(page.hash, case.hash, "hash");
                    assert_eq!(page.file_name, case.file_name, "file name");
                }
                Err(err) => assert_eq!(Some(err), case.error, "error"),
            },
            other => panic!("unknown kind {other}"),
        }
    }
}

#[derive(Deserialize)]
struct GofileTokenCase {
    token: String,
    unix_seconds: i64,
    expected: String,
}

#[test]
fn gofile_token_matches_the_original() {
    let cases: Vec<GofileTokenCase> = load("gofile_token.json");
    assert!(!cases.is_empty(), "fixture has no cases");
    for case in cases {
        let got = steamrip_core::hosters::gofile_website_token(&case.token, case.unix_seconds);
        assert_eq!(
            got, case.expected,
            "token for {} at {}",
            case.token, case.unix_seconds
        );
    }
}

#[derive(Deserialize)]
struct NormalizeCase {
    input: Option<String>,
    normalized: Option<String>,
}

#[test]
fn normalize_matches_the_original() {
    let cases: Vec<NormalizeCase> = load("normalize.json");
    assert!(!cases.is_empty(), "fixture has no cases");
    for case in cases {
        let got = steamrip_core::updates::normalize(case.input.as_deref());
        assert_eq!(got, case.normalized, "normalize {:?}", case.input);
    }
}

#[derive(Deserialize)]
struct IsUpdateCase {
    installed: Option<String>,
    version_key: Option<String>,
    is_update: bool,
}

#[test]
fn is_update_matches_the_original() {
    let cases: Vec<IsUpdateCase> = load("is_update.json");
    assert!(!cases.is_empty(), "fixture has no cases");
    for case in cases {
        let got = steamrip_core::updates::is_update(
            case.installed.as_deref(),
            case.version_key.as_deref(),
        );
        assert_eq!(
            got, case.is_update,
            "is_update {:?} vs {:?}",
            case.installed, case.version_key
        );
    }
}

#[derive(Deserialize)]
struct ProtonCase {
    stored: Option<String>,
    effective: String,
    is_keyword: bool,
}

#[test]
fn proton_selection_matches_the_original() {
    let cases: Vec<ProtonCase> = load("proton_selection.json");
    assert!(!cases.is_empty(), "fixture has no cases");
    for case in cases {
        let stored = case.stored.as_deref();
        assert_eq!(
            steamrip_core::proton::effective(stored),
            case.effective,
            "effective for {:?}",
            case.stored
        );
        assert_eq!(
            steamrip_core::proton::is_keyword(stored),
            case.is_keyword,
            "is_keyword for {:?}",
            case.stored
        );
    }
}

#[derive(Deserialize)]
struct MarkerCase {
    length: i64,
    marker: Option<String>,
    verified: bool,
}

#[test]
fn archive_marker_matches_the_original() {
    let cases: Vec<MarkerCase> = load("archive_marker.json");
    assert!(!cases.is_empty(), "fixture has no cases");
    for case in cases {
        let got = steamrip_core::archive::is_verified(case.length, case.marker.as_deref());
        assert_eq!(got, case.verified, "verified for {:?}", case.marker);
    }
}

#[test]
fn host_capabilities_match_the_original() {
    let raw = std::fs::read_to_string(fixtures_dir().join("host_capabilities.json"))
        .expect("host_capabilities fixture");
    let doc: serde_json::Value = serde_json::from_str(&raw).expect("valid json");
    let cases = doc["cases"].as_array().expect("cases");
    assert!(!cases.is_empty(), "fixture has no cases");
    for case in cases {
        let kind = case["kind"].as_str().expect("kind");
        match kind {
            "edid" => {
                let bytes = hex_bytes(case["input"].as_str().expect("hex input"));
                let got = steamrip_core::host_caps::edid_has_hdr(&bytes);
                assert_eq!(Some(got), case["has_hdr"].as_bool(), "edid");
            }
            "vendor" => {
                let input = case["input"].as_str().expect("vendor input");
                let got = steamrip_core::host_caps::vendor_name(input);
                assert_eq!(
                    Some(got),
                    case["vendors"].as_str().map(str::to_string),
                    "vendor"
                );
            }
            "env" => {
                let input = case["input"].as_str().unwrap_or_default();
                let got: Vec<String> = steamrip_core::host_caps::proton_env_vars(input)
                    .into_iter()
                    .collect();
                let expected: Vec<String> = case["env_vars"]
                    .as_array()
                    .map(|a| {
                        a.iter()
                            .map(|v| v.as_str().unwrap_or_default().to_string())
                            .collect()
                    })
                    .unwrap_or_default();
                assert_eq!(got, expected, "env vars for {input:?}");
            }
            "kscreen" => {
                let input = case["input"].as_str().unwrap_or_default();
                let got = steamrip_core::host_caps::parse_kscreen(input);
                let expected: Vec<steamrip_core::host_caps::DisplayInfo> =
                    serde_json::from_value(case["displays"].clone()).expect("displays");
                assert_eq!(got, expected, "kscreen for {input:?}");
            }
            other => panic!("unknown kind {other}"),
        }
    }
}

#[test]
fn host_displays_match_the_original() {
    let raw = std::fs::read_to_string(fixtures_dir().join("host_displays.json"))
        .expect("host_displays fixture");
    let doc: serde_json::Value = serde_json::from_str(&raw).expect("valid json");
    let cases = doc["cases"].as_array().expect("cases");
    assert!(!cases.is_empty(), "fixture has no cases");
    for case in cases {
        let kind = case["kind"].as_str().expect("kind");
        let input = match case["html_file"].as_str() {
            Some(file) => {
                let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
                    .join("../../../tests/SteamRip.Core.Tests/Fixtures")
                    .join(file);
                std::fs::read_to_string(&path)
                    .unwrap_or_else(|e| panic!("fixture {}: {e}", path.display()))
            }
            None => case["html"].as_str().unwrap_or_default().to_string(),
        };
        let got = match kind {
            "mutter" => steamrip_core::host_caps::parse_mutter(&input),
            "wlr" => steamrip_core::host_caps::parse_wlr_outputs(&input),
            other => panic!("unknown kind {other}"),
        };
        let expected: Vec<steamrip_core::host_caps::DisplayInfo> =
            serde_json::from_value(case["displays"].clone()).expect("displays");
        assert_eq!(got, expected, "{kind} for {input:.40?}");
    }
}

#[derive(Deserialize)]
struct SaveWalkCase {
    name: String,
    depth: i32,
    is_save: bool,
    is_skipped: bool,
}

#[test]
fn save_walk_matches_the_original() {
    let cases: Vec<SaveWalkCase> = load("save_walk.json");
    assert!(!cases.is_empty(), "fixture has no cases");
    for case in cases {
        assert_eq!(
            steamrip_core::save_locator::is_save_dir_name(&case.name),
            case.is_save,
            "is_save for {:?}",
            case.name
        );
        assert_eq!(
            steamrip_core::save_locator::is_skipped_dir(&case.name, case.depth),
            case.is_skipped,
            "is_skipped for {:?} at {}",
            case.name,
            case.depth
        );
    }
}

#[derive(Deserialize)]
struct PercentCase {
    input: String,
    values: Vec<i32>,
}

#[test]
fn percent_values_match_the_original() {
    let cases: Vec<PercentCase> = load("percent_values.json");
    assert!(!cases.is_empty(), "fixture has no cases");
    for case in cases {
        assert_eq!(
            steamrip_core::archive::percent_values(&case.input),
            case.values,
            "percentages for {:?}",
            case.input
        );
    }
}

#[derive(Debug, Deserialize)]
struct ReleaseCase {
    parsed: Option<ReleaseFields>,
}

#[derive(Debug, Deserialize)]
struct ReleaseFields {
    tag: String,
    archive: String,
    archive_name: String,
    checksum: Option<String>,
}

#[test]
fn proton_release_matches_the_original() {
    let raw = std::fs::read_to_string(fixtures_dir().join("proton_release.json")).expect("fixture");
    let doc: serde_json::Value = serde_json::from_str(&raw).expect("json");
    let cases = doc["cases"].as_array().expect("cases");
    let fixture = std::fs::read_to_string(
        PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("../../../tests/SteamRip.Core.Tests/Fixtures/dwproton_release.json"),
    )
    .expect("dwproton fixture");
    let inputs = [
        fixture.as_str(),
        r#"{\"tag_name\":\"v1\",\"assets\":[{\"name\":\"other.zip\",\"browser_download_url\":\"https://x/y.zip\"}]}"#,
        r#"{\"assets\":[]}"#,
    ];
    assert_eq!(cases.len(), inputs.len(), "case count");
    for (case, input) in cases.iter().zip(inputs) {
        let expected: ReleaseCase = serde_json::from_value(case.clone()).expect("case");
        let got = steamrip_core::proton_release::parse_latest(input);
        match (got, expected.parsed) {
            (None, None) => {}
            (Some(release), Some(fields)) => {
                assert_eq!(release.tag, fields.tag);
                assert_eq!(release.archive_url, fields.archive);
                assert_eq!(release.archive_name, fields.archive_name);
                assert_eq!(release.checksum_url, fields.checksum);
            }
            (got, expected) => panic!("release mismatch: {got:?} vs {expected:?}"),
        }
    }
}

#[derive(Deserialize)]
struct ChecksumCase {
    file: String,
    hash: Option<String>,
}

#[test]
fn proton_checksum_matches_the_original() {
    let cases: Vec<ChecksumCase> = load("proton_checksum.json");
    let text = "abc123  dwproton-11.0-16-x86_64.tar.xz\ndef456 *dwproton-other.tar.xz\nshort\nfedcba  dwproton-11.0-16-x86_64.sha512sum\n";
    for case in cases {
        assert_eq!(
            steamrip_core::proton_release::parse_checksum(text, &case.file),
            case.hash,
            "checksum for {:?}",
            case.file
        );
    }
}

#[derive(Deserialize)]
struct TopGamesCase {
    input: String,
    ids: Vec<i64>,
}

#[test]
fn top_games_match_the_original() {
    let cases: Vec<TopGamesCase> = load("top_games.json");
    assert!(!cases.is_empty(), "fixture has no cases");
    for case in cases {
        assert_eq!(
            steamrip_core::catalog::parse_top_games(&case.input),
            case.ids,
            "ids for {:?}",
            case.input
        );
    }
}

#[derive(Deserialize)]
struct MirrorCase {
    input: String,
    mirrors: Vec<steamrip_core::catalog::Mirror>,
}

#[test]
fn mirrors_match_the_original() {
    let cases: Vec<MirrorCase> = load("mirrors.json");
    assert!(!cases.is_empty(), "fixture has no cases");
    for case in cases {
        assert_eq!(
            steamrip_core::catalog::mirrors_from_html(&case.input),
            case.mirrors,
            "mirrors for {:?}",
            case.input
        );
    }
}

#[derive(Deserialize)]
struct GameSizeCase {
    input: String,
    size: Option<String>,
}

#[test]
fn game_size_matches_the_original() {
    let cases: Vec<GameSizeCase> = load("game_size.json");
    assert!(!cases.is_empty(), "fixture has no cases");
    for case in cases {
        assert_eq!(
            steamrip_core::catalog::game_size_text(&case.input),
            case.size,
            "size for {:?}",
            case.input
        );
    }
}

#[derive(Deserialize)]
struct VersionKeyRow {
    name: String,
    key: Vec<i32>,
}

#[derive(Deserialize)]
struct VersionComparison {
    a: String,
    b: String,
    result: i32,
}

#[derive(Deserialize)]
struct VersionsCase {
    keys: Vec<VersionKeyRow>,
    comparisons: Vec<VersionComparison>,
    newest: Option<String>,
}

#[test]
fn proton_versions_match_the_original() {
    let cases: Vec<VersionsCase> = load("proton_versions.json");
    let case = cases.first().expect("versions case");
    for row in &case.keys {
        assert_eq!(
            steamrip_core::proton::version_key(&row.name),
            row.key,
            "key for {}",
            row.name
        );
    }
    for cmp in &case.comparisons {
        let got = steamrip_core::proton::compare_versions(
            &steamrip_core::proton::version_key(&cmp.a),
            &steamrip_core::proton::version_key(&cmp.b),
        );
        assert_eq!(got as i32, cmp.result, "compare {} vs {}", cmp.a, cmp.b);
    }
    let names: Vec<String> = case.keys.iter().map(|r| r.name.clone()).collect();
    assert_eq!(
        steamrip_core::proton::newest_build_name(&names),
        case.newest,
        "newest build"
    );
}

#[derive(Deserialize)]
struct DisplayNameCase {
    input: String,
    name: Option<String>,
}

#[test]
fn proton_display_name_matches_the_original() {
    let cases: Vec<DisplayNameCase> = load("proton_display_name.json");
    for case in cases {
        assert_eq!(
            steamrip_core::proton::display_name(&case.input),
            case.name,
            "display name for {:?}",
            case.input
        );
    }
}

#[derive(Deserialize)]
struct InstallerSafeNameCase {
    input: String,
    safe: String,
}

#[test]
fn installer_safe_name_matches_the_original() {
    let cases: Vec<InstallerSafeNameCase> = load("installer_safe_name.json");
    for case in cases {
        assert_eq!(
            steamrip_core::installer::safe_name(&case.input),
            case.safe,
            "safe name for {:?}",
            case.input
        );
    }
}

#[derive(Deserialize)]
struct AutostartCase {
    path: String,
    entry: String,
}

#[test]
fn autostart_entry_matches_the_original() {
    let cases: Vec<AutostartCase> = load("autostart_entry.json");
    assert!(!cases.is_empty(), "fixture has no cases");
    for case in cases {
        assert_eq!(
            steamrip_core::autostart::desktop_entry(&case.path),
            case.entry,
            "desktop entry for {:?}",
            case.path
        );
    }
}

#[derive(Deserialize)]
struct ProtonLabelCase {
    stored: Option<String>,
    resolved: Option<String>,
    label: String,
    build: Option<String>,
}

#[test]
fn proton_label_matches_the_original() {
    let cases: Vec<ProtonLabelCase> = load("proton_label.json");
    assert!(!cases.is_empty(), "fixture has no cases");
    for case in cases {
        let resolved = case.resolved.clone();
        let label =
            steamrip_core::prefix::proton_label(case.stored.as_deref(), |_| resolved.clone());
        assert_eq!(label, case.label, "label for {:?}", case.stored);

        let resolved = case.resolved.clone();
        let probe = resolved.clone();
        let build = steamrip_core::prefix::resolve_build(
            case.stored.as_deref(),
            |_| resolved.clone(),
            |path| match &probe {
                Some(starts) => path.starts_with(starts.as_str()),
                None => false,
            },
        );
        assert_eq!(build, case.build, "build for {:?}", case.stored);
    }
}

#[derive(Deserialize)]
struct CollapseCase {
    dirs: Vec<String>,
    files: Vec<String>,
    target: Option<String>,
}

#[test]
fn collapse_matches_the_original() {
    let cases: Vec<CollapseCase> = load("collapse.json");
    assert!(!cases.is_empty(), "fixture has no cases");
    for case in cases {
        assert_eq!(
            steamrip_core::installer::collapse_target(&case.dirs, &case.files),
            case.target,
            "collapse for {:?} / {:?}",
            case.dirs,
            case.files
        );
    }
}

#[derive(Deserialize)]
struct CategoriesCase {
    input: String,
    categories: Vec<steamrip_core::catalog::Category>,
}

#[test]
fn categories_match_the_original() {
    let cases: Vec<CategoriesCase> = load("categories.json");
    assert!(!cases.is_empty(), "fixture has no cases");
    let fixture = std::fs::read_to_string(
        PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("../../../tests/SteamRip.Core.Tests/Fixtures/categories.json"),
    )
    .expect("categories fixture");
    for case in cases {
        let input = if case.input == "<fixture>" {
            fixture.clone()
        } else {
            case.input.clone()
        };
        assert_eq!(
            steamrip_core::catalog::parse_categories(&input),
            case.categories,
            "categories for {:?}",
            case.input
        );
    }
}

#[derive(Deserialize)]
struct TorrentRow {
    file_name: Option<String>,
    url: String,
    is_torrent: bool,
}

#[derive(Deserialize)]
struct EngineRow {
    available: bool,
    prefer: bool,
    name: String,
}

#[derive(Deserialize)]
struct TorrentEngineCase {
    torrents: Vec<TorrentRow>,
    #[serde(rename = "engineNames")]
    engine_names: Vec<EngineRow>,
}

#[test]
fn torrent_and_engine_match_the_original() {
    let cases: Vec<TorrentEngineCase> = load("torrent_engine.json");
    let case = cases.first().expect("torrent case");
    for row in &case.torrents {
        assert_eq!(
            steamrip_core::downloader::is_torrent(row.file_name.as_deref(), &row.url),
            row.is_torrent,
            "is_torrent {}",
            row.url
        );
    }
    for row in &case.engine_names {
        assert_eq!(
            steamrip_core::downloader::engine_name(row.available, row.prefer),
            row.name,
            "engine name"
        );
    }
}

#[derive(Deserialize)]
struct RedistSelected {
    file: String,
    args: Vec<String>,
}

#[derive(Deserialize)]
struct RedistCase {
    files: Vec<String>,
    selected: Vec<RedistSelected>,
}

#[test]
fn redist_selection_matches_the_original() {
    let cases: Vec<RedistCase> = load("redist_selection.json");
    assert!(!cases.is_empty(), "fixture has no cases");
    for case in cases {
        let got: Vec<(String, Vec<String>)> = steamrip_core::redist::select_installers(&case.files)
            .into_iter()
            .map(|v| (v.file, v.args))
            .collect();
        let expected: Vec<(String, Vec<String>)> = case
            .selected
            .into_iter()
            .map(|s| (s.file, s.args))
            .collect();
        assert_eq!(got, expected, "selection for {:?}", case.files);
    }
}

#[derive(Deserialize)]
struct TimestampCase {
    input: Option<String>,
    unix: Option<i64>,
}

#[test]
fn timestamps_match_the_original() {
    let cases: Vec<TimestampCase> = load("timestamps.json");
    assert!(!cases.is_empty(), "fixture has no cases");
    for case in cases {
        let got = case
            .input
            .as_deref()
            .and_then(steamrip_core::dates::parse_utc_text);
        assert_eq!(got, case.unix, "unix for {:?}", case.input);
    }
}

#[derive(Deserialize)]
struct UpgradeRow {
    previous: Option<String>,
    current: Option<String>,
    needs_upgrade: bool,
}

#[derive(Deserialize)]
struct AsideRow {
    dir: String,
    path: String,
}

#[derive(Deserialize)]
struct PrefixUpgradeCase {
    upgrades: Vec<UpgradeRow>,
    aside: Vec<AsideRow>,
}

#[test]
fn prefix_upgrade_matches_the_original() {
    let cases: Vec<PrefixUpgradeCase> = load("prefix_upgrade.json");
    let case = cases.first().expect("prefix upgrade case");
    for row in &case.upgrades {
        assert_eq!(
            steamrip_core::prefix::needs_upgrade(row.previous.as_deref(), row.current.as_deref()),
            row.needs_upgrade,
            "upgrade {:?} -> {:?}",
            row.previous,
            row.current
        );
    }
    for row in &case.aside {
        assert_eq!(
            steamrip_core::prefix::aside_path(&row.dir, (2026, 11, 19, 20, 30, 45)),
            row.path,
            "aside for {:?}",
            row.dir
        );
    }
}

#[derive(Deserialize)]
struct OrderCase {
    configured: Vec<String>,
    effective: Vec<String>,
}

fn hoster(name: &str) -> steamrip_core::hoster::Hoster {
    match name {
        "BuzzHeavier" => steamrip_core::hoster::Hoster::BuzzHeavier,
        "FileDitch" => steamrip_core::hoster::Hoster::FileDitch,
        "GoFile" => steamrip_core::hoster::Hoster::GoFile,
        "MegaDb" => steamrip_core::hoster::Hoster::MegaDb,
        "Fichier" => steamrip_core::hoster::Hoster::Fichier,
        "FileCrypt" => steamrip_core::hoster::Hoster::FileCrypt,
        other => panic!("unknown hoster {other}"),
    }
}

#[test]
fn hoster_order_matches_the_original() {
    let cases: Vec<OrderCase> = load("hoster_order.json");
    assert!(!cases.is_empty(), "fixture has no cases");
    for case in cases {
        let configured: Vec<steamrip_core::hoster::Hoster> =
            case.configured.iter().map(|n| hoster(n)).collect();
        let got: Vec<String> = steamrip_core::chain::effective_order(&configured)
            .into_iter()
            .map(|h| format!("{h:?}"))
            .collect();
        assert_eq!(got, case.effective, "order for {:?}", case.configured);
    }
}

#[derive(Deserialize)]
struct DirCase {
    windows: bool,
    app_data: String,
    xdg: Option<String>,
    home: String,
    dir: String,
}

#[derive(Deserialize)]
struct HostersCase {
    names: Vec<String>,
    parsed: Vec<String>,
}

#[derive(Deserialize)]
struct OrderTextCase {
    input: String,
    names: Vec<String>,
}

#[derive(Deserialize)]
struct SettingsCase {
    #[serde(rename = "defaultDirs")]
    default_dirs: Vec<DirCase>,
    hosters: Vec<HostersCase>,
    orders: Vec<OrderTextCase>,
}

#[test]
fn settings_rules_match_the_original() {
    let raw = std::fs::read_to_string(fixtures_dir().join("settings_rules.json"))
        .expect("settings_rules fixture");
    let doc: serde_json::Value = serde_json::from_str(&raw).expect("json");
    let case: SettingsCase =
        serde_json::from_value(doc["cases"][0].clone()).expect("settings case");
    for row in &case.default_dirs {
        assert_eq!(
            steamrip_core::settings::default_dir(
                row.windows,
                &row.app_data,
                row.xdg.as_deref(),
                &row.home
            ),
            row.dir,
            "dir for {:?}",
            row.xdg
        );
    }
    for row in &case.hosters {
        let parsed: Vec<String> = steamrip_core::settings::hosters_from_names(&row.names)
            .into_iter()
            .map(|h| format!("{h:?}"))
            .collect();
        assert_eq!(parsed, row.parsed, "hosters for {:?}", row.names);
    }
    for row in &case.orders {
        assert_eq!(
            steamrip_core::settings::parse_hoster_order(&row.input),
            row.names,
            "order for {:?}",
            row.input
        );
    }
}

#[derive(Debug, Deserialize)]
struct GpuRow {
    vendor: String,
    driver: Option<String>,
    driver_info: Option<String>,
    driver_version: Option<String>,
}

#[derive(Deserialize)]
struct VulkanRow {
    input: String,
    gpu: Option<GpuRow>,
}

#[derive(Debug, Deserialize)]
struct SizePair {
    width: i32,
    height: i32,
}

#[derive(Deserialize)]
struct ScreenRow {
    modes: Vec<String>,
    largest: Option<SizePair>,
}

#[derive(Deserialize)]
struct VulkanCase {
    vulkan: Vec<VulkanRow>,
    screens: Vec<ScreenRow>,
}

#[test]
fn vulkan_and_screen_match_the_original() {
    let raw = std::fs::read_to_string(fixtures_dir().join("vulkan_screen.json"))
        .expect("vulkan_screen fixture");
    let doc: serde_json::Value = serde_json::from_str(&raw).expect("json");
    let case: VulkanCase = serde_json::from_value(doc["cases"][0].clone()).expect("vulkan case");
    for row in &case.vulkan {
        let got = steamrip_core::host_caps::parse_vulkan_info(&row.input);
        match (got, row.gpu.as_ref()) {
            (None, None) => {}
            (Some(gpu), Some(expected)) => {
                assert_eq!(gpu.vendor, expected.vendor, "vendor");
                assert_eq!(gpu.driver, expected.driver, "driver");
                assert_eq!(gpu.driver_info, expected.driver_info, "driver info");
                assert_eq!(
                    gpu.driver_version, expected.driver_version,
                    "driver version"
                );
            }
            (got, expected) => panic!("gpu mismatch: {got:?} vs {expected:?}"),
        }
    }
    for row in &case.screens {
        let expected = row.largest.as_ref().map(|s| (s.width, s.height));
        assert_eq!(
            steamrip_core::host_caps::largest_screen_of(&row.modes),
            expected,
            "largest for {:?}",
            row.modes
        );
    }
}

#[derive(Deserialize)]
struct SavePathsCase {
    users: Vec<String>,
    paths: Vec<String>,
}

#[derive(Deserialize)]
struct GofileLinkCase {
    link: String,
    id: Option<String>,
}

#[derive(Deserialize)]
struct EmulatorGofileCase {
    paths: Vec<SavePathsCase>,
    gofile: Vec<GofileLinkCase>,
}

#[test]
fn emulator_and_gofile_match_the_original() {
    let raw = std::fs::read_to_string(fixtures_dir().join("emulator_gofile.json"))
        .expect("emulator_gofile fixture");
    let doc: serde_json::Value = serde_json::from_str(&raw).expect("json");
    let case: EmulatorGofileCase = serde_json::from_value(doc["cases"][0].clone()).expect("case");
    for row in &case.paths {
        assert_eq!(
            steamrip_core::save_locator::emulator_save_paths(&row.users, 2483190),
            row.paths,
            "paths for {:?}",
            row.users
        );
    }
    for row in &case.gofile {
        assert_eq!(
            steamrip_core::hosters::gofile_content_id(&row.link),
            row.id,
            "id for {:?}",
            row.link
        );
    }
}
