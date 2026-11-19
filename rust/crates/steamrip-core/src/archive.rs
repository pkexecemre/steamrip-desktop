//! Port of the pure parts of SteamRip.Core.Install.ArchiveTool: what a file's first bytes
//! say it is, and which volume of a split archive has to be opened first.

use regex::Regex;
use std::path::{Path, PathBuf};
use std::sync::OnceLock;

fn multipart() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| {
        Regex::new(r"(?i)^(?P<base>.+)\.part(?P<n>\d+)\.(?P<ext>rar|7z|zip)$").expect("valid")
    })
}

fn old_rar_part() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| Regex::new(r"(?i)^(?P<base>.+)\.r(?P<n>\d{2})$").expect("valid"))
}

fn zip_part() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| Regex::new(r"(?i)^(?P<base>.+)\.z(?P<n>\d{2})$").expect("valid"))
}

fn numeric_split() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| {
        Regex::new(r"(?i)^(?P<base>.+)\.(?P<ext>7z|zip|rar)\.(?P<n>\d{3})$").expect("valid")
    })
}

/// The first volume of a split archive, given any volume: Foo.part03.rar -> Foo.part01.rar,
/// Foo.r05 -> Foo.rar, Foo.7z.004 -> Foo.7z.001. Both unrar and 7z open a set from its first
/// volume. The existence check is injected so the rules stay testable without a disk.
pub fn first_part(archive: &Path, exists: impl Fn(&Path) -> bool) -> PathBuf {
    let dir = archive.parent().unwrap_or_else(|| Path::new(""));
    let name = archive
        .file_name()
        .map(|n| n.to_string_lossy().into_owned())
        .unwrap_or_default();

    if let Some(caps) = multipart().captures(&name) {
        let digits = &caps["n"];
        match digits.parse::<u32>() {
            Ok(n) if n <= 1 => return archive.to_path_buf(),
            Ok(_) => {}
            Err(_) => return archive.to_path_buf(),
        }
        let first = dir.join(format!(
            "{}.part{}.{}",
            &caps["base"],
            pad_left_one(digits.len()),
            &caps["ext"]
        ));
        return if exists(&first) {
            first
        } else {
            archive.to_path_buf()
        };
    }

    if let Some(caps) = old_rar_part().captures(&name) {
        let rar = dir.join(format!("{}.rar", &caps["base"]));
        return if exists(&rar) {
            rar
        } else {
            archive.to_path_buf()
        };
    }

    if let Some(caps) = zip_part().captures(&name) {
        let target = dir.join(format!("{}.zip", &caps["base"]));
        return if exists(&target) {
            target
        } else {
            archive.to_path_buf()
        };
    }

    if let Some(caps) = numeric_split().captures(&name) {
        let over_one = caps["n"].parse::<u32>().map(|n| n > 1).unwrap_or(false);
        if over_one {
            let target = dir.join(format!("{}.{}.001", &caps["base"], &caps["ext"]));
            return if exists(&target) {
                target
            } else {
                archive.to_path_buf()
            };
        }
    }

    archive.to_path_buf()
}

fn pad_left_one(width: usize) -> String {
    // .NET's "1".PadLeft(width, '0'): 01, 001, 0001.
    let mut s = "0".repeat(width.saturating_sub(1));
    s.push('1');
    s
}

/// What the first bytes say a file is: rar, 7z, zip, torrent, html, else None.
pub fn sniff(head: &[u8]) -> Option<&'static str> {
    if head.len() < 2 {
        return None;
    }
    if head.len() >= 4 && head[0] == b'R' && head[1] == b'a' && head[2] == b'r' && head[3] == b'!' {
        return Some("rar");
    }
    if head.len() >= 6 && head[0] == 0x37 && head[1] == 0x7A && head[2] == 0xBC {
        return Some("7z");
    }
    if head[0] == b'P' && head[1] == b'K' {
        return Some("zip");
    }
    if head.len() >= 3 && head[0] == b'd' && head[1] == b'8' && head[2] == b':' {
        return Some("torrent");
    }
    if head.len() >= 5 && (head[0] == b'<' || (head[0] == 0xEF && head[1] == 0xBB)) {
        return Some("html");
    }
    None
}

/// A file an archive tool can open.
pub fn looks_like_archive(head: &[u8]) -> bool {
    matches!(sniff(head), Some("rar" | "7z" | "zip"))
}

/// A BitTorrent metadata file, which some hosters serve instead of the archive.
pub fn is_torrent(head: &[u8], path: &str) -> bool {
    sniff(head) == Some("torrent") || path.to_ascii_lowercase().ends_with(".torrent")
}

/// True when the archive was marked and still has the size it was marked at.
/// A missing marker is never verified; the marker's first line carries the size.
pub fn is_verified(archive_length: i64, marker_text: Option<&str>) -> bool {
    is_verified_for(archive_length, marker_text, None)
}

/// The size line and the version line, both checked against what the caller expects. A None
/// version accepts any marker, which is how markers written by an older release stay usable.
pub fn is_verified_for(
    archive_length: i64,
    marker_text: Option<&str>,
    version_key: Option<&str>,
) -> bool {
    let Some(text) = marker_text else {
        return false;
    };
    let size_ok = text.split('\n').next().unwrap_or_default().trim() == archive_length.to_string();
    size_ok && version_matches(marker_text, version_key)
}

/// True when the marker's third line is the release being installed; None accepts any marker.
pub fn version_matches(marker_text: Option<&str>, version_key: Option<&str>) -> bool {
    let Some(version) = version_key else {
        return true;
    };
    let recorded = marker_text
        .and_then(|text| text.split('\n').nth(2))
        .unwrap_or_default()
        .trim();
    !recorded.is_empty() && recorded.eq_ignore_ascii_case(version)
}

fn percent_regex() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| Regex::new(r"(\d{1,3})%").expect("valid"))
}

/// Every percentage the archive tool printed in a chunk, in order.
pub fn percent_values(chunk: &str) -> Vec<i32> {
    percent_regex()
        .captures_iter(chunk)
        .filter_map(|c| c[1].parse::<i32>().ok())
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sniffs_the_known_magic() {
        assert_eq!(sniff(b"Rar!\x1a\x07"), Some("rar"));
        assert_eq!(sniff(b"7z\xbc\xaf\x27\x1c"), Some("7z"));
        assert_eq!(sniff(b"PK\x03\x04"), Some("zip"));
        assert_eq!(sniff(b"d8:announce"), Some("torrent"));
        assert_eq!(sniff(b"<html>"), Some("html"));
        // three bytes only: the html rule needs five, same as the C# original.
        assert_eq!(sniff(b"\xef\xbb\xbf"), None);
        assert_eq!(sniff(b"\x00\x01"), None);
        assert_eq!(sniff(b""), None);
    }

    #[test]
    fn reads_percentages_in_order() {
        assert_eq!(percent_values("100% 101% 7%"), vec![100, 101, 7]);
        assert!(percent_values("no numbers").is_empty());
    }

    #[test]
    fn marker_must_match_the_size() {
        assert!(is_verified(100, Some("100\n2026-11-19T00:00:00Z")));
        assert!(!is_verified(100, Some("99\n2026-11-19T00:00:00Z")));
        assert!(!is_verified(100, None));
    }

    #[test]
    fn finds_the_first_volume() {
        let exists = |p: &Path| p.to_string_lossy().ends_with("Foo.part01.rar");
        assert_eq!(
            first_part(Path::new("games/Foo.part03.rar"), exists),
            PathBuf::from("games/Foo.part01.rar")
        );
        assert_eq!(
            first_part(Path::new("games/Foo.part03.rar"), |_| false),
            PathBuf::from("games/Foo.part03.rar")
        );
    }
}
