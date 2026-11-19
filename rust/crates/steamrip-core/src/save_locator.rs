//! Port of the pure name rules in SteamRip.Core.Install.SaveLocator: which directories look like
//! saves, and which the walk must not descend into.

pub const SAVE_DIR_NAMES: [&str; 10] = [
    "Save",
    "Saves",
    "SaveGames",
    "SaveGame",
    "SaveData",
    "Savegames",
    "Profiles",
    "Saved",
    "UserData",
    "Savedata",
];

pub const EMU_SAVE_DIR_NAMES: [&str; 2] = ["GSE Saves", "Goldberg SteamEmu Saves"];

const SKIP_DIRS: [&str; 10] = [
    "_CommonRedist",
    "_Redist",
    "redist",
    "drive_c",
    "Engine",
    "Content",
    "Binaries",
    "Shaders",
    "Win64",
    "Win32",
];

const SKIP_BELOW_ROOT: [&str; 4] = ["Data", "data", "Resources", "Plugins"];

/// True for a directory name that looks like a save folder (exact list, or any Save prefix).
pub fn is_save_dir_name(name: &str) -> bool {
    SAVE_DIR_NAMES.iter().any(|n| n.eq_ignore_ascii_case(name))
        || name.to_lowercase().starts_with("save")
}

/// Directories the save walk skips: engine/redist bulk at any depth, and the deep-only ones.
pub fn is_skipped_dir(name: &str, depth: i32) -> bool {
    SKIP_DIRS.iter().any(|s| s.eq_ignore_ascii_case(name))
        || (depth > 0 && SKIP_BELOW_ROOT.iter().any(|s| s.eq_ignore_ascii_case(name)))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn save_prefix_counts() {
        assert!(is_save_dir_name("Save"));
        assert!(is_save_dir_name("savepoint"));
        assert!(is_save_dir_name("SAVES"));
        assert!(is_save_dir_name("UserData"));
        assert!(is_save_dir_name("Saver"));
        assert!(!is_save_dir_name("Data"));
        assert!(!is_save_dir_name("Profiles2"));
    }

    #[test]
    fn deep_only_skips_start_below_the_root() {
        assert!(!is_skipped_dir("Data", 0));
        assert!(is_skipped_dir("Data", 1));
        assert!(is_skipped_dir("Engine", 0));
    }
}
