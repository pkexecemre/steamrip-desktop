//! Port of the pure selection rules in SteamRip.Core.Launch.ExeFinder: which `.exe` inside an
//! extracted game folder is the game, best first.
//!
//! The filesystem walk stays on the C# side; this module takes the tree it saw as data, so every
//! directory rule (symlinks, redist/tool folders, wine prefixes, the depth cap) lives here.
//!
//! Three host differences are accepted rather than emulated and do not occur in the recordings:
//! `char.IsLetterOrDigit` is L*/Nd where `char::is_alphanumeric` also counts Nl/No and
//! Other_Alphabetic marks, the C# walk's `Directory.Exists(dir/drive_c)` is case-insensitive on
//! Windows while `drive_c` is matched literally here (Linux semantics), and both separators split
//! a path where .NET splits on the platform's separator only.

use std::collections::HashSet;

/// How deep (in directories) the walk goes; files deeper than this are not candidates.
pub const MAX_DEPTH: i32 = 4;

const HUGE: i64 = 50 << 20;
const TINY: i64 = 300 << 10;

/// Folders that hold redistributables, tools or a wine prefix rather than the game.
pub const NOISE_DIRS: [&str; 13] = [
    "_commonredist",
    "_redist",
    "redist",
    "redistributables",
    "prefix",
    "__macosx",
    "_setup",
    "directx",
    "dotnet",
    "vcredist",
    "engine",
    "support",
    "tools",
];

/// File-name fragments that mark installers, crash handlers and stores rather than the game.
pub const NOISE_NAMES: [&str; 41] = [
    "unins",
    "uninstall",
    "setup",
    "install",
    "redist",
    "vcredist",
    "vc_redist",
    "dxsetup",
    "dxwebsetup",
    "dotnet",
    "oalinst",
    "physx",
    "crashhandler",
    "crashreport",
    "crashreportclient",
    "unitycrashhandler",
    "ue4prereqsetup",
    "uecrash",
    "launcherpatch",
    "cleanup",
    "report",
    "eac",
    "easyanticheat",
    "battleye",
    "beservice",
    "steamservice",
    "steam_api",
    "steamclient",
    "python",
    "wine",
    "7z",
    "unrar",
    "rar",
    "touchup",
    "activat",
    "update",
    "patch",
    "cheat",
    "trainer",
    "config",
    "settings",
];

/// Words that carry no signal in a game or file name.
const STOP_WORDS: [&str; 8] = ["the", "of", "and", "a", "an", "win64", "win32", "exe"];

const TOKEN_SEPARATORS: [char; 11] = [' ', '-', '_', '.', ':', '\'', '’', '(', ')', '[', ']'];

/// One entry the walk saw, relative to the game root: a directory or a file.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Entry {
    pub path: String,
    pub is_dir: bool,
    pub is_link: bool,
    pub size: i64,
    pub depth: i32,
}

impl Entry {
    /// A file at the given walk depth whose length the walk read.
    pub fn file(path: impl Into<String>, size: i64, depth: i32) -> Self {
        Self {
            path: path.into(),
            is_dir: false,
            is_link: false,
            size,
            depth,
        }
    }

    /// A real directory.
    pub fn dir(path: impl Into<String>, depth: i32) -> Self {
        Self {
            path: path.into(),
            is_dir: true,
            is_link: false,
            size: 0,
            depth,
        }
    }

    /// A directory that is a symlink (the walk never descends into one).
    pub fn linked_dir(path: impl Into<String>, depth: i32) -> Self {
        Self {
            path: path.into(),
            is_dir: true,
            is_link: true,
            size: 0,
            depth,
        }
    }
}

/// A plausible game executable and the score that ranked it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Candidate {
    pub relative_path: String,
    pub size: i64,
    pub depth: i32,
    pub score: i32,
}

/// All plausible executables in the snapshot, best first (score descending, then shallower,
/// then bigger; ties keep the walk's order).
pub fn rank(entries: &[Entry], game_name: Option<&str>) -> Vec<Candidate> {
    let name = game_name.unwrap_or("");
    let name_tokens = tokens(name);
    let name_squashed = squash(name);
    let pruned = pruned_directories(entries);
    let mut candidates: Vec<Candidate> = Vec::new();
    for entry in entries {
        if entry.is_dir || entry.depth > MAX_DEPTH {
            continue;
        }
        let parts = segments(&entry.path);
        let Some(&file_name) = parts.last() else {
            continue;
        };
        if under_pruned(&parts, &pruned) || !is_exe(file_name) {
            continue;
        }
        let lower = stem(file_name).to_lowercase();
        if NOISE_NAMES.iter().any(|noise| lower.contains(noise)) {
            continue;
        }
        candidates.push(Candidate {
            relative_path: entry.path.clone(),
            size: entry.size,
            depth: entry.depth,
            score: score(
                &lower,
                &entry.path,
                entry.size,
                entry.depth,
                &name_tokens,
                &name_squashed,
            ),
        });
    }
    candidates.sort_by(|a, b| {
        b.score
            .cmp(&a.score)
            .then(a.depth.cmp(&b.depth))
            .then(b.size.cmp(&a.size))
    });
    candidates
}

/// The single best relative path, or None when nothing qualifies.
pub fn best(entries: &[Entry], game_name: Option<&str>) -> Option<String> {
    rank(entries, game_name)
        .into_iter()
        .next()
        .map(|candidate| candidate.relative_path)
}

fn score(
    lower: &str,
    relative_path: &str,
    size: i64,
    depth: i32,
    name_tokens: &HashSet<String>,
    name_squashed: &str,
) -> i32 {
    let mut score = 0;
    let file_tokens = tokens(lower);
    let matched = name_tokens
        .iter()
        .filter(|token| file_tokens.contains(*token))
        .count();
    score += 10 * matched as i32; // name match is the strongest signal
    let squashed = squash(lower);
    if name_squashed.encode_utf16().count() > 3
        && (squashed.contains(name_squashed)
            || (name_squashed.contains(squashed.as_str()) && lower.encode_utf16().count() > 3))
    {
        score += 10; // "5dchesswithmultiversetimetravel"
    }
    if lower.contains("shipping") {
        score += 8; // UE packaged binary
    }
    if lower.contains("win64") || lower.contains("x64") {
        score += 2;
    }
    if relative_path.to_lowercase().contains("binaries") {
        score += 3; // UE layout
    }
    if lower.contains("launcher") {
        score -= 3; // usually a stub, but sometimes the only way in
    }
    if depth == 0 {
        score += 2; // top-level exe in the collapsed game dir
    }
    if size > HUGE {
        score += 3;
    } else if size < TINY {
        score -= 2; // real games aren't tiny
    }
    score
}

/// Directories the walk must not descend into, as root-relative keys joined with '/'.
fn pruned_directories(entries: &[Entry]) -> HashSet<String> {
    let mut holds_drive_c: HashSet<String> = HashSet::new();
    for entry in entries.iter().filter(|entry| entry.is_dir) {
        let parts = segments(&entry.path);
        if parts.last().is_some_and(|name| *name == "drive_c") {
            holds_drive_c.insert(parent_key(&parts));
        }
    }

    let mut pruned: HashSet<String> = HashSet::new();
    for entry in entries.iter().filter(|entry| entry.is_dir) {
        let parts = segments(&entry.path);
        let Some(&name) = parts.last() else {
            continue;
        };
        let key = parts.join("/");
        if entry.is_link
            || holds_drive_c.contains(&key)
            || NOISE_DIRS.contains(&name.to_lowercase().as_str())
        {
            pruned.insert(key);
        }
    }
    pruned
}

fn under_pruned(parts: &[&str], pruned: &HashSet<String>) -> bool {
    let mut key = String::new();
    for part in &parts[..parts.len().saturating_sub(1)] {
        if !key.is_empty() {
            key.push('/');
        }
        key.push_str(part);
        if pruned.contains(&key) {
            return true;
        }
    }
    false
}

fn parent_key(parts: &[&str]) -> String {
    parts[..parts.len().saturating_sub(1)].join("/")
}

fn segments(path: &str) -> Vec<&str> {
    path.split(['/', '\\'])
        .filter(|part| !part.is_empty())
        .collect()
}

fn is_exe(name: &str) -> bool {
    extension(name).eq_ignore_ascii_case(".exe")
}

/// The .NET rule: the last dot after the last separator, but not a trailing one.
fn dot_index(name: &str) -> Option<usize> {
    let bytes = name.as_bytes();
    for (index, byte) in bytes.iter().enumerate().rev() {
        match *byte {
            b'.' => return (index != bytes.len() - 1).then_some(index),
            b'/' | b'\\' => return None,
            _ => {}
        }
    }
    None
}

fn extension(name: &str) -> &str {
    match dot_index(name) {
        Some(index) => &name[index..],
        None => "",
    }
}

fn stem(name: &str) -> &str {
    match dot_index(name) {
        Some(index) => &name[..index],
        None => name,
    }
}

fn squash(value: &str) -> String {
    value
        .to_lowercase()
        .chars()
        .filter(|c| c.is_alphanumeric())
        .collect()
}

fn tokens(value: &str) -> HashSet<String> {
    value
        .to_lowercase()
        .split(TOKEN_SEPARATORS.as_slice())
        .filter(|token| !token.is_empty())
        .filter(|token| token.encode_utf16().count() > 1 && !STOP_WORDS.contains(token))
        .map(str::to_string)
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn names(candidates: &[Candidate]) -> Vec<&str> {
        candidates
            .iter()
            .map(|candidate| candidate.relative_path.as_str())
            .collect()
    }

    #[test]
    fn shipping_binary_beats_the_plain_exe() {
        let entries = [
            Entry::dir("Game", 1),
            Entry::dir("Game/Binaries", 2),
            Entry::dir("Game/Binaries/Win64", 3),
            Entry::file("Game/Binaries/Win64/Game-Win64-Shipping.exe", HUGE + 1, 3),
            Entry::file("Game/Game.exe", 8_000_000, 1),
        ];
        let ranked = rank(&entries, Some("Game"));
        assert_eq!(
            names(&ranked),
            vec![
                "Game/Binaries/Win64/Game-Win64-Shipping.exe",
                "Game/Game.exe"
            ]
        );
        assert_eq!(ranked[0].score, 36);
    }

    #[test]
    fn noise_names_and_dirs_are_not_candidates() {
        let entries = [
            Entry::dir("Game", 1),
            Entry::file("Game/unins000.exe", 1_000_000, 1),
            Entry::file("Game/Game.exe", 1_000_000, 1),
            Entry::dir("Game/_commonredist", 2),
            Entry::file("Game/_commonredist/setup.exe", 1_000_000, 2),
            Entry::dir("Game/engine", 2),
            Entry::file("Game/engine/engine.exe", 1_000_000, 2),
        ];
        assert_eq!(names(&rank(&entries, None)), vec!["Game/Game.exe"]);
    }

    #[test]
    fn a_folder_holding_drive_c_is_pruned_whole() {
        let entries = [
            Entry::dir("Game", 1),
            Entry::dir("Game/drive_c", 2),
            Entry::file("Game/drive_c/real.exe", 30_000_000, 2),
            Entry::dir("Game/bin", 2),
            Entry::file("Game/bin/real.exe", 30_000_000, 2),
        ];
        assert!(rank(&entries, None).is_empty());
    }

    #[test]
    fn drive_c_under_the_root_is_kept() {
        let entries = [
            Entry::dir("drive_c", 1),
            Entry::file("drive_c/tool.exe", 1_000_000, 1),
        ];
        assert_eq!(names(&rank(&entries, None)), vec!["drive_c/tool.exe"]);
    }

    #[test]
    fn symlinked_directories_are_skipped() {
        let entries = [
            Entry::linked_dir("linked", 1),
            Entry::file("linked/game.exe", 5_000_000, 1),
            Entry::file("real.exe", 1_000_000, 0),
        ];
        assert_eq!(names(&rank(&entries, None)), vec!["real.exe"]);
    }

    #[test]
    fn depth_cap_drops_files_below_the_walk() {
        let entries = [
            Entry::dir("a", 1),
            Entry::dir("a/b", 2),
            Entry::dir("a/b/c", 3),
            Entry::dir("a/b/c/d", 4),
            Entry::file("a/b/c/d/keep.exe", 1_000_000, 4),
            Entry::file("a/b/c/d/e/drop.exe", 1_000_000, 5),
        ];
        assert_eq!(names(&rank(&entries, None)), vec!["a/b/c/d/keep.exe"]);
    }

    #[test]
    fn ordering_is_score_then_depth_then_size() {
        let entries = [
            Entry::file("top.exe", 100_000, 0),
            Entry::file("sub/normal.exe", 1_000_000, 1),
            Entry::file("sub/big.exe", 2_000_000, 1),
        ];
        let ranked = rank(&entries, None);
        assert_eq!(
            names(&ranked),
            vec!["top.exe", "sub/big.exe", "sub/normal.exe"]
        );
    }

    #[test]
    fn name_matching_uses_its_own_tokenizer() {
        let entries = [Entry::file("Lara's Journey.exe", 500_000, 0)];
        // "Lara’s Quest" splits on the typographic apostrophe, so "lara" matches.
        assert_eq!(rank(&entries, Some("Lara’s Quest"))[0].score, 12);
        // The same name with a plain apostrophe in the file name, not in the game name.
        assert_eq!(
            rank(&entries, Some("The Lara’s of Quest"))[0].score,
            12 // "the"/"of" are stop words, "lara" still matches
        );
    }

    #[test]
    fn tiny_files_are_pushed_down_and_huge_ones_up() {
        let entries = [
            Entry::file("tiny.exe", 100_000, 1),
            Entry::file("huge.exe", HUGE + 1, 1),
        ];
        let ranked = rank(&entries, None);
        assert_eq!(ranked[0].relative_path, "huge.exe");
        assert_eq!(ranked[0].score, 3);
        assert_eq!(ranked[1].score, -2);
    }

    #[test]
    fn extension_rule_ignores_non_executables() {
        assert!(is_exe("game.EXE"));
        assert!(is_exe(".exe"));
        assert!(!is_exe("game.exe.bak"));
        assert!(!is_exe("game."));
        assert!(!is_exe("readme.txt"));
        assert_eq!(stem("notes.exe.bak"), "notes.exe");
        assert_eq!(stem("game."), "game.");
    }

    #[test]
    fn squashed_match_only_needs_the_name_in_the_file() {
        let entries = [Entry::file(
            "5dchesswithmultiversetimetravel.exe",
            5_000_000,
            0,
        )];
        let ranked = rank(&entries, Some("5D Chess With Multiverse Time Travel"));
        assert_eq!(names(&ranked), vec!["5dchesswithmultiversetimetravel.exe"]);
        assert_eq!(ranked[0].score, 12);
    }
}
