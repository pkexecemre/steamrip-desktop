//! Port of the pure rules in SteamRip.Core.Install.SaveLocator: which directories look like saves,
//! which the walk must not descend into, and the walk's decision over a snapshot of the tree.

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

/// One entry of a game-folder snapshot: a path relative to the root, and what it is.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TreeEntry {
    pub path: String,
    pub is_dir: bool,
    pub is_link: bool,
}

/// Save-like dirs inside a snapshot of the tree, in the order the directories are listed.
///
/// Save-named directories are selected before the depth cutoff, so a save folder sitting on
/// `max_depth` still counts; `max_depth` 0 and 1 both select only the root's own children.
pub fn find_in_game_folder(entries: &[TreeEntry], max_depth: i32) -> Vec<String> {
    let mut results = Vec::new();
    walk(entries, "", 0, max_depth, &mut results);
    results
}

fn walk(entries: &[TreeEntry], parent: &str, depth: i32, max_depth: i32, out: &mut Vec<String>) {
    for child in children(entries, parent) {
        if !child.is_dir || child.is_link {
            continue;
        }
        let name = leaf_name(&child.path);
        if is_save_dir_name(name) {
            out.push(child.path.clone());
            continue;
        }
        if depth + 1 >= max_depth {
            continue;
        }
        if is_skipped_dir(name, depth) {
            continue;
        }
        if is_wine_prefix(entries, &child.path) {
            continue;
        }
        walk(entries, &child.path, depth + 1, max_depth, out);
    }
}

/// Entries directly inside `parent`, keeping the snapshot's order.
fn children<'a>(entries: &'a [TreeEntry], parent: &'a str) -> impl Iterator<Item = &'a TreeEntry> {
    entries
        .iter()
        .filter(move |entry| parent_path(&entry.path) == parent)
}

fn leaf_name(path: &str) -> &str {
    path.rsplit('/').next().unwrap_or(path)
}

fn parent_path(path: &str) -> &str {
    match path.rfind('/') {
        Some(index) => &path[..index],
        None => "",
    }
}

/// A folder holding drive_c (a directory) or system.reg (a file) is a Wine prefix.
fn is_wine_prefix(entries: &[TreeEntry], dir: &str) -> bool {
    children(entries, dir).any(|entry| match leaf_name(&entry.path) {
        "drive_c" => entry.is_dir,
        "system.reg" => !entry.is_dir,
        _ => false,
    })
}

/// Where Goldberg/GSE keep their saves for one app id, for each user directory, in probe order.
pub fn emulator_save_paths(user_dirs: &[String], steam_app_id: i64) -> Vec<String> {
    let mut users: Vec<String> = Vec::new();
    for user in user_dirs {
        if !users.contains(user) {
            users.push(user.clone());
        }
    }
    let mut paths = Vec::new();
    for user in &users {
        for emu in EMU_SAVE_DIR_NAMES {
            paths.push(format!("{user}/AppData/Roaming/{emu}/{steam_app_id}"));
        }
    }
    paths
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

    fn dir(path: &str) -> TreeEntry {
        TreeEntry {
            path: path.into(),
            is_dir: true,
            is_link: false,
        }
    }

    fn file(path: &str) -> TreeEntry {
        TreeEntry {
            path: path.into(),
            is_dir: false,
            is_link: false,
        }
    }

    fn link(path: &str) -> TreeEntry {
        TreeEntry {
            path: path.into(),
            is_dir: true,
            is_link: true,
        }
    }

    #[test]
    fn save_at_the_depth_cap_is_still_selected() {
        let entries = vec![dir("a"), dir("a/b"), dir("a/b/c"), dir("a/b/c/Saves")];
        assert_eq!(find_in_game_folder(&entries, 4), ["a/b/c/Saves"]);
    }

    #[test]
    fn depth_zero_and_one_select_only_the_root_children() {
        let entries = vec![dir("Saves"), dir("Saves/Profiles"), dir("deep/Saves")];
        assert_eq!(find_in_game_folder(&entries, 0), ["Saves"]);
        assert_eq!(find_in_game_folder(&entries, 1), ["Saves"]);
    }

    #[test]
    fn deeper_than_the_cap_is_not_visited() {
        let entries = vec![
            dir("a"),
            dir("a/b"),
            dir("a/b/c"),
            dir("a/b/c/d"),
            dir("a/b/c/d/Saves"),
        ];
        assert_eq!(find_in_game_folder(&entries, 4), Vec::<String>::new());
    }

    #[test]
    fn a_wine_prefix_is_not_descended_into() {
        let entries = vec![
            dir("prefix"),
            dir("prefix/drive_c"),
            dir("prefix/Saves"),
            dir("ok"),
            dir("ok/Saves"),
        ];
        assert_eq!(find_in_game_folder(&entries, 4), ["ok/Saves"]);
    }

    #[test]
    fn system_reg_also_makes_a_prefix() {
        let entries = vec![
            dir("gog"),
            file("gog/system.reg"),
            dir("gog/Saves"),
            dir("plain"),
            dir("plain/Saves"),
        ];
        assert_eq!(find_in_game_folder(&entries, 4), ["plain/Saves"]);
    }

    #[test]
    fn a_file_named_drive_c_is_not_a_prefix() {
        let entries = vec![dir("tools"), file("tools/drive_c"), dir("tools/Saves")];
        assert_eq!(find_in_game_folder(&entries, 4), ["tools/Saves"]);
    }

    #[test]
    fn symlinked_directories_are_skipped() {
        let entries = vec![
            link("Saves"),
            dir("Saves/Profiles"),
            link("linked"),
            dir("linked/Saves"),
            dir("real"),
            dir("real/Saves"),
        ];
        assert_eq!(find_in_game_folder(&entries, 4), ["real/Saves"]);
    }

    #[test]
    fn deep_only_skips_apply_below_the_root() {
        let entries = vec![
            dir("Data"),
            dir("Data/Saves"),
            dir("game"),
            dir("game/Data"),
            dir("game/Data/Saves"),
        ];
        assert_eq!(find_in_game_folder(&entries, 4), ["Data/Saves"]);
    }

    #[test]
    fn redist_and_engine_dirs_are_skipped_at_any_depth() {
        let entries = vec![
            dir("Engine"),
            dir("Engine/Saves"),
            dir("x"),
            dir("x/_redist"),
            dir("x/_redist/Saves"),
            dir("keep"),
            dir("keep/Saves"),
        ];
        assert_eq!(find_in_game_folder(&entries, 4), ["keep/Saves"]);
    }

    #[test]
    fn a_selected_save_dir_is_not_descended_into() {
        let entries = vec![
            dir("Saves"),
            dir("Saves/Profiles"),
            dir("other"),
            dir("other/Saves"),
        ];
        assert_eq!(find_in_game_folder(&entries, 4), ["Saves", "other/Saves"]);
    }

    #[test]
    fn no_save_dirs_returns_nothing() {
        let entries = vec![dir("bin"), file("bin/game.bin"), dir("bin/x")];
        assert_eq!(find_in_game_folder(&entries, 4), Vec::<String>::new());
    }

    #[test]
    fn pre_order_is_kept_across_levels() {
        let entries = vec![
            dir("Saves"),
            dir("a"),
            dir("a/Profiles"),
            dir("b"),
            dir("b/UserData"),
        ];
        assert_eq!(
            find_in_game_folder(&entries, 4),
            ["Saves", "a/Profiles", "b/UserData"]
        );
    }
}
