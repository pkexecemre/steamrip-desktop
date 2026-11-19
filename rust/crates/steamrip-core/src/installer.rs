//! Port of the pure naming rule in SteamRip.Core.Install.Installer.

/// A name safe to use as one path segment: invalid file-name characters and spaces become
/// underscores, and a blank result falls back to "unknown".
pub fn safe_name(value: &str) -> String {
    let cleaned: String = value
        .chars()
        .map(|c| {
            if c == '/' || c == '\0' || c == ' ' {
                '_'
            } else {
                c
            }
        })
        .collect();
    if cleaned.trim().is_empty() {
        "unknown".to_string()
    } else {
        cleaned
    }
}

/// Directories that are redist/content noise rather than the game itself.
pub const NOISE_DIRS: [&str; 5] = ["_commonredist", "_redist", "redist", "__macosx", "_setup"];

/// The last path segment, the way .NET's Path.GetFileName reports it: a path ending in a
/// separator has an empty name.
fn file_name(path: &str) -> String {
    match path.rsplit_once('/') {
        Some((_, name)) => name.to_string(),
        None => path.to_string(),
    }
}

/// The name the fetched archive is written as inside the slug directory. The guard is .NET's
/// `Path.HasExtension`, which is true for a dotted name like `Game 1.2`: such a name is kept
/// as it is and never gets a `.rar` appended (so the installer's unrar-only branch never sees
/// it). The quirk is recorded, not fixed.
pub fn archive_file_name(requested: Option<&str>, slug: &str) -> String {
    let name = match requested {
        Some(name) => safe_name(name),
        None => safe_name(&format!("{slug}.rar")),
    };
    if has_extension(&name) {
        name
    } else {
        name + ".rar"
    }
}

/// The name the fetched `.torrent` metadata is written as: only the last segment of the
/// hoster's file name is used, and the extension is enforced case-insensitively.
pub fn torrent_file_name(requested: Option<&str>, slug: &str) -> String {
    let source = match requested {
        Some(name) => name.to_string(),
        None => format!("{slug}.torrent"),
    };
    let name = safe_name(&file_name(&source));
    if name.to_ascii_lowercase().ends_with(".torrent") {
        name
    } else {
        name + ".torrent"
    }
}

/// .NET's `Path.HasExtension` on Unix: a dot in the last segment that is not its final
/// character. A trailing dot, and a dot before the last separator, do not count.
fn has_extension(path: &str) -> bool {
    let chars: Vec<char> = path.chars().collect();
    for (index, ch) in chars.iter().enumerate().rev() {
        if *ch == '.' {
            return index != chars.len() - 1;
        }
        if *ch == '/' {
            break;
        }
    }
    false
}

/// The archive among a torrent payload: split sets resolve to their first volume, and among the
/// usable candidates the biggest wins (ties keep input order, as the original's stable sort
/// did). Existence, length and archive sniffing are injected, so the decision stays pure.
pub fn pick_archive(
    files: &[String],
    exists: impl Fn(&str) -> bool,
    length: impl Fn(&str) -> i64,
    looks_like_archive: impl Fn(&str) -> bool,
) -> Option<String> {
    let mut best: Option<(String, i64)> = None;
    for file in files {
        if !exists(file) || !looks_like_archive(file) {
            continue;
        }
        let resolved = crate::archive::first_part(std::path::Path::new(file), |path| {
            exists(&path.to_string_lossy())
        });
        let resolved = resolved.to_string_lossy().into_owned();
        let size = length(&resolved);
        match &best {
            Some((_, best_size)) if *best_size >= size => {}
            _ => best = Some((resolved, size)),
        }
    }
    best.map(|(archive, _)| archive)
}

/// The folder the staging directory should collapse into, or None to keep it as it is:
/// exactly one real folder and no loose files. Redist noise folders do not count.
pub fn collapse_target(directories: &[String], files: &[String]) -> Option<String> {
    let real: Vec<String> = directories
        .iter()
        .map(|d| file_name(d))
        .filter(|name| !NOISE_DIRS.contains(&name.to_lowercase().as_str()))
        .collect();
    if files.is_empty() && real.len() == 1 {
        Some(real[0].clone())
    } else {
        None
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn replaces_spaces_and_separators() {
        assert_eq!(safe_name("a b"), "a_b");
        assert_eq!(safe_name("a/b"), "a_b");
        assert_eq!(safe_name("Çok Özel"), "Çok_Özel");
        assert_eq!(safe_name(""), "unknown");
    }

    fn probe(
        files: &[&str],
        sizes: &[(&str, i64)],
        archives: &[&str],
        existing: &[&str],
    ) -> Option<String> {
        let files: Vec<String> = files.iter().map(|file| file.to_string()).collect();
        pick_archive(
            &files,
            |path| existing.contains(&path),
            |path| {
                sizes
                    .iter()
                    .find(|(candidate, _)| *candidate == path)
                    .map_or(0, |(_, size)| *size)
            },
            |path| archives.contains(&path),
        )
    }

    #[test]
    fn picks_the_biggest_archive() {
        let files = ["dl/Game.rar", "dl/Game.Extra.rar"];
        let sizes = [("dl/Game.rar", 500), ("dl/Game.Extra.rar", 900)];
        let archives = ["dl/Game.rar", "dl/Game.Extra.rar"];
        assert_eq!(
            probe(&files, &sizes, &archives, &files),
            Some("dl/Game.Extra.rar".to_string())
        );
    }

    #[test]
    fn split_set_resolves_to_its_first_volume() {
        let files = ["dl/Game.part02.rar", "dl/Game.part03.rar"];
        let sizes = [
            ("dl/Game.part02.rar", 900),
            ("dl/Game.part03.rar", 800),
            ("dl/Game.part01.rar", 100),
        ];
        let archives = [
            "dl/Game.part02.rar",
            "dl/Game.part03.rar",
            "dl/Game.part01.rar",
        ];
        let existing = [
            "dl/Game.part02.rar",
            "dl/Game.part03.rar",
            "dl/Game.part01.rar",
        ];
        assert_eq!(
            probe(&files, &sizes, &archives, &existing),
            Some("dl/Game.part01.rar".to_string())
        );
    }

    #[test]
    fn keeps_the_volume_when_the_first_part_is_missing() {
        let files = ["dl/Game.part02.rar"];
        let sizes = [("dl/Game.part02.rar", 900), ("dl/Game.part01.rar", 0)];
        let archives = ["dl/Game.part02.rar", "dl/Game.part01.rar"];
        assert_eq!(
            probe(&files, &sizes, &archives, &files),
            Some("dl/Game.part02.rar".to_string())
        );
    }

    #[test]
    fn skips_missing_files_and_non_archives() {
        let files = ["dl/gone.rar", "dl/index.html", "dl/Game.rar"];
        let sizes = [
            ("dl/gone.rar", 900),
            ("dl/index.html", 9_000),
            ("dl/Game.rar", 300),
        ];
        let archives = ["dl/gone.rar", "dl/Game.rar"];
        let existing = ["dl/index.html", "dl/Game.rar"];
        assert_eq!(
            probe(&files, &sizes, &archives, &existing),
            Some("dl/Game.rar".to_string())
        );
    }

    #[test]
    fn nothing_usable_picks_nothing() {
        let files = ["dl/Game.torrent", "dl/Game.rar.part", "dl/gone.rar"];
        let sizes = [
            ("dl/Game.torrent", 80),
            ("dl/Game.rar.part", 4_096),
            ("dl/gone.rar", 900),
        ];
        let archives = ["dl/gone.rar"];
        let existing = ["dl/Game.torrent", "dl/Game.rar.part"];
        assert_eq!(probe(&files, &sizes, &archives, &existing), None);
    }

    #[test]
    fn ties_keep_input_order() {
        let files = ["dl/b.rar", "dl/a.rar"];
        let sizes = [("dl/b.rar", 100), ("dl/a.rar", 100)];
        assert_eq!(
            probe(&files, &sizes, &files, &files),
            Some("dl/b.rar".to_string())
        );
    }

    #[test]
    fn archive_name_keeps_dotted_names_and_appends_otherwise() {
        assert_eq!(archive_file_name(Some("Game 1.2"), "game"), "Game_1.2");
        assert_eq!(archive_file_name(Some("Game"), "game"), "Game.rar");
        assert_eq!(archive_file_name(Some("Game."), "game"), "Game..rar");
        assert_eq!(archive_file_name(None, "some-game"), "some-game.rar");
    }

    #[test]
    fn torrent_name_uses_the_last_segment_and_enforces_the_extension() {
        assert_eq!(
            torrent_file_name(Some("Game 1.2"), "game"),
            "Game_1.2.torrent"
        );
        assert_eq!(
            torrent_file_name(Some("My Games/Final Cut"), "game"),
            "Final_Cut.torrent"
        );
        assert_eq!(
            torrent_file_name(Some("Game.TORRENT"), "game"),
            "Game.TORRENT"
        );
        assert_eq!(torrent_file_name(None, "some-game"), "some-game.torrent");
    }
}
