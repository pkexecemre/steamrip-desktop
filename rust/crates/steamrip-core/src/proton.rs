//! Port of the keyword handling in SteamRip.Core.Launch.ProtonLocator: the two "latest of"
//! keywords and what a stored value actually means.

pub const GE_PROTON: &str = "GE-Proton";
pub const DW_PROTON: &str = "DW-Proton";

/// True when the stored value is a keyword rather than a build directory.
pub fn is_keyword(path: Option<&str>) -> bool {
    matches!(path, Some(GE_PROTON) | Some(DW_PROTON))
}

/// What the launcher uses for a stored value: empty means the GE-Proton keyword, never UMU-Proton.
pub fn effective(stored: Option<&str>) -> String {
    match stored {
        None => GE_PROTON.to_string(),
        Some("") => GE_PROTON.to_string(),
        Some(s) => s.to_string(),
    }
}

use regex::Regex;
use std::cmp::Ordering;
use std::sync::OnceLock;

fn digit_regex() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| Regex::new(r"\d+").expect("valid digits regex"))
}

fn display_name_regex() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| {
        Regex::new(r#""display_name"\s+"([^"]+)""#).expect("valid display name regex")
    })
}

/// Digits in order of appearance, so GE-Proton11-3 sorts above GE-Proton9-27.
pub fn version_key(name: &str) -> Vec<i32> {
    digit_regex()
        .find_iter(name)
        .filter_map(|m| m.as_str().parse::<i32>().ok())
        .collect()
}

/// Compares two version keys the way the build list is sorted: missing parts count as zero.
pub fn compare_versions(x: &[i32], y: &[i32]) -> Ordering {
    let len = x.len().max(y.len());
    for i in 0..len {
        let a = x.get(i).copied().unwrap_or(0);
        let b = y.get(i).copied().unwrap_or(0);
        match a.cmp(&b) {
            Ordering::Equal => continue,
            other => return other,
        }
    }
    Ordering::Equal
}

/// The newest build name: the first one when several share a version key.
pub fn newest_build_name(names: &[String]) -> Option<String> {
    let mut best: Option<(String, Vec<i32>)> = None;
    for name in names {
        let key = version_key(name);
        match &best {
            Some((_, best_key)) if compare_versions(&key, best_key) != Ordering::Greater => {}
            _ => best = Some((name.clone(), key)),
        }
    }
    best.map(|(name, _)| name)
}

/// The display name a Proton build advertises in its compatibilitytool.vdf.
pub fn display_name(compatibility_tool_vdf: &str) -> Option<String> {
    display_name_regex()
        .captures(compatibility_tool_vdf)
        .map(|c| c[1].to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn keywords_are_exact() {
        assert!(is_keyword(Some("GE-Proton")));
        assert!(is_keyword(Some("DW-Proton")));
        assert!(!is_keyword(Some("ge-proton")));
        assert!(!is_keyword(None));
    }

    #[test]
    fn empty_falls_back_to_ge() {
        assert_eq!(effective(None), "GE-Proton");
        assert_eq!(effective(Some("")), "GE-Proton");
        assert_eq!(effective(Some("/opt/proton")), "/opt/proton");
    }
}
