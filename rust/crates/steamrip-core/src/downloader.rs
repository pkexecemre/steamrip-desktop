//! Port of the pure choices in SteamRip.Core.Downloads.Downloader: which engine runs, and
//! whether a resolved link is a torrent instead of the archive.

/// What a transfer would run: aria2c when it is installed and preferred, else the built-in loop.
pub fn engine_name(aria2_available: bool, prefer_aria2: bool) -> &'static str {
    if aria2_available && prefer_aria2 {
        "aria2c"
    } else {
        "http"
    }
}

/// True when a resolved link points at a .torrent instead of the archive.
pub fn is_torrent(file_name: Option<&str>, url: &str) -> bool {
    file_name
        .map(|name| name.to_ascii_lowercase().ends_with(".torrent"))
        .unwrap_or(false)
        || url.to_ascii_lowercase().ends_with(".torrent")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn prefers_aria2_only_when_both_are_true() {
        assert_eq!(engine_name(true, true), "aria2c");
        assert_eq!(engine_name(true, false), "http");
        assert_eq!(engine_name(false, true), "http");
    }

    #[test]
    fn spots_torrent_links() {
        assert!(is_torrent(None, "https://x/game.torrent"));
        assert!(is_torrent(Some("game.torrent"), "https://x/game.rar"));
        assert!(!is_torrent(Some("game.rar"), "https://x/game.rar"));
    }
}
