//! Port of SteamRip.Core.Catalog.HosterDetector: which hoster a mirror url belongs to.

use serde::{Deserialize, Serialize};
use url::Url;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum Hoster {
    BuzzHeavier,
    FileDitch,
    GoFile,
    MegaDb,
    Fichier,
    FileCrypt,
    Unknown,
}

pub fn normalize(url: &str) -> String {
    if let Some(rest) = url.strip_prefix("//") {
        format!("https://{rest}")
    } else {
        url.to_string()
    }
}

pub fn detect(url: &str) -> Hoster {
    let candidate = normalize(url);
    let Ok(parsed) = Url::parse(&candidate) else {
        return Hoster::Unknown;
    };
    let host = parsed.host_str().unwrap_or_default().to_lowercase();
    if host.contains("bzzhr") || host.contains("buzzheavier") || host.contains("fuckingfast") {
        Hoster::BuzzHeavier
    } else if host.contains("fileditch") {
        Hoster::FileDitch
    } else if host.contains("gofile.io") {
        Hoster::GoFile
    } else if host.contains("megadb") {
        Hoster::MegaDb
    } else if host.contains("1fichier") {
        Hoster::Fichier
    } else if host.contains("filecrypt") {
        Hoster::FileCrypt
    } else {
        Hoster::Unknown
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn detects_the_known_hosts() {
        assert_eq!(detect("https://fileditch.com/f/x.rar"), Hoster::FileDitch);
        assert_eq!(detect("//bzzhr.co/abc"), Hoster::BuzzHeavier);
        assert_eq!(detect("https://gofile.io/d/abc"), Hoster::GoFile);
        assert_eq!(detect("https://unknown.example/x"), Hoster::Unknown);
        assert_eq!(detect("not a url"), Hoster::Unknown);
    }

    #[test]
    fn normalizes_protocol_relative_urls() {
        assert_eq!(normalize("//x/y"), "https://x/y");
        assert_eq!(normalize("https://x/y"), "https://x/y");
    }
}
