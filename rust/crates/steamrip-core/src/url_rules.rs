//! Port of the two URL decisions in SteamRip.Core.Solver.InteractivePage: does a URL point at an
//! archive, and is it the file rather than the site's own page or its abuse form.

use url::Url;

const ARCHIVE_SUFFIXES: [&str; 7] = [
    ".rar",
    ".zip",
    ".7z",
    ".r00",
    ".7z.001",
    ".part1.rar",
    ".part01.rar",
];

/// True when a URL points at an archive, query string ignored. A relative or unparsable string is
/// treated as a path and checked as it is, which is what the C# does when Uri.TryCreate fails.
pub fn looks_like_archive(href: &str) -> bool {
    let path = match Url::parse(href) {
        Ok(url) => url.path().to_string(),
        Err(_) => href.to_string(),
    };
    let lower = path.to_lowercase();
    ARCHIVE_SUFFIXES
        .iter()
        .any(|suffix| lower.ends_with(suffix))
}

/// A link that is actually the file: archives only, not the site's abuse form or its own share page.
pub fn is_download_link(href: &str, page_url: Option<&str>) -> bool {
    if !looks_like_archive(href) {
        return false;
    }
    let Ok(url) = Url::parse(href) else {
        return false;
    };
    let host = url.host_str().unwrap_or_default().to_lowercase();
    if host.starts_with("abuse.") {
        return false;
    }
    if url.path().to_lowercase().contains("/abuse") {
        return false;
    }
    match page_url {
        Some(page) => href != page,
        None => true,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn archives_ignore_the_query_string() {
        assert!(looks_like_archive("https://x/game.rar"));
        assert!(looks_like_archive("https://x/game.rar?token=abc"));
        assert!(looks_like_archive("https://x/game.RAR"));
        assert!(!looks_like_archive("https://x/game.rar/"));
        assert!(!looks_like_archive("https://x/game.zip.001"));
        assert!(looks_like_archive("files/x.zip"));
    }

    #[test]
    fn abuse_pages_are_not_downloads() {
        assert!(!is_download_link("https://abuse.foo/game.rar", None));
        assert!(!is_download_link("https://x/abuse/1.rar", None));
        assert!(!is_download_link(
            "https://x/game.rar",
            Some("https://x/game.rar")
        ));
        assert!(is_download_link(
            "https://x/other.rar",
            Some("https://x/game.rar")
        ));
    }
}
