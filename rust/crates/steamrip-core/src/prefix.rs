//! Port of the pure path/naming helpers on SteamRip.Core.Launch.PrefixManager.

use std::path::{Path, PathBuf};

pub const SIDECAR_NAME: &str = ".steamrip-prefix.json";
pub const LOCK_NAME: &str = ".steamrip-prefix.lock";

/// Default root: a sibling of the game folders, not inside them.
pub fn default_root(library_root: &Path) -> PathBuf {
    library_root.join(".prefixes")
}

/// Where prefixes live: the configured root when it says something, else the library default.
pub fn root(library_root: &Path, configured: Option<&str>) -> PathBuf {
    match configured.map(str::trim) {
        Some(s) if !s.is_empty() => PathBuf::from(s),
        _ => default_root(library_root),
    }
}

/// One game's prefix directory.
pub fn dir(prefix_root: &Path, slug: &str) -> PathBuf {
    prefix_root.join(safe_name(slug))
}

/// A slug that is safe as a single path segment (a post can be titled Foo/Bar).
pub fn safe_name(slug: &str) -> String {
    let mapped: String = slug
        .trim()
        .chars()
        .map(|c| {
            if c.is_alphanumeric() || matches!(c, '-' | '_' | '.') {
                c
            } else {
                '-'
            }
        })
        .collect();
    let trimmed = mapped.trim_matches(|c| c == '.' || c == '-');
    if trimmed.is_empty() {
        "game".to_string()
    } else {
        trimmed.to_string()
    }
}

/// The umu GAMEID: Steam's id when the catalog knows it, else a stable id of our own.
pub fn game_id(steam_app_id: Option<i64>, slug: Option<&str>) -> String {
    match steam_app_id {
        Some(id) => format!("umu-{id}"),
        None => match slug {
            Some(s) if !s.trim().is_empty() => {
                format!("umu-steamrip-{}", safe_name(s).to_lowercase())
            }
            _ => "umu-default".to_string(),
        },
    }
}

/// Human size for the prefix card: 1536 -> "2 KB", 1.25 GiB -> "1.3 GB".
///
/// .NET formats with round-half-away-from-zero, Rust's formatter rounds half to even, so the
/// value is rounded before it is formatted.
pub fn format_size(bytes: i64) -> String {
    const KB: i64 = 1 << 10;
    const MB: i64 = 1 << 20;
    const GB: i64 = 1 << 30;
    if bytes >= GB {
        format!("{:.1} GB", round_one_decimal(bytes as f64 / GB as f64))
    } else if bytes >= MB {
        format!("{:.0} MB", (bytes as f64 / MB as f64).round())
    } else if bytes >= KB {
        format!("{:.0} KB", (bytes as f64 / KB as f64).round())
    } else {
        format!("{bytes} B")
    }
}

fn round_one_decimal(value: f64) -> f64 {
    (value * 10.0).round() / 10.0
}

/// A short name for a Proton setting: the build directory, or a plain label for a keyword that
/// is not on disk.
pub fn proton_label(
    proton_path: Option<&str>,
    resolve_keyword: impl Fn(&str) -> Option<String>,
) -> String {
    let Some(stored) = proton_path else {
        return "umu default".to_string();
    };
    if stored.trim().is_empty() {
        return "umu default".to_string();
    }
    let resolved = if crate::proton::is_keyword(Some(stored)) {
        resolve_keyword(stored)
    } else {
        Some(stored.to_string())
    };
    match resolved {
        None => stored.to_string(),
        Some(dir) => dir
            .trim_end_matches('/')
            .rsplit('/')
            .next()
            .unwrap_or_default()
            .to_string(),
    }
}

/// The resolved build directory for a Proton setting, when the build is on disk.
pub fn resolve_build(
    proton_path: Option<&str>,
    resolve_keyword: impl Fn(&str) -> Option<String>,
    file_exists: impl Fn(&str) -> bool,
) -> Option<String> {
    let stored = proton_path?;
    if stored.trim().is_empty() {
        return None;
    }
    let dir = if crate::proton::is_keyword(Some(stored)) {
        resolve_keyword(stored)
    } else {
        Some(stored.to_string())
    }?;
    let probe = format!("{}/proton", dir.trim_end_matches('/'));
    file_exists(&probe).then_some(dir)
}

/// True when the prefix was built by a different Proton: the build directory name changed.
pub fn needs_upgrade(previous_build: Option<&str>, current_build: Option<&str>) -> bool {
    let (Some(previous), Some(current)) = (previous_build, current_build) else {
        return false;
    };
    build_name(previous) != build_name(current)
}

fn build_name(build: &str) -> String {
    build
        .trim_end_matches('/')
        .rsplit('/')
        .next()
        .unwrap_or_default()
        .to_lowercase()
}

/// Where a reset prefix moves to: sibling with a UTC stamp, so two resets never collide.
pub fn aside_path(prefix_dir: &str, utc: (i32, u32, u32, u32, u32, u32)) -> String {
    let (year, month, day, hour, minute, second) = utc;
    let stamp = format!("{year:04}{month:02}{day:02}-{hour:02}{minute:02}{second:02}");
    format!("{}.bak-{stamp}", prefix_dir.trim_end_matches('/'))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn keeps_slugs_to_one_path_segment() {
        assert_eq!(safe_name("Foo/Bar"), "Foo-Bar");
        assert_eq!(safe_name("Game: Deluxe"), "Game--Deluxe");
        assert_eq!(safe_name("..."), "game");
        assert_eq!(safe_name("ÄÖÜ ç"), "ÄÖÜ-ç");
    }

    #[test]
    fn builds_stable_game_ids() {
        assert_eq!(game_id(Some(2483190), Some("forza")), "umu-2483190");
        assert_eq!(game_id(None, Some("My Game!")), "umu-steamrip-my-game");
        assert_eq!(game_id(None, None), "umu-default");
        assert_eq!(game_id(None, Some("   ")), "umu-default");
    }

    #[test]
    fn formats_sizes_like_the_original() {
        assert_eq!(format_size(0), "0 B");
        assert_eq!(format_size(1536), "2 KB");
        assert_eq!(format_size(1342177280), "1.3 GB");
        assert_eq!(format_size(1073741824), "1.0 GB");
    }
}
