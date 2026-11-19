//! Port of SteamRip.Core.Hosters.HostPages: pure parsers for hoster landing pages, plus the
//! GoFile website token. No network here, so a saved page is all a test needs.

use base64::engine::general_purpose::STANDARD as BASE64;
use base64::Engine as _;
use regex::Regex;
use sha2::{Digest, Sha256};
use std::sync::OnceLock;

fn re(cell: &'static OnceLock<Regex>, pattern: &str) -> &'static Regex {
    cell.get_or_init(|| Regex::new(pattern).expect("valid hoster regex"))
}

fn fileditch_anchor() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    re(&RE, r"<a\b[^>]*\bbtn-main\b[^>]*>")
}

fn href() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    re(&RE, r#"href="(https://[^"]+)"#)
}

fn title() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    re(&RE, r"<title>([^<]*)</title>")
}

fn decode(value: &str) -> String {
    html_escape::decode_html_entities(value).into_owned()
}

fn page_title(html: &str) -> Option<String> {
    title()
        .captures(html)
        .map(|c| decode(&c[1]).trim().to_string())
        .filter(|t| !t.is_empty())
}

/// The signed CDN url, its unix expiry (from the base64 token) and the file name on a FileDitch
/// page that already passed the browser check.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FileDitchPage {
    pub url: String,
    pub expires: Option<i64>,
    pub file_name: Option<String>,
}

pub fn parse_file_ditch(html: &str) -> Result<FileDitchPage, String> {
    let anchor = fileditch_anchor().find(html);
    let matched = anchor.and_then(|a| href().captures(a.as_str()));
    let Some(matched) = matched else {
        if html.contains("Verifying your browser") {
            return Err(
                "FileDitch: page is still the browser-verification interstitial (needs a real browser solve)"
                    .to_string(),
            );
        }
        return Err("FileDitch: no btn-main download link in page".to_string());
    };
    let url = decode(&matched[1]);
    let expires = expiry_from_url(&url);
    Ok(FileDitchPage {
        url,
        expires,
        file_name: page_title(html),
    })
}

fn expiry_from_url(url: &str) -> Option<i64> {
    let token = url.rsplit('/').next()?;
    let padded = match token.len() % 4 {
        0 => token.to_string(),
        rest => format!("{token}{}", "=".repeat(4 - rest)),
    };
    let decoded = BASE64.decode(padded).ok()?;
    let text = String::from_utf8_lossy(&decoded);
    text.split(':').next()?.parse::<i64>().ok()
}

fn bzzhr_hx() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    re(&RE, r#"hx-get="(/[^"]+/download\?t=[^"&]+)"#)
}

fn md5_of_32() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    re(&RE, r"MD5:\s*(?:<[^>]*>\s*)*([a-fA-F0-9]{32})")
}

fn sha256_of_64() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    re(&RE, r"SHA-256:\s*(?:<[^>]*>\s*)*([a-fA-F0-9]{64})")
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BzzhrPage {
    pub hx_path: String,
    pub md5: Option<String>,
    pub sha256: Option<String>,
    pub file_name: Option<String>,
}

pub fn parse_bzzhr(html: &str) -> Result<BzzhrPage, String> {
    let Some(matched) = bzzhr_hx().captures(html) else {
        return Err("BuzzHeavier: no hx-get download path in page".to_string());
    };
    Ok(BzzhrPage {
        hx_path: decode(&matched[1]),
        md5: md5_of_32().captures(html).map(|c| c[1].to_lowercase()),
        sha256: sha256_of_64().captures(html).map(|c| c[1].to_lowercase()),
        file_name: page_title(html),
    })
}

fn megadb_final() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    re(&RE, r"var finalDownloadUrl\s*=\s*'([^']+)'")
}

fn megadb_seconds() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    re(&RE, r"var seconds\s*=\s*(\d+)")
}

fn megadb_hash() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    re(&RE, r"MD5:\s*(?:<[^>]*>\s*)*([a-fA-F0-9]{32,64})")
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MegaDbPage {
    pub pt_url: String,
    pub wait_seconds: u32,
    pub hash: Option<String>,
    pub file_name: Option<String>,
}

pub fn parse_megadb(html: &str) -> Result<MegaDbPage, String> {
    let Some(matched) = megadb_final().captures(html) else {
        return Err("MegaDB: no finalDownloadUrl in page".to_string());
    };
    let wait = megadb_seconds()
        .captures(html)
        .and_then(|c| c[1].parse::<u32>().ok())
        .unwrap_or(10);
    let file_name = page_title(html).map(|t| t.replace(" - MegaDB", "").trim().to_string());
    Ok(MegaDbPage {
        pt_url: decode(&matched[1]),
        wait_seconds: wait,
        hash: megadb_hash().captures(html).map(|c| c[1].to_lowercase()),
        file_name,
    })
}

/// GoFile's per-four-hour website token: sha256 of UA::locale::account::slot::salt, lower hex.
pub fn gofile_website_token(account_token: &str, unix_seconds: i64) -> String {
    const USER_AGENT: &str = "Mozilla/5.0";
    const SALT: &str = "12af056dacea0b";
    let slot = unix_seconds / 14400;
    let digest =
        Sha256::digest(format!("{USER_AGENT}::en-US::{account_token}::{slot}::{SALT}").as_bytes());
    digest.iter().map(|b| format!("{b:02x}")).collect()
}

fn gofile_content_id_regex() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    re(&RE, r"gofile\.io/d/([A-Za-z0-9]+)")
}

/// The share id in a gofile.io/d/<id> link, or None when the link is not one.
pub fn gofile_content_id(share_url: &str) -> Option<String> {
    gofile_content_id_regex()
        .captures(share_url)
        .map(|c| c[1].to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn fileditch_without_a_button_is_an_error() {
        let err = parse_file_ditch("<html></html>").unwrap_err();
        assert_eq!(err, "FileDitch: no btn-main download link in page");
    }

    #[test]
    fn token_is_stable_within_a_four_hour_slot() {
        let a = gofile_website_token("tok", 14400 * 1000);
        let b = gofile_website_token("tok", 14400 * 1000 + 3 * 3600);
        let c = gofile_website_token("tok", 14400 * 1000 + 4 * 3600);
        assert_eq!(a, b);
        assert_ne!(a, c);
        assert_eq!(a.len(), 64);
    }
}
