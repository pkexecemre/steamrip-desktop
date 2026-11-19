//! Parity against the recording of the C# `UpdateService.PickArchive` and of the download
//! path's destination naming.
//!
//! The fixture under `rust/fixtures/pick_archive.json` is written by
//! `tests/SteamRip.Core.Tests/GoldenPickArchiveTests.cs` (run with
//! `SR_GOLDEN_DIR=rust/fixtures`) and holds, per payload, the injected existence/length/archive
//! facts together with the file the original picks, and the archive/torrent names the original
//! gives the fetched files. This test replays every case; a mismatch means the port is wrong,
//! not the fixture.

use serde::Deserialize;
use std::collections::HashMap;
use std::path::PathBuf;
use steamrip_core::installer::{archive_file_name, pick_archive, torrent_file_name};

fn fixtures_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("..")
        .join("fixtures")
}

#[derive(Deserialize)]
struct Fixture {
    pick_archive: Vec<PickCase>,
    names: Vec<NameCase>,
}

#[derive(Deserialize)]
struct PickCase {
    name: String,
    files: Vec<String>,
    probes: Vec<Probe>,
    picked: Option<String>,
}

#[derive(Deserialize)]
struct Probe {
    path: String,
    exists: bool,
    length: i64,
    archive: bool,
}

#[derive(Deserialize)]
struct NameCase {
    name: String,
    file_name: Option<String>,
    slug: String,
    archive: String,
    torrent: String,
}

fn load() -> Fixture {
    let path = fixtures_dir().join("pick_archive.json");
    let raw = std::fs::read_to_string(&path)
        .unwrap_or_else(|e| panic!("missing fixture {}: {e}; run: make golden", path.display()));
    serde_json::from_str(&raw).expect("fixture is valid json")
}

#[test]
fn pick_archive_matches_the_original() {
    let fixture = load();
    assert_eq!(
        fixture.pick_archive.len(),
        11,
        "the fixture replays every recorded payload"
    );

    for case in &fixture.pick_archive {
        let probes: HashMap<&str, &Probe> = case
            .probes
            .iter()
            .map(|probe| (probe.path.as_str(), probe))
            .collect();
        let picked = pick_archive(
            &case.files,
            |path| probes.get(path).is_some_and(|probe| probe.exists),
            |path| probes.get(path).map_or(0, |probe| probe.length),
            |path| probes.get(path).is_some_and(|probe| probe.archive),
        );
        assert_eq!(picked, case.picked, "pick for case {:?}", case.name);
    }
}

#[test]
fn destination_names_match_the_original() {
    let fixture = load();
    assert_eq!(
        fixture.names.len(),
        7,
        "the fixture replays every recorded destination name"
    );
    assert_eq!(
        fixture.pick_archive.len() + fixture.names.len(),
        18,
        "the fixture carries 18 cases in total"
    );

    for case in &fixture.names {
        assert_eq!(
            archive_file_name(case.file_name.as_deref(), &case.slug),
            case.archive,
            "archive name for case {:?}",
            case.name
        );
        assert_eq!(
            torrent_file_name(case.file_name.as_deref(), &case.slug),
            case.torrent,
            "torrent name for case {:?}",
            case.name
        );
    }
}
