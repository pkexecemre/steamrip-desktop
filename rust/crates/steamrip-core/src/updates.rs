//! Port of SteamRip.Core.Updates.UpdateDetector: deciding whether a post is a new version of
//! what is installed.

use regex::Regex;
use std::sync::OnceLock;

fn build_prefix() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| Regex::new(r"(?i)build ").expect("valid"))
}

/// A version string with the noise the site adds removed: trimmed, and without any Build prefix.
pub fn normalize(value: Option<&str>) -> Option<String> {
    let value = value?;
    if value.trim().is_empty() {
        return None;
    }
    let stripped = build_prefix().replace_all(value.trim(), "");
    Some(stripped.trim().to_string())
}

/// True when the post's version key differs from what is installed. An unknown key never churns.
pub fn is_update(installed: Option<&str>, version_key: Option<&str>) -> bool {
    let Some(key) = version_key else {
        return false;
    };
    let current = normalize(Some(key));
    let installed = normalize(installed);
    match (current, installed) {
        (None, None) => false,
        (Some(a), Some(b)) => a.to_lowercase() != b.to_lowercase(),
        _ => true,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn strips_the_build_prefix_case_insensitively() {
        assert_eq!(
            normalize(Some(" Build 26315250 ")).as_deref(),
            Some("26315250")
        );
        assert_eq!(normalize(Some("build 12")).as_deref(), Some("12"));
        assert_eq!(normalize(Some("Build Build 5")).as_deref(), Some("5"));
        assert_eq!(normalize(Some("   ")), None);
        assert_eq!(normalize(None), None);
    }

    #[test]
    fn unknown_keys_never_churn() {
        assert!(!is_update(Some("v1"), None));
        assert!(!is_update(Some("v1"), Some("V1")));
        assert!(is_update(Some("v1"), Some("v2")));
    }
}
