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

/// Directories that are redist/content noise rather than the game itself.
pub const NOISE_DIRS: [&str; 5] = ["_commonredist", "_redist", "redist", "__macosx", "_setup"];

/// The last path segment, the way .NET's Path.GetFileName reports it: a path ending in a
/// separator has an empty name.
fn file_name(path: &str) -> String {
    match path.rsplit_once('/') {
        Some((_, name)) => name.to_string(),
        None => path.to_string(),
    }
}

/// The folder the staging directory should collapse into, or None to keep it as it is:
/// exactly one real folder and no loose files. Redist noise folders do not count.
pub fn collapse_target(directories: &[String], files: &[String]) -> Option<String> {
    let real: Vec<String> = directories
        .iter()
        .map(|d| file_name(d))
        .filter(|name| !NOISE_DIRS.contains(&name.to_lowercase().as_str()))
        .collect();
    if files.is_empty() && real.len() == 1 {
        Some(real[0].clone())
    } else {
        None
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
