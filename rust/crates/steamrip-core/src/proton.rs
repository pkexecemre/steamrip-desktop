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
