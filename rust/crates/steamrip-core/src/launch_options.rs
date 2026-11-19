//! Port of SteamRip.Core.Launch.LaunchOptions: the per-game launch tweaks and the merge that
//! decides which of them win over the app-wide defaults.

use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(default, rename_all = "PascalCase")]
pub struct LaunchOptions {
    pub mango_hud: Option<bool>,
    pub mango_hud_config: Option<String>,
    pub game_mode: Option<bool>,
    pub gamescope: Option<bool>,
    pub gamescope_args: Option<String>,
    pub hdr: Option<bool>,
    pub wayland: Option<bool>,
    pub fsr: Option<bool>,
    pub fsync: Option<bool>,
    pub proton_log: Option<bool>,
    pub dll_overrides: Option<String>,
    pub environment: Option<String>,
    pub wrapper: Option<String>,
}

impl LaunchOptions {
    /// What a fresh install gets: gamemode on (harmless when missing), everything else off.
    pub fn app_defaults() -> Self {
        Self {
            mango_hud: Some(false),
            game_mode: Some(true),
            gamescope: Some(false),
            gamescope_args: Some("-f".to_string()),
            hdr: Some(false),
            wayland: Some(false),
            fsr: Some(false),
            fsync: Some(true),
            proton_log: Some(false),
            ..Self::default()
        }
    }

    /// This record's set values win; blank text falls through to the defaults.
    pub fn merge_over(&self, defaults: &LaunchOptions) -> LaunchOptions {
        LaunchOptions {
            mango_hud: self.mango_hud.or(defaults.mango_hud),
            mango_hud_config: text(&self.mango_hud_config, &defaults.mango_hud_config),
            game_mode: self.game_mode.or(defaults.game_mode),
            gamescope: self.gamescope.or(defaults.gamescope),
            gamescope_args: text(&self.gamescope_args, &defaults.gamescope_args),
            hdr: self.hdr.or(defaults.hdr),
            wayland: self.wayland.or(defaults.wayland),
            fsr: self.fsr.or(defaults.fsr),
            fsync: self.fsync.or(defaults.fsync),
            proton_log: self.proton_log.or(defaults.proton_log),
            dll_overrides: text(&self.dll_overrides, &defaults.dll_overrides),
            environment: text(&self.environment, &defaults.environment),
            wrapper: text(&self.wrapper, &defaults.wrapper),
        }
    }
}

fn text(mine: &Option<String>, theirs: &Option<String>) -> Option<String> {
    match mine {
        Some(s) if !s.trim().is_empty() => Some(s.clone()),
        _ => theirs.clone(),
    }
}

/// One KEY=VALUE per line, with optional `export`, comments and quoted values.
pub fn parse_environment(text: Option<&str>) -> Vec<(String, String)> {
    let Some(raw) = text else {
        return Vec::new();
    };
    if raw.trim().is_empty() {
        return Vec::new();
    }
    let mut out = Vec::new();
    for line in raw.split('\n') {
        let mut line = line.trim();
        if line.is_empty() || line.starts_with('#') {
            continue;
        }
        if let Some(rest) = line.strip_prefix("export ") {
            line = rest.trim_start();
        }
        let Some(eq) = line.find('=') else {
            continue;
        };
        if eq == 0 {
            continue;
        }
        let key = line[..eq].trim();
        if key.is_empty() || !key.chars().all(|c| c.is_alphanumeric() || c == '_') {
            continue;
        }
        let value = line[eq + 1..].trim().trim_matches('"');
        out.push((key.to_string(), value.to_string()));
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn merge_prefers_set_values() {
        let defaults = LaunchOptions::app_defaults();
        let mine = LaunchOptions {
            game_mode: Some(false),
            ..LaunchOptions::default()
        };
        let merged = mine.merge_over(&defaults);
        assert_eq!(merged.game_mode, Some(false));
        assert_eq!(merged.fsync, Some(true));
    }

    #[test]
    fn parses_export_lines_and_quotes() {
        let entries = parse_environment(Some("export A=1\n# c\nB=\"two words\"\nbad key=1"));
        assert_eq!(
            entries,
            vec![
                ("A".to_string(), "1".to_string()),
                ("B".to_string(), "two words".to_string()),
            ]
        );
    }
}
