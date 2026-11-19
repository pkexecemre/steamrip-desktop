//! Port of the pure rules in SteamRip.Core.Data.Settings: where the profile lives, and how the
//! hoster order the settings window edits turns into names and hosters.

use crate::hoster::Hoster;

/// Where the profile lives, with the environment passed in so the rule is testable.
pub fn default_dir(
    windows: bool,
    app_data: &str,
    xdg_config_home: Option<&str>,
    home: &str,
) -> String {
    let base = if windows {
        app_data
    } else if let Some(xdg) = xdg_config_home.filter(|x| !x.is_empty()) {
        xdg
    } else {
        &format!("{}/.config", home.trim_end_matches('/'))
    };
    format!("{}/steamrip-desktop", base.trim_end_matches('/'))
}

/// Hoster names from the settings file, in order; an unknown name becomes Unknown.
pub fn hosters_from_names(names: &[String]) -> Vec<Hoster> {
    names
        .iter()
        .map(|name| match name.to_ascii_lowercase().as_str() {
            "buzzheavier" => Hoster::BuzzHeavier,
            "fileditch" => Hoster::FileDitch,
            "gofile" => Hoster::GoFile,
            "megadb" => Hoster::MegaDb,
            "fichier" => Hoster::Fichier,
            "filecrypt" => Hoster::FileCrypt,
            "unknown" => Hoster::Unknown,
            _ => Hoster::Unknown,
        })
        .collect()
}

/// The comma-separated field the settings window edits, back to names.
pub fn parse_hoster_order(text: &str) -> Vec<String> {
    text.split(',')
        .map(str::trim)
        .filter(|part| !part.is_empty())
        .map(str::to_string)
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn xdg_wins_over_home_on_linux() {
        assert_eq!(
            default_dir(false, "/appdata", Some("/xdg"), "/home/emre"),
            "/xdg/steamrip-desktop"
        );
        assert_eq!(
            default_dir(false, "/appdata", None, "/home/emre"),
            "/home/emre/.config/steamrip-desktop"
        );
    }

    #[test]
    fn unknown_hoster_names_are_kept_as_unknown() {
        let names = vec!["Nope".to_string(), "MegaDb".to_string()];
        assert_eq!(
            hosters_from_names(&names),
            vec![Hoster::Unknown, Hoster::MegaDb]
        );
    }
}
