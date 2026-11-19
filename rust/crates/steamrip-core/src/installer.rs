//! Port of the pure naming rule in SteamRip.Core.Install.Installer.

/// A name safe to use as one path segment: invalid file-name characters and spaces become
/// underscores, and a blank result falls back to "unknown".
pub fn safe_name(value: &str) -> String {
    let cleaned: String = value
        .chars()
        .map(|c| {
            if c == '/' || c == '\0' || c == ' ' {
                '_'
            } else {
                c
            }
        })
        .collect();
    if cleaned.trim().is_empty() {
        "unknown".to_string()
    } else {
        cleaned
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn replaces_spaces_and_separators() {
        assert_eq!(safe_name("a b"), "a_b");
        assert_eq!(safe_name("a/b"), "a_b");
        assert_eq!(safe_name("Çok Özel"), "Çok_Özel");
        assert_eq!(safe_name(""), "unknown");
    }
}
