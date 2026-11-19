//! Port of the pure parsers in SteamRip.Core.Launch.ProtonDownloader: picking the x86_64 tarball
//! out of a GitHub release payload, and reading its sha512sum line.

use serde::Serialize;

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct ProtonRelease {
    pub tag: String,
    pub archive_url: String,
    pub archive_name: String,
    pub checksum_url: Option<String>,
}

/// GitHub "latest release" JSON -> the x86_64 tarball and its checksum file.
pub fn parse_latest(json: &str) -> Option<ProtonRelease> {
    let value: serde_json::Value = serde_json::from_str(json).ok()?;
    let tag = value.get("tag_name")?.as_str()?.to_string();
    let mut archive: Option<(String, String)> = None;
    let mut checksum: Option<String> = None;
    for asset in value.get("assets")?.as_array()? {
        let name = asset
            .get("name")
            .and_then(|n| n.as_str())
            .unwrap_or_default();
        let url = asset
            .get("browser_download_url")
            .and_then(|u| u.as_str())
            .unwrap_or_default();
        if name.ends_with("-x86_64.tar.xz") {
            archive = Some((url.to_string(), name.to_string()));
        } else if name.ends_with("-x86_64.sha512sum") {
            checksum = Some(url.to_string());
        }
    }
    let (archive_url, archive_name) = archive?;
    Some(ProtonRelease {
        tag,
        archive_url,
        archive_name,
        checksum_url: checksum,
    })
}

/// sha512sum output: <hex>  <file> per line; the hash for one file name, when listed.
pub fn parse_checksum(text: &str, file_name: &str) -> Option<String> {
    for line in text.split('\n') {
        let parts: Vec<&str> = line.split_whitespace().collect();
        if parts.len() >= 2 && parts[parts.len() - 1].trim_start_matches('*') == file_name {
            return Some(parts[0].to_string());
        }
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn prefers_the_x86_64_assets() {
        let json = r#"{"tag_name":"v1","assets":[{"name":"dw-x86_64.tar.xz","browser_download_url":"https://a"},{"name":"dw-x86_64.sha512sum","browser_download_url":"https://b"}]}"#;
        let release = parse_latest(json).expect("release");
        assert_eq!(release.tag, "v1");
        assert_eq!(release.archive_url, "https://a");
        assert_eq!(release.checksum_url.as_deref(), Some("https://b"));
    }

    #[test]
    fn reads_starred_checksum_lines() {
        let text = "abc  one.tar.xz\ndef *two.tar.xz\n";
        assert_eq!(parse_checksum(text, "two.tar.xz").as_deref(), Some("def"));
        assert_eq!(parse_checksum(text, "three.tar.xz"), None);
    }
}
